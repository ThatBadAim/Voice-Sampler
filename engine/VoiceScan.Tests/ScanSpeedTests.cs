namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;
using Xunit;

/// <summary>docs/SPEC-scan-speed-accuracy.md: sidecar answer cache, concurrent sidecar, request ids, timeouts.</summary>
public sealed class ScanSpeedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"voicescan_speed_{Guid.NewGuid():N}");

    public ScanSpeedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SidecarScanResponse OneLine() => new()
    {
        HasSpeech = true,
        Segments = [new DetectedSegment(0.2, 1.8, 0.9, speakerLabel: "SPEAKER_00", transcript: "rotate to B")]
    };

    private static VoiceProfile ProfileFor(ISpeakerEmbeddingModel model) => new()
    {
        ProfileName = "Target",
        ModelId = model.ModelId,
        ModelVersion = model.ModelVersion,
        Centroid = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f]
    };

    [Fact]
    public async Task Scanner_ReusesCachedSidecarAnswer_UntilModelsChange()
    {
        using var db = new VoiceScanDatabase(Path.Combine(_dir, "scan.db"));
        await db.InitializeAsync();
        using var model = new FakeEmbeddingModel("fake");
        var sidecar = new FakeSidecar { Respond = _ => OneLine() };
        var scanner = new PipelineScanner(model, new WebRtcVad(), db, sidecarClient: sidecar);
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "clip.wav"), 2.0);

        var first = await scanner.ScanFileAsync(wav, ProfileFor(model), threshold: 0.5);
        var second = await scanner.ScanFileAsync(wav, ProfileFor(model), threshold: 0.5);

        Assert.Single(sidecar.Requests);
        Assert.Equal("rotate to B", Assert.Single(first.Segments).Transcript);
        Assert.Equal("rotate to B", Assert.Single(second.Segments).Transcript);
        Assert.True(second.AnalyzerUsed);

        await sidecar.SetModelsAsync(new Dictionary<string, string> { ["asr"] = "large-v3" });
        await scanner.ScanFileAsync(wav, ProfileFor(model), threshold: 0.5);
        Assert.Equal(2, sidecar.Requests.Count);
    }

    [Fact]
    public async Task Scanner_DoesNotCacheWhileSidecarIsLoading()
    {
        using var db = new VoiceScanDatabase(Path.Combine(_dir, "scan.db"));
        await db.InitializeAsync();
        using var model = new FakeEmbeddingModel("fake");
        var sidecar = new FakeSidecar { Respond = _ => OneLine(), Ready = false };
        var scanner = new PipelineScanner(model, new WebRtcVad(), db, sidecarClient: sidecar);
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "clip.wav"), 2.0);

        await scanner.ScanFileAsync(wav, ProfileFor(model), threshold: 0.5);
        await scanner.ScanFileAsync(wav, ProfileFor(model), threshold: 0.5);

        Assert.Equal(2, sidecar.Requests.Count);
    }

    [Fact]
    public async Task Scanner_RunsSidecarWhileDecoding_AndAppliesItsAnswer()
    {
        using var model = new FakeEmbeddingModel("fake");
        var decodeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The sidecar answers only after decoding has reported progress: a scanner that waits for the sidecar
        // before decoding would never finish.
        var sidecar = new GatedSidecar(decodeStarted.Task, OneLine());
        var scanner = new PipelineScanner(model, new WebRtcVad(), sidecarClient: sidecar);
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "clip.wav"), 2.0);

        var result = await scanner.ScanFileAsync(
            wav, ProfileFor(model), threshold: 0.5,
            reportProgress: f => { if (f > 0.0) decodeStarted.TrySetResult(); })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("rotate to B", Assert.Single(result.Segments).Transcript);
        Assert.Equal(0, sidecar.Request!.AudioTrackIndex);
    }

    [Fact]
    public async Task Scanner_NoSpeechAnswerAfterDecode_KeepsWaveform()
    {
        using var model = new FakeEmbeddingModel("fake");
        var sidecar = new FakeSidecar { Respond = _ => new SidecarScanResponse { HasSpeech = false } };
        var scanner = new PipelineScanner(model, new WebRtcVad(), sidecarClient: sidecar);
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "clip.wav"), 2.0);

        var result = await scanner.ScanFileAsync(wav, ProfileFor(model), threshold: 0.5);

        Assert.Contains("NO_SPEECH_DETECTED", result.ReasonFlags);
        Assert.Empty(result.Segments);
        Assert.NotNull(result.WaveformMaxPeaks);
        Assert.Equal(2.0, result.DurationSeconds, 2);
    }

    [Fact]
    public async Task SidecarClient_SendsTrackAndRequestId_AndCancelsTheSameRequest()
    {
        var handler = new BlockingProcessHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(SidecarClient.DefaultBaseUrl) };
        using var client = new SidecarClient(http);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.AnalyseAsync("/rec/missing.mkv", new SidecarAnalysisRequest(AudioTrackIndex: 2), cts.Token));

        var process = Assert.Single(handler.Requests, r => r.Path == "/process");
        using var body = JsonDocument.Parse(process.Body);
        Assert.Equal(2, body.RootElement.GetProperty("audio_track").GetInt32());
        string requestId = body.RootElement.GetProperty("request_id").GetString()!;
        Assert.False(string.IsNullOrEmpty(requestId));

        var cancel = Assert.Single(handler.Requests, r => r.Path == "/cancel");
        using var cancelBody = JsonDocument.Parse(cancel.Body);
        Assert.Equal(requestId, cancelBody.RootElement.GetProperty("request_id").GetString());
    }

    [Fact]
    public void ProcessingTimeout_GrowsWithAudioLength_AndNeverDropsBelowTheFloor()
    {
        var floor = TimeSpan.FromMinutes(15);
        Assert.Equal(floor, SidecarClient.ProcessingTimeout(60, floor));
        Assert.Equal(floor, SidecarClient.ProcessingTimeout(0, floor));
        Assert.Equal(TimeSpan.FromHours(6), SidecarClient.ProcessingTimeout(3 * 3600, floor));
    }

    [Fact]
    public async Task SidecarClient_LoadingHealth_IsUpButNotReady()
    {
        var handler = new RecordingHandler("{\"status\":\"loading\",\"cuda_available\":true}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(SidecarClient.DefaultBaseUrl) };
        using var client = new SidecarClient(http);

        var health = await client.GetHealthAsync();

        Assert.True(health!.IsLoading);
        Assert.False(health.IsReady);
        Assert.False(await client.CheckHealthAsync());
    }

    private sealed class GatedSidecar(Task gate, SidecarScanResponse answer) : IInferenceClient
    {
        public SidecarAnalysisRequest? Request { get; private set; }

        public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default) =>
            await AnalyseAsync(filePath, SidecarAnalysisRequest.Standard, ct);

        public async Task<SidecarScanResponse> AnalyseAsync(string audioFilePath, SidecarAnalysisRequest request, CancellationToken ct = default)
        {
            Request = request;
            await gate.WaitAsync(ct);
            return answer;
        }
    }

    /// <summary>Holds /process until the caller cancels; answers everything else with an empty JSON object.</summary>
    private sealed class BlockingProcessHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string Path, string Body)> _requests = [];

        public List<(string Path, string Body)> Requests
        {
            get { lock (_gate) return [.. _requests]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            lock (_gate) _requests.Add((request.RequestUri!.AbsolutePath, body));
            if (request.RequestUri.AbsolutePath == "/process")
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
