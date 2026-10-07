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

    /// <summary>Analyses a file (or a section of it) with the given preset. Throws when the sidecar cannot answer.</summary>
    Task<SidecarScanResponse> AnalyseAsync(string audioFilePath, SidecarAnalysisRequest request, CancellationToken ct = default);
}

/// <summary>Reads and replaces the sidecar's active models while it runs.</summary>
public interface ISidecarModelControl
{
    Task<SidecarModelStatus> GetModelsAsync(CancellationToken ct = default);

    /// <summary>Loads the given models (stage to model name or path); stages that fail keep their previous model.</summary>
    Task<SidecarModelStatus> SetModelsAsync(IReadOnlyDictionary<string, string> models, CancellationToken ct = default);
}

/// <summary>Sidecar analysis settings. Detailed trades speed for quieter and shorter speech (sidecar/inference_server.py PRESETS).</summary>
public enum AnalysisPreset
{
    Standard,
    Detailed
}

/// <param name="StartSeconds">Start of the section to analyse; null with <paramref name="EndSeconds"/> null analyses the whole file.</param>
/// <param name="AudioTrackIndex">Audio stream of the file to analyse (0 = first).</param>
public sealed record SidecarAnalysisRequest(
    AnalysisPreset Preset = AnalysisPreset.Standard,
    double? StartSeconds = null,
    double? EndSeconds = null,
    int AudioTrackIndex = 0)
{
    public static SidecarAnalysisRequest Standard { get; } = new();

    public bool IsSection => StartSeconds.HasValue || EndSeconds.HasValue;
}

/// <summary>Model stages the sidecar can swap, in pipeline order.</summary>
public static class SidecarModelStages
{
    public const string Vad = "vad";
    public const string Diarization = "diarization";
    public const string Asr = "asr";
    public const string Alignment = "alignment";
    public const string Moderation = "moderation";

    public static IReadOnlyList<string> All { get; } = [Vad, Diarization, Asr, Alignment, Moderation];
}

public sealed class SidecarModelStatus
{
    /// <summary>Active model per stage.</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, string> Models { get; set; } = new();

    [JsonPropertyName("loaded")]
    public Dictionary<string, bool> Loaded { get; set; } = new();

    /// <summary>Last load error per stage; null when the stage loaded.</summary>
    [JsonPropertyName("errors")]
    public Dictionary<string, string?> Errors { get; set; } = new();

    /// <summary>False while the sidecar is still loading its models at start-up.</summary>
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    /// <summary>Changes whenever the sidecar's output for the same models can change; empty from older sidecars.</summary>
    [JsonPropertyName("pipeline_version")]
    public string PipelineVersion { get; set; } = string.Empty;
}

public sealed class SidecarScanResponse
{
    [JsonPropertyName("has_speech")]
    public bool HasSpeech { get; set; } = true;

    [JsonPropertyName("segments")]
    public List<DetectedSegment> Segments { get; set; } = new();

    /// <summary>Models that produced this response, per stage; null from sidecars that predate model swapping.</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, string>? Models { get; set; }
}

public sealed class SidecarHealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    public bool IsReady => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server is up and loading its models; requests wait until loading ends.</summary>
    public bool IsLoading => string.Equals(Status, "loading", StringComparison.OrdinalIgnoreCase);

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
public sealed class SidecarClient : IInferenceClient, ISidecarModelControl, IDisposable
{
    public const string DefaultBaseUrl = "http://localhost:54321";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Time allowed per second of audio before an analysis counts as timed out (half real time).</summary>
    public const double ProcessingSecondsPerAudioSecond = 2.0;

    private static readonly TimeSpan CancelNoticeTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
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
        _timeout = timeout ?? DefaultTimeout;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // Timeouts are applied per request: an analysis may run for longer than the default.
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = Timeout.InfiniteTimeSpan
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
    /// Pings the sidecar GET /health endpoint once; true only when it has finished loading its models.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default) =>
        (await GetHealthAsync(ct))?.IsReady == true;

