namespace VoiceScan.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core.Inference;
using Xunit;

public class SidecarInferenceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task SidecarClient_CheckHealth_ReturnsTrueOnReadyResponse()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.EndsWith("/health", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\": \"ready\", \"cuda_available\": true, \"device_name\": \"NVIDIA GeForce RTX 4090\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        bool ready = await client.CheckHealthAsync();
        Assert.True(ready);
    }

    [Fact]
    public async Task SidecarClient_CheckHealth_ReturnsFalseOnFailureOrException()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        bool ready = await client.CheckHealthAsync();
        Assert.False(ready);
    }

    [Fact]
    public async Task SidecarClient_ProcessAudio_ParsesSegmentsAndPropertiesCleanly()
    {
        string fakeAudio = Path.GetTempFileName();
        try
        {
            var handler = new MockHttpMessageHandler(req =>
            {
                Assert.EndsWith("/process", req.RequestUri!.AbsolutePath);
                Assert.Equal(HttpMethod.Post, req.Method);

                string responseJson = @"{
                    ""has_speech"": true,
                    ""segments"": [
                        {
                            ""start_time_seconds"": 0.5,
                            ""end_time_seconds"": 2.8,
                            ""confidence"": 0.95,
                            ""verdict"": ""Match"",
                            ""reason_flags"": [],
                            ""speaker_label"": ""SPEAKER_00"",
                            ""transcript"": ""Enemy spotted near mid"",
                            ""is_offensive"": false,
                            ""moderation_violations"": []
                        },
                        {
                            ""start_time_seconds"": 3.1,
                            ""end_time_seconds"": 4.2,
                            ""confidence"": 0.88,
                            ""verdict"": ""Match"",
                            ""reason_flags"": [],
                            ""speaker_label"": ""SPEAKER_01"",
                            ""transcript"": ""Abusive insult detected"",
                            ""is_offensive"": true,
                            ""moderation_violations"": [""harassment""]
                        }
                    ]
                }";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            using var client = new SidecarClient(httpClient);

            var response = await client.ProcessAudioAsync(fakeAudio);
            Assert.NotNull(response);
            Assert.True(response.HasSpeech);
            Assert.Equal(2, response.Segments.Count);

            var s0 = response.Segments[0];
            Assert.Equal(0.5, s0.StartTimeSeconds);
            Assert.Equal(2.8, s0.EndTimeSeconds);
            Assert.Equal("SPEAKER_00", s0.SpeakerLabel);
            Assert.Equal("Enemy spotted near mid", s0.Transcript);
            Assert.False(s0.IsOffensive);
            Assert.Empty(s0.ModerationViolations);

            var s1 = response.Segments[1];
            Assert.Equal("SPEAKER_01", s1.SpeakerLabel);
            Assert.Equal("Abusive insult detected", s1.Transcript);
            Assert.True(s1.IsOffensive);
            Assert.Single(s1.ModerationViolations);
            Assert.Equal("harassment", s1.ModerationViolations[0]);
        }
        finally
        {
            if (File.Exists(fakeAudio)) File.Delete(fakeAudio);
        }
    }

    [Fact]
    public async Task SidecarClient_ProcessAudio_ThrowsOnInvalidPath()
    {
        using var client = new SidecarClient();
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProcessAudioAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ScanAudioAsync(""));
    }

    [Fact]
    public void SidecarClient_PathTranslation_MapsHostToContainer()
    {
        string hostPath1 = @"C:\AudioData\sample.wav";
        Assert.Equal("/data/sample.wav", SidecarClient.MapHostPath(hostPath1));

        string hostPath2 = @"C:\AudioData\nested\subfolder\recording.mkv";
        Assert.Equal("/data/nested/subfolder/recording.mkv", SidecarClient.MapHostPath(hostPath2));

        string fwdPath = "C:/AudioData/match1.wav";
        Assert.Equal("/data/match1.wav", SidecarClient.MapHostPath(fwdPath));

        string alreadyMapped = "/data/already.wav";
        Assert.Equal("/data/already.wav", SidecarClient.MapHostPath(alreadyMapped));
    }

    [Fact]
    public async Task SidecarClient_IsReadyAsync_RetriesWithBackoffAndSucceeds()
    {
        int attempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempts++;
            if (attempts < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ready\",\"cuda_available\":true}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        bool ready = await client.IsReadyWithBackoffAsync(maxRetries: 4, initialDelayMs: 10);
        Assert.True(ready);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task SidecarClient_IsReadyAsync_ReturnsFalseWhenRetriesExhausted()
    {
        int attempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        bool ready = await client.IsReadyWithBackoffAsync(maxRetries: 3, initialDelayMs: 10);
        Assert.False(ready);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task SidecarClient_ScanAudioAsync_TranslatesPathInRequestBody()
    {
        string? sentAudioPath = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            sentAudioPath = doc.RootElement.GetProperty("audio_path").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"has_speech\":true,\"segments\":[]}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        var response = await client.ScanAudioAsync(@"C:\AudioData\tournaments\final.wav");
        Assert.NotNull(response);
        Assert.Equal("/data/tournaments/final.wav", sentAudioPath);
    }

    [Fact]
    public async Task SidecarClient_ScanAudioAsync_HandlesHttp500ErrorGracefully()
    {
        var handler = new MockHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Out of VRAM or CUDA kernel fault", Encoding.UTF8, "text/plain")
            });

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ScanAudioAsync(@"C:\AudioData\fail.wav"));
        Assert.Contains("500", ex.Message);
        Assert.Contains("CUDA kernel fault", ex.Message);

        // ProcessAudioAsync catches it and logs, returning null
        var fallback = await client.ProcessAudioAsync(@"C:\AudioData\fail.wav");
        Assert.Null(fallback);
    }

    [Fact]
    public async Task SidecarClient_ScanAudioAsync_HandlesTimeoutAndRecovers()
    {
        bool firstCall = true;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (firstCall)
            {
                firstCall = false;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 15 minutes.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"has_speech\":true,\"segments\":[]}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new SidecarClient(httpClient);

        // First attempt times out
        await Assert.ThrowsAsync<TimeoutException>(() => client.ScanAudioAsync(@"C:\AudioData\long.wav"));

        // Second attempt recovers when server is responsive
        var recovered = await client.ScanAudioAsync(@"C:\AudioData\recovered.wav");
        Assert.NotNull(recovered);
        Assert.True(recovered.HasSpeech);
    }

    [Fact]
    public void SidecarManager_LocateInferenceScript_FindsScriptInRepo()
    {
        string? scriptPath = SidecarManager.LocateInferenceScript();
        Assert.NotNull(scriptPath);
        Assert.True(File.Exists(scriptPath));
        Assert.EndsWith("inference_server.py", scriptPath);

        string scriptDir = Path.GetDirectoryName(scriptPath)!;
        string python = SidecarManager.LocatePythonExecutable(scriptDir);
        Assert.False(string.IsNullOrWhiteSpace(python));
    }

    [Fact]
    public async Task SidecarManager_LifecycleAndDispose_TerminatesCleanly()
    {
        // Test manager construction with a dummy port to verify it reports down and disposes cleanly
        await using var manager = new SidecarManager(baseUrl: "http://127.0.0.1:59999", startupTimeout: TimeSpan.FromMilliseconds(100));
        bool isHealthy = await manager.CheckHealthAsync();
        Assert.False(isHealthy);
    }

    private sealed class MockEmbeddingModel : VoiceScan.Core.ISpeakerEmbeddingModel
    {
        public string ModelId => "mock_model";
        public string ModelVersion => "mock_model@v1";
        public int EmbeddingDimension => 4;
        public string ActiveProvider => "CPU";
        public bool IsCudaActive => false;
        public VoiceScan.Core.ModelOperatingPoint OperatingPoint => new(0.5, 0.4);

        public float[] ExtractEmbedding(float[] audioWindow) => new float[] { 1f, 0f, 0f, 0f };
        public float[][] ExtractEmbeddingsBatch(System.Collections.Generic.IReadOnlyList<float[]> audioWindows) =>
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(audioWindows, _ => new float[] { 1f, 0f, 0f, 0f }));
        public void Dispose() { }
    }

    private sealed class MockInferenceClient : IInferenceClient
    {
        private readonly Func<string, SidecarScanResponse?> _responder;

        public MockInferenceClient(Func<string, SidecarScanResponse?> responder)
        {
            _responder = responder;
        }

        public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(_responder(filePath));
    }

    private static string CreateDummyWav(int sampleCount = 16000)
    {
        string path = Path.GetTempFileName() + ".wav";
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        int byteRate = 16000 * 2;
        int dataChunkSize = sampleCount * 2;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataChunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // 1 channel
        bw.Write(16000); // sample rate
        bw.Write(byteRate);
        bw.Write((short)2); // block align
        bw.Write((short)16); // 16-bit
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataChunkSize);

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 10000);
            bw.Write(sample);
        }

        return path;
    }

    [Fact]
    public async Task PipelineScanner_SidecarNoSpeech_ReturnsZeroSegmentsWithReasonFlag()
    {
        string wavPath = CreateDummyWav(16000);
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_nospeech_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new VoiceScan.Core.Storage.VoiceScanDatabase(tempDb);
            await db.InitializeAsync();

            var sidecarMock = new MockInferenceClient(_ => new SidecarScanResponse
            {
                HasSpeech = false,
                Segments = new System.Collections.Generic.List<VoiceScan.Core.DetectedSegment>()
            });

            using var model = new MockEmbeddingModel();
            var vad = new VoiceScan.Core.WebRtcVad();
            var scanner = new VoiceScan.Core.PipelineScanner(model, vad, database: db, sidecarClient: sidecarMock);

            var profile = new VoiceScan.Core.VoiceProfile
            {
                ProfileName = "TargetSpeaker",
                ModelId = model.ModelId,
                ModelVersion = model.ModelVersion,
                Centroid = new float[] { 1f, 0f, 0f, 0f }
            };

            var result = await scanner.ScanFileAsync(wavPath, profile, threshold: 0.5);

            Assert.NotNull(result);
            Assert.Empty(result.Segments);
            Assert.Contains("NO_SPEECH_DETECTED", result.ReasonFlags);
            Assert.Equal("No match", result.Verdict);
            Assert.Equal(0.0, result.MaxConfidence);

            // Verify stored in DB
            var stored = await db.GetScanResultAsync(result.FileHash, profile.ProfileName, model.ModelVersion, 0.5);
            Assert.NotNull(stored);
            Assert.Equal("No match", stored.Verdict);
            Assert.Equal(0.0, stored.MaxConfidence);
        }
        finally
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task PipelineScanner_SidecarSpeech_MapsSegmentsAndScoresProfile()
    {
        string wavPath = CreateDummyWav(32000);
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_speech_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new VoiceScan.Core.Storage.VoiceScanDatabase(tempDb);
            await db.InitializeAsync();

            var sidecarMock = new MockInferenceClient(_ => new SidecarScanResponse
            {
                HasSpeech = true,
                Segments = new System.Collections.Generic.List<VoiceScan.Core.DetectedSegment>
                {
                    new VoiceScan.Core.DetectedSegment
                    {
                        StartTimeSeconds = 0.2,
                        EndTimeSeconds = 1.8,
                        Confidence = 0.90,
                        Verdict = "Match",
                        SpeakerLabel = "SPEAKER_00",
                        Transcript = "Enemy spotted on the flank",
                        IsOffensive = true,
                        ModerationViolations = new[] { "harassment" }
                    }
                }
            });

            using var model = new MockEmbeddingModel();
            var vad = new VoiceScan.Core.WebRtcVad();
            var scanner = new VoiceScan.Core.PipelineScanner(model, vad, database: db, sidecarClient: sidecarMock);

            var profile = new VoiceScan.Core.VoiceProfile
            {
                ProfileName = "TargetSpeaker",
                ModelId = model.ModelId,
                ModelVersion = model.ModelVersion,
                Centroid = new float[] { 1f, 0f, 0f, 0f }
            };

            var result = await scanner.ScanFileAsync(wavPath, profile, threshold: 0.5);

            Assert.NotNull(result);
            Assert.Single(result.Segments);
            var seg = result.Segments[0];
            Assert.Equal("SPEAKER_00", seg.SpeakerLabel);
            Assert.Equal("Enemy spotted on the flank", seg.Transcript);
            Assert.True(seg.IsOffensive);
            Assert.Single(seg.ModerationViolations);
            Assert.Equal("harassment", seg.ModerationViolations[0]);
            Assert.Equal("Match", seg.Verdict);
            Assert.True(seg.Confidence >= 0.5);

            // Verify stored in DB
            var stored = await db.GetScanResultAsync(result.FileHash, profile.ProfileName, model.ModelVersion, 0.5);
            Assert.NotNull(stored);
            Assert.Equal("SPEAKER_00", stored.SpeakerLabel);
            Assert.Equal("Enemy spotted on the flank", stored.Transcript);
            Assert.True(stored.IsOffensive);
            Assert.NotNull(stored.ModerationViolations);
            Assert.Single(stored.ModerationViolations);
            Assert.Equal("harassment", stored.ModerationViolations[0]);
        }
        finally
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public void HitSegmentResult_IsFlagged_ReturnsTrueWhenViolationsOrOffensive()
    {
        var seg1 = new VoiceScan.App.Core.Models.HitSegmentResult(
            SegmentId: "seg1",
            FilePath: "sample.wav",
            StartTimeSeconds: 0.0,
            EndTimeSeconds: 2.0,
            DurationSeconds: 2.0,
            Verdict: "Match",
            Confidence: 0.9,
            ReasonFlags: Array.Empty<string>(),
            IsOffensive: false,
            ModerationViolations: new[] { "threat" });

        Assert.True(seg1.IsFlagged);
        Assert.Equal("threat", seg1.ViolationsSummary);

        var seg2 = new VoiceScan.App.Core.Models.HitSegmentResult(
            SegmentId: "seg2",
            FilePath: "sample.wav",
            StartTimeSeconds: 0.0,
            EndTimeSeconds: 2.0,
            DurationSeconds: 2.0,
            Verdict: "Match",
            Confidence: 0.9,
            ReasonFlags: Array.Empty<string>(),
            IsOffensive: true,
            ModerationViolations: Array.Empty<string>());

        Assert.True(seg2.IsFlagged);
        Assert.Equal("Violation", seg2.ViolationsSummary);
    }

    [Fact]
    public async Task SidecarClient_ProcessAudio_ParsesDetoxifyScoresAndSnakeCaseKeys()
    {
        string fakeAudio = Path.GetTempFileName();
        try
        {
            var handler = new MockHttpMessageHandler(req =>
            {
                string json = @"{
                    ""has_speech"": true,
                    ""segments"": [
                        {
                            ""start_time_seconds"": 1.0,
                            ""end_time_seconds"": 4.0,
                            ""confidence"": 0.92,
                            ""verdict"": ""Match"",
                            ""reason_flags"": [],
                            ""speaker_label"": ""SPEAKER_00"",
                            ""transcript"": ""Toxic threat statement"",
                            ""is_offensive"": true,
                            ""moderation_violations"": [""threat"", ""toxicity""],
                            ""moderation_scores"": {
                                ""toxicity"": 0.99,
                                ""threat"": 0.85
                            }
                        }
                    ]
                }";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            using var client = new SidecarClient(httpClient);

            var resp = await client.ProcessAudioAsync(fakeAudio);
            Assert.NotNull(resp);
            Assert.True(resp.HasSpeech);
            Assert.Single(resp.Segments);

            var seg = resp.Segments[0];
            Assert.Equal("SPEAKER_00", seg.SpeakerLabel);
            Assert.Equal("Toxic threat statement", seg.Transcript);
            Assert.True(seg.IsOffensive);
            Assert.Equal(2, seg.ModerationViolations.Count);
            Assert.NotNull(seg.ModerationScores);
            Assert.Equal(0.99, seg.ModerationScores["toxicity"]);
            Assert.Equal(0.85, seg.ModerationScores["threat"]);
        }
        finally
        {
            if (File.Exists(fakeAudio)) File.Delete(fakeAudio);
        }
    }
}

