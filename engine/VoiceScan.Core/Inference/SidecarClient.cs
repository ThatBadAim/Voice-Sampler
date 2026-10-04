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
    Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default);
}

public sealed class SidecarScanResponse
{
    [JsonPropertyName("has_speech")]
    public bool HasSpeech { get; set; }

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
}

/// <summary>
/// HTTP client communicating with the local VoiceScan Python inference sidecar service.
/// </summary>
public sealed class SidecarClient : IInferenceClient, IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:54321";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;

    public SidecarClient(HttpClient? httpClient = null, string baseUrl = DefaultBaseUrl, TimeSpan? timeout = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
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
            var health = JsonSerializer.Deserialize<SidecarHealthResponse>(json);
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

    public async Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        string fullPath = Path.GetFullPath(filePath);
        VoiceScanLogger.Info(nameof(SidecarClient), $"Submitting audio to sidecar for processing: {fullPath}");

        try
        {
            var requestBody = new { audio_path = fullPath };
            string jsonRequest = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync($"{_baseUrl}/process", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                string errBody = await response.Content.ReadAsStringAsync(ct);
                VoiceScanLogger.Error(nameof(SidecarClient), $"Sidecar processing failed with HTTP {(int)response.StatusCode}: {errBody}");
                return null;
            }

            string jsonResponse = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SidecarScanResponse>(jsonResponse);
            VoiceScanLogger.Info(nameof(SidecarClient), $"Sidecar processing completed. HasSpeech: {result?.HasSpeech}, Segments: {result?.Segments.Count ?? 0}");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            VoiceScanLogger.Warn(nameof(SidecarClient), $"Audio processing cancelled for: {fullPath}");
            throw;
        }
        catch (HttpRequestException ex)
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"Network error communicating with sidecar at {_baseUrl}: {ex.Message}", ex);
            return null;
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"Unexpected error processing audio via sidecar: {ex.Message}", ex);
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