    /// <summary>The sidecar's /health answer, or null when it does not answer.</summary>
    public async Task<SidecarHealthResponse?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = WithTimeout(_timeout, ct);
            using var response = await _httpClient.GetAsync($"{_baseUrl}/health", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                VoiceScanLogger.Warn(nameof(SidecarClient), $"Health check returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeout.Token);
            var health = JsonSerializer.Deserialize<SidecarHealthResponse>(json, JsonOptions);
            VoiceScanLogger.Debug(nameof(SidecarClient), $"Health check status: {health?.Status}, CUDA: {health?.CudaAvailable}, Device: {health?.DeviceName}");
            return health;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Debug(nameof(SidecarClient), $"Health check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Time allowed for analysing <paramref name="audioSeconds"/> of audio: never below <paramref name="floor"/>.</summary>
    public static TimeSpan ProcessingTimeout(double audioSeconds, TimeSpan floor) =>
        TimeSpan.FromSeconds(Math.Max(floor.TotalSeconds, Math.Max(0.0, audioSeconds) * ProcessingSecondsPerAudioSecond));

    private static CancellationTokenSource WithTimeout(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
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
                using var timeout = WithTimeout(_timeout, ct);
                using var response = await _httpClient.GetAsync($"{_baseUrl}/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(timeout.Token);
                    var health = JsonSerializer.Deserialize<SidecarHealthResponse>(json, JsonOptions);
                    if (health?.IsReady == true)
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
    public Task<SidecarScanResponse> ScanAudioAsync(string audioFilePath, CancellationToken ct = default) =>
        AnalyseAsync(audioFilePath, SidecarAnalysisRequest.Standard, ct);

    public async Task<SidecarScanResponse> AnalyseAsync(string audioFilePath, SidecarAnalysisRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            throw new ArgumentException("Audio file path cannot be null or empty.", nameof(audioFilePath));
        }

        string mappedPath = MapHostPathToContainerPath(audioFilePath);
        VoiceScanLogger.Info(nameof(SidecarClient), $"Submitting audio to sidecar ({request.Preset}, track {request.AudioTrackIndex}). Host: '{audioFilePath}' -> Container: '{mappedPath}'");

        string requestId = Guid.NewGuid().ToString("N");
        var requestBody = new ProcessRequest(
            mappedPath, request.Preset.ToString().ToLowerInvariant(), request.StartSeconds, request.EndSeconds,
            request.AudioTrackIndex, requestId);
        string jsonRequest = JsonSerializer.Serialize(requestBody, RequestJsonOptions);
        using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

        double audioSeconds = request.StartSeconds.HasValue && request.EndSeconds.HasValue
            ? request.EndSeconds.Value - request.StartSeconds.Value
            : await AudioDecoder.GetMediaDurationSecondsAsync(audioFilePath, ct) - (request.StartSeconds ?? 0.0);
        using var timeout = WithTimeout(ProcessingTimeout(audioSeconds, _timeout), ct);

        try
        {
            using var response = await _httpClient.PostAsync($"{_baseUrl}/process", content, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string errBody = await response.Content.ReadAsStringAsync(timeout.Token);
                VoiceScanLogger.Error(nameof(SidecarClient), $"Sidecar processing failed (HTTP {(int)response.StatusCode}): {errBody}");
                throw new HttpRequestException(
                    $"Sidecar returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {errBody}",
                    null,
                    response.StatusCode);
            }

            string jsonResponse = await response.Content.ReadAsStringAsync(timeout.Token);
            var result = JsonSerializer.Deserialize<SidecarScanResponse>(jsonResponse, JsonOptions);
            return result ?? new SidecarScanResponse { HasSpeech = false, Segments = new List<DetectedSegment>() };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            VoiceScanLogger.Warn(nameof(SidecarClient), $"Audio scan cancelled for: {audioFilePath}");
            await SendCancelAsync(requestId);
            throw;
        }
        catch (OperationCanceledException ex) // timeout
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"Sidecar request timed out for {audioFilePath}: {ex.Message}");
            await SendCancelAsync(requestId);
            throw new TimeoutException($"Sidecar request timed out while processing '{audioFilePath}'.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            VoiceScanLogger.Error(nameof(SidecarClient), $"Network error contacting sidecar at {_baseUrl}: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>Tells the sidecar to stop (or skip) a request the engine no longer waits for. Best effort.</summary>
    private async Task SendCancelAsync(string requestId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CancelNoticeTimeout);
            using var body = new StringContent(
                JsonSerializer.Serialize(new CancelRequest(requestId)), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{_baseUrl}/cancel", body, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            VoiceScanLogger.Debug(nameof(SidecarClient), $"Could not send cancel for request {requestId}: {ex.Message}");
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

    public async Task<SidecarModelStatus> GetModelsAsync(CancellationToken ct = default)
    {
        using var timeout = WithTimeout(_timeout, ct);
        using var response = await _httpClient.GetAsync($"{_baseUrl}/models", timeout.Token);
        return await ReadModelStatusAsync(response, timeout.Token);
    }

    public async Task<SidecarModelStatus> SetModelsAsync(IReadOnlyDictionary<string, string> models, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(new { models });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        // Loading a large model can take minutes; the client timeout (15 min by default) bounds it.
        using var timeout = WithTimeout(_timeout, ct);
        using var response = await _httpClient.PutAsync($"{_baseUrl}/models", content, timeout.Token);
        return await ReadModelStatusAsync(response, timeout.Token);
    }

    private static async Task<SidecarModelStatus> ReadModelStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Sidecar returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {body}", null, response.StatusCode);
        }
        return JsonSerializer.Deserialize<SidecarModelStatus>(body, JsonOptions) ?? new SidecarModelStatus();
    }

    private sealed record ProcessRequest(
        [property: JsonPropertyName("audio_path")] string AudioPath,
        [property: JsonPropertyName("preset")] string Preset,
        [property: JsonPropertyName("start_seconds")] double? StartSeconds,
        [property: JsonPropertyName("end_seconds")] double? EndSeconds,
        [property: JsonPropertyName("audio_track")] int AudioTrack,
        [property: JsonPropertyName("request_id")] string RequestId);

    private sealed record CancelRequest([property: JsonPropertyName("request_id")] string RequestId);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
