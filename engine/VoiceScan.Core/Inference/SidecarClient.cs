namespace VoiceScan.Core.Inference;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core.Logging;

public interface IInferenceClient
{
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
    Task<bool> IsReadyAsync(CancellationToken ct = default) => CheckHealthAsync(ct);
    Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default);
    Task<SidecarScanResponse> ScanAudioAsync(string audioFilePath, CancellationToken ct = default) =>
        ProcessAudioAsync(audioFilePath, ct).ContinueWith(t => t.Result ?? new SidecarScanResponse(), TaskScheduler.Default);
}

public sealed class SidecarScanResponse
{
    [JsonPropertyName("has_speech")]
    public bool HasSpeech { get; set; } = true;

    [JsonPropertyName("segments")]
    public List<DetectedSegment> Segments { get; set; } = new();
}

public sealed class SidecarHealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cuda_available")]
    public bool CudaAvailable { get; set; }

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("vram_free_gb")]
    public double VramFreeGb { get; set; }

    [JsonPropertyName("vram_total_gb")]
    public double VramTotalGb { get; set; }
}

/// <summary>
/// HTTP client communicating with the VoiceScan Python Docker sidecar worker.
/// </summary>
public sealed class SidecarClient : IInferenceClient, IDisposable
{
    public const string DefaultBaseUrl = "http://localhost:54321";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _hostPrefix;
    private readonly string _containerPrefix;

    public SidecarClient(
        HttpClient? httpClient = null,
        string baseUrl = DefaultBaseUrl,
        TimeSpan? timeout = null,
        string hostPrefix = @"C:\AudioData",
        string containerPrefix = "/data")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _hostPrefix = hostPrefix;
        _containerPrefix = containerPrefix;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = timeout ?? DefaultTimeout
            };
            _ownsHttpClient = true;
        }
    }

    /// <summary>
    /// Translates host audio paths (e.g. C:\AudioData\sample.wav) to Docker container volume paths (/data/sample.wav).
    /// </summary>
    public string MapHostPathToContainerPath(string hostPath)
    {
        return MapHostPath(hostPath, _hostPrefix, _containerPrefix);
    }

    public static string MapHostPath(string hostPath, string hostPrefix = @"C:\AudioData", string containerPrefix = "/data")
    {
        if (string.IsNullOrWhiteSpace(hostPath)) return hostPath;

        string normalizedHost = hostPath.Replace('/', '\\');
        string normalizedPrefix = hostPrefix.TrimEnd('\\', '/') + '\\';

        if (normalizedHost.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relative = normalizedHost.Substring(normalizedPrefix.Length).Replace('\\', '/');
            return $"{containerPrefix.TrimEnd('/')}/{relative}";
        }

        string fwdHost = hostPath.Replace('\\', '/');
        string fwdPrefix = hostPrefix.Replace('\\', '/').TrimEnd('/') + '/';
        if (fwdHost.StartsWith(fwdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relative = fwdHost.Substring(fwdPrefix.Length);
            return $"{containerPrefix.TrimEnd('/')}/{relative}";
        }

        return hostPath.Replace('\\', '/');
    }

    /// <summary>
    /// Pings the sidecar GET /health endpoint once.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_baseUrl}/health", ct);
            if (!response.IsSuccessStatusCode)
            {
                VoiceScanLogger.Warn(nameof(SidecarClient), $"Health check returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            var health = JsonSerializer.Deserialize<SidecarHealthResponse>(json, JsonOptions);
            bool isReady = health != null && string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase);
            VoiceScanLogger.Debug(nameof(SidecarClient), $"Health check result: {isReady}, CUDA: {health?.CudaAvailable}, Device: {health?.DeviceName}");
            return isReady;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Debug(nameof(SidecarClient), $"Health check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks whether the sidecar worker is up and ready with retry backoff to ping /health.
    /// </summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        return await IsReadyWithBackoffAsync(maxRetries: 5, initialDelayMs: 200, ct: ct);
    }

    public async Task<bool> IsReadyWithBackoffAsync(int maxRetries, int initialDelayMs, CancellationToken ct = default)
    {
        int delay = initialDelayMs;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/health", ct);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(ct);
                    var health = JsonSerializer.Deserialize<SidecarHealthResponse>(json, JsonOptions);
                    if (health != null && string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        VoiceScanLogger.Debug(nameof(SidecarClient), $"Sidecar ready on attempt {attempt}.");
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                VoiceScanLogger.Debug(nameof(SidecarClient), $"Health check attempt {attempt}/{maxRetries} failed: {ex.Message}");
            }

            if (attempt < maxRetries)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                delay = Math.Min(delay * 2, 5000);
            }
        }

        VoiceScanLogger.Warn(nameof(SidecarClient), $"Sidecar not ready after {maxRetries} attempts.");
        return false;
    }

    /// <summary>
    /// Submits an audio file to the sidecar worker for VAD, diarization, transcription, and moderation analysis.
    /// </summary>
    public async Task<SidecarScanResponse> ScanAudioAsync(string audioFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            throw new ArgumentException("Audio file path cannot be null or empty.", nameof(audioFilePath));
        }

        string mappedPath = MapHostPathToContainerPath(audioFilePath);
        VoiceScanLogger.Info(nameof(SidecarClient), $"Submitting audio to sidecar. Host: '{audioFilePath}' -> Container: '{mappedPath}'");

        var requestBody = new { audio_path = mappedPath };
        string jsonRequest = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync($"{_baseUrl}/process", content, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            VoiceScanLogger.Warn(nameof(SidecarClient), $"Audio scan cancelled for: {audioFilePath}");
            throw;
        }
        catch (OperationCanceledException ex) // HttpClient timeout
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"Sidecar request timed out for {audioFilePath}: {ex.Message}");
            throw new TimeoutException($"Sidecar request timed out while processing '{audioFilePath}'.", ex);
        }
        catch (HttpRequestException ex)
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"Network error contacting sidecar at {_baseUrl}: {ex.Message}", ex);
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string errBody = await response.Content.ReadAsStringAsync(ct);
                VoiceScanLogger.Error(nameof(SidecarClient), $"Sidecar processing failed (HTTP {(int)response.StatusCode}): {errBody}");
                throw new HttpRequestException(
                    $"Sidecar returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {errBody}",
                    null,
                    response.StatusCode);
            }

            string jsonResponse = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SidecarScanResponse>(jsonResponse, JsonOptions);
            return result ?? new SidecarScanResponse { HasSpeech = false, Segments = new List<DetectedSegment>() };
        }
    }

    public async Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        try
        {
            return await ScanAudioAsync(filePath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"ProcessAudioAsync error for {filePath}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
