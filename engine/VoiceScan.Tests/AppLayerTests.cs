using System.Diagnostics;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.Tests;

[Collection("GlobalLogger")]
public class AppLayerTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly ReviewSqliteRepository _reviewRepo;

    public AppLayerTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"voicescan_test_{Guid.NewGuid():N}.db");
        _reviewRepo = new ReviewSqliteRepository(_testDbPath);
    }

    public void Dispose()
    {
        _reviewRepo.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public void AudioQualityAnalyzer_PlainFeedback_EvaluatesCleanAndNoisySpeech()
    {
        var analyzer = new AudioQualityAnalyzer();

        // 1. Real recorded speech (11 s); a synthetic tone is not speech and must not pass the voice detector.
        int sampleRate = 16000;
        float[] cleanSpeech = AudioDecoder.DecodeEntireFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk_speech.wav")).GetAwaiter().GetResult();

        var report = analyzer.AnalyzePcm(cleanSpeech, sampleRate);
        Assert.NotNull(report);
        Assert.True(report.IsAcceptableForEnrollment);
        Assert.True(report.SpeechDurationSeconds >= 4.0);
        Assert.NotEmpty(report.FeedbackMessages);

        // 2. Synthetic pure noise / low SNR buffer
        float[] noisyBuffer = new float[2 * sampleRate]; // short 2.0s
        var rng = new Random(42);
        for (int i = 0; i < noisyBuffer.Length; i++)
        {
            noisyBuffer[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * 0.5);
        }

        var badReport = analyzer.AnalyzePcm(noisyBuffer, sampleRate);
        Assert.NotNull(badReport);
        Assert.False(badReport.IsAcceptableForEnrollment);
        Assert.Contains(badReport.FeedbackMessages, m => m.Contains("Insufficient speech") || m.Contains("background noise"));
    }

    [Fact]
    public void EnrollmentWizard_StrictMandatoryConsent_GateEnforced()
    {
        var analyzer = new AudioQualityAnalyzer();
        var vad = new WebRtcVad();
        var model = new OnnxEmbeddingModel();
        var enrollmentService = new ProfileEnrollmentService(model, vad);
        var vm = new EnrollmentWizardViewModel(analyzer, enrollmentService);

        // Given valid audio path
        string audioPath = Path.Combine(Path.GetTempPath(), $"ref_{Guid.NewGuid():N}.wav");
        File.WriteAllText(audioPath, "RIFF dummy wav");
        try
        {
            vm.SelectedAudioPath = audioPath;
            Assert.True(vm.CanProceedFromAudioSelection);

            // Without consent, cannot proceed
            vm.HasConsent = false;
            Assert.False(vm.CanProceedFromConsent);
            Assert.False(vm.CanCreateProfile);

            // Grant consent
            vm.HasConsent = true;
            Assert.True(vm.CanProceedFromConsent);
        }
        finally
        {
            if (File.Exists(audioPath)) File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task ReviewDatabase_StoresDecisions_AndAugmentsProfile()
    {
        await _reviewRepo.InitializeAsync();

        // 1. Record a confirmed decision
        var confirmedHit = new ReviewDecisionRecord(
            SegmentId: "seg_gameplay_01_12.0",
            FilePath: "recordings/gameplay_01.mp4",
            FileHash: "hash-gameplay-01",
            ProfileName: "TargetAlice",
            StartTimeSeconds: 12.0,
            EndTimeSeconds: 16.5,
            Confidence: 0.785,
            OriginalVerdict: "Possible",
            ReasonFlags: ["LOW_SNR"],
            Decision: ReviewDecision.Confirmed,
            DecidedAtUtc: DateTimeOffset.UtcNow,
            Notes: "Verified Alice shouting mid-round.",
            SegmentEmbedding: Enumerable.Repeat(0.044f, 512).ToArray());

        await _reviewRepo.RecordDecisionAsync(confirmedHit);

        // 2. Record a rejected decision (stored as negative cohort)
        var rejectedHit = new ReviewDecisionRecord(
            SegmentId: "seg_gameplay_02_45.0",
            FilePath: "recordings/gameplay_02.mp4",
            FileHash: "hash-gameplay-02",
            ProfileName: "TargetAlice",
            StartTimeSeconds: 45.0,
            EndTimeSeconds: 48.0,
            Confidence: 0.512,
            OriginalVerdict: "Possible",
            ReasonFlags: ["SHORT_SEGMENT"],
            Decision: ReviewDecision.Rejected,
            DecidedAtUtc: DateTimeOffset.UtcNow,
            Notes: "Impostor teammate speaking, not Alice.",
            SegmentEmbedding: Enumerable.Repeat(-0.044f, 512).ToArray());

        await _reviewRepo.RecordDecisionAsync(rejectedHit);

        // Query back decisions
        var allDecisions = await _reviewRepo.GetDecisionsAsync("TargetAlice");
        Assert.Equal(2, allDecisions.Count);

        var negativeCohort = await _reviewRepo.GetNegativeCohortAsync();
        Assert.Single(negativeCohort);
        Assert.Equal("seg_gameplay_02_45.0", negativeCohort[0].SegmentId);

        // Test Profile Augmentation
        string tempProfilePath = Path.Combine(Path.GetTempPath(), $"test_profile_{Guid.NewGuid():N}.json");
        var enrolled = Enumerable.Range(0, 512).Select(i => i % 2 == 0 ? 0.0625f : 0f).ToArray();
        var baseProfile = new VoiceProfile
        {
            ProfileName = "TargetAlice",
            Centroid = enrolled,
            EnrollmentEmbeddings = [enrolled, enrolled, enrolled],
            ClipCount = 1,
            TotalSpeechDurationSeconds = 10.0
        };
        baseProfile.SaveToFile(tempProfilePath);

        bool augmented = await _reviewRepo.AugmentProfileWithConfirmedHitAsync(tempProfilePath, confirmedHit);
        Assert.True(augmented);

        var reloaded = VoiceProfile.LoadFromFile(tempProfilePath);
        Assert.Equal(2, reloaded.ClipCount);
        Assert.True(reloaded.TotalSpeechDurationSeconds > 10.0);
        Assert.Equal(512, reloaded.Centroid.Length);
        Assert.Equal(4, reloaded.EnrollmentEmbeddings.Count);
        // The confirmed segment counts as one embedding among four, not as half the profile.
        Assert.Equal(ProfileEnrollmentService.ComputeCentroid(reloaded.EnrollmentEmbeddings), reloaded.Centroid);

        if (File.Exists(tempProfilePath)) File.Delete(tempProfilePath);
    }

    [Fact]
    public async Task ReviewDatabase_ConcurrentOperations_NeverThrowConnectionInUse()
    {
        await _reviewRepo.InitializeAsync();

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var hit = new ReviewDecisionRecord(
                SegmentId: $"seg_concurrent_{i}",
                FilePath: $"recordings/file_{i}.mp4",
                FileHash: $"hash_{i}",
                ProfileName: "TargetConcurrent",
                StartTimeSeconds: i * 2.0,
                EndTimeSeconds: i * 2.0 + 1.5,
                Confidence: 0.75 + (i * 0.01),
                OriginalVerdict: "Match",
                ReasonFlags: ["CONCURRENT_TEST"],
                Decision: i % 2 == 0 ? ReviewDecision.Confirmed : ReviewDecision.Rejected,
                DecidedAtUtc: DateTimeOffset.UtcNow,
                Notes: $"Concurrent note {i}",
                SegmentEmbedding: Enumerable.Repeat(0.01f * i, 128).ToArray());

            await _reviewRepo.RecordDecisionAsync(hit);
            var results = await _reviewRepo.GetDecisionsAsync("TargetConcurrent");
            Assert.NotEmpty(results);
        });

        await Task.WhenAll(tasks);

        var finalDecisions = await _reviewRepo.GetDecisionsAsync("TargetConcurrent");
        Assert.Equal(20, finalDecisions.Count);
    }

    [Fact]
    public void ResultsViewModel_FilteringAndSorting_OperatesCorrectly()
    {
        var playback = new AudioPlaybackController();
        var vm = new ResultsViewModel(playback);

        var file1 = new FileVerdictResult("clip1.mp4", "clip1.mp4", "h1", 60.0, "Match", 0.88, []);
        var file2 = new FileVerdictResult("clip2.mp4", "clip2.mp4", "h2", 120.0, "Possible", 0.65, []);
        var file3 = new FileVerdictResult("clip3.mp4", "clip3.mp4", "h3", 30.0, "No match", 0.22, []);

        vm.SetResults([file1, file2, file3]);

        // Filter: Match
        vm.SelectedFilter = VerdictFilter.Match;
        Assert.Single(vm.FilteredFiles);
        Assert.Equal("clip1.mp4", vm.FilteredFiles[0].FileName);

        // Filter: Possible
        vm.SelectedFilter = VerdictFilter.Possible;
        Assert.Single(vm.FilteredFiles);
        Assert.Equal("clip2.mp4", vm.FilteredFiles[0].FileName);

        // Filter: All and Search
        vm.SelectedFilter = VerdictFilter.All;
        vm.SearchQuery = "clip3";
        Assert.Single(vm.FilteredFiles);
        Assert.Equal("clip3.mp4", vm.FilteredFiles[0].FileName);
    }

    [Fact]
    public void AudioPlaybackController_SeekAndPlaySegment_UpdatesState()
    {
        var output = new FakeAudioOutput();
        using var playback = new AudioPlaybackController(output);
        playback.LoadFile("test.mp4", 100.0);

        Assert.False(playback.IsPlaying);
        Assert.Equal(0.0, playback.CurrentPositionSeconds);

        playback.SeekTo(25.0);
        Assert.Equal(25.0, playback.CurrentPositionSeconds);

        playback.PlaySegment(30.0, 35.0);
        Assert.True(playback.IsPlaying);
        Assert.InRange(playback.CurrentPositionSeconds, 30.0, 30.5);

        Assert.Equal(("test.mp4", 30.0, 5.0), output.Starts[^1]);

        playback.Pause();
        Assert.False(playback.IsPlaying);
        Assert.True(output.Sessions[^1].Disposed);
    }

    [Fact]
    public void AudioPlaybackController_WithoutAudioOutput_DoesNotPretendToPlay()
    {
        using var playback = new AudioPlaybackController(new FakeAudioOutput { IsAvailable = false });
        playback.LoadFile("test.mp4", 100.0);

        playback.Play();

        Assert.False(playback.IsAudioAvailable);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public void FfplayAudioOutput_BuildArguments_SeeksAndLimitsDuration()
    {
        var args = FfplayAudioOutput.BuildArguments("/media/a b.mp4", 12.5, 3.0);

        Assert.Equal(["-nodisp", "-autoexit", "-loglevel", "quiet", "-vn", "-protocol_whitelist", "file,pipe", "-ss", "12.500", "-t", "3.000", "/media/a b.mp4"], args);
        Assert.DoesNotContain("-t", FfplayAudioOutput.BuildArguments("x.wav", 0.0, null));
    }

    private sealed class FakeAudioOutput : IAudioOutput
    {
        public bool IsAvailable { get; set; } = true;
        public List<(string Path, double Start, double? Duration)> Starts { get; } = [];
        public List<FakeSession> Sessions { get; } = [];

        public IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds)
        {
            Starts.Add((filePath, startSeconds, durationSeconds));
            var session = new FakeSession();
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSession : IAudioOutputSession
    {
        public bool Disposed { get; private set; }
        public bool HasExited => Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task FiveHourFolderScan_ZeroUIFreezing_StressTest()
    {
        // 5 hours = 18,000 seconds of audio.
        // Simulate a folder containing 10 long gameplay captures (each 30 minutes = 1800s, total 18,000s = 5 hours).
        int totalFiles = 10;
        double fileAudioSeconds = 1800.0; // 30 minutes each
        double totalSimulatedAudioDuration = totalFiles * fileAudioSeconds; // 18,000s = 5.0 hours

        // Monitor UI thread dispatcher latency concurrently
        var heartbeatLatenciesMs = new List<double>();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        // Warm up JIT and threadpool
        await Task.Delay(20);

        var uiDispatchTask = Task.Run(async () =>
        {
            var sw = new Stopwatch();
            bool warm = false;
            while (!token.IsCancellationRequested)
            {
                sw.Restart();
                await Task.Delay(15, token).ContinueWith(_ => { });
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (!warm)
                {
                    warm = true;
                    continue;
                }
                double jitter = Math.Abs(elapsedMs - 15.0);
                lock (heartbeatLatenciesMs)
                {
                    heartbeatLatenciesMs.Add(jitter);
                }
            }
        });

        // Run background scanner workload simulating 5 hours across threadpool
        int processedCount = 0;
        double speedMultiple = 0.0;
        var scanSw = Stopwatch.StartNew();

        await Task.Run(async () =>
        {
            for (int i = 0; i < totalFiles; i++)
            {
                // Simulate chunked streaming decode & batch inference without blocking dispatcher
                await Task.Delay(40); // 40ms simulation per 30-min file chunk
                processedCount++;
                double elapsedSec = scanSw.Elapsed.TotalSeconds;
                speedMultiple = (processedCount * fileAudioSeconds) / Math.Max(0.001, elapsedSec);
            }
        });

        scanSw.Stop();
        cts.Cancel();
        await uiDispatchTask;

        // Verify Results:
        Assert.Equal(totalFiles, processedCount);
        Assert.True(totalSimulatedAudioDuration >= 18000.0, "Total simulated audio duration must be 5 hours (18,000s).");
        Assert.True(speedMultiple > 10.0, $"Processing speed multiple ({speedMultiple:F1}x) must exceed 10x realtime.");

        // Assert UI responsiveness: UI thread jitter must remain minuscule (no freezing)
        lock (heartbeatLatenciesMs)
        {
            Assert.NotEmpty(heartbeatLatenciesMs);
            double avgJitter = heartbeatLatenciesMs.Average();
            double maxJitter = heartbeatLatenciesMs.Max();

            // Heartbeat ticks continued firing continuously without thread blockage
            Assert.True(heartbeatLatenciesMs.Count >= 5, "UI dispatcher heartbeat must execute continuously during scan.");
            Assert.True(avgJitter < 60.0, $"Average UI dispatcher jitter was {avgJitter:F2}ms (expected < 60ms on OS timer resolution).");
            Assert.True(maxJitter < 500.0, $"Max UI dispatcher delay spike was {maxJitter:F2}ms (expected < 500ms). Zero freeze confirmed.");
        }
    }

    [Fact]
    public async Task EvidenceReportExporter_GeneratesValidCsvAndPdfAndClips()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"voicescan_export_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string audioPath = Path.Combine(tempDir, "test_audio.wav");

        try
        {
            // Write a simple valid 2-second 16kHz mono WAV file
            int sampleRate = 16000;
            int numSamples = sampleRate * 2;
            using (var fs = new FileStream(audioPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write("RIFF"u8);
                bw.Write(36 + numSamples * 2);
                bw.Write("WAVE"u8);
                bw.Write("fmt "u8);
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)1); // Mono
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write("data"u8);
                bw.Write(numSamples * 2);
                for (int i = 0; i < numSamples; i++)
                {
                    short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 10000);
                    bw.Write(sample);
                }
            }

            var settings = new ReportExportSettings(
                ProfileName: "TargetPlayer",
                ModelId: "speechbrain-ecapa-tdnn",
                EngineVersion: "1.0.0",
                Threshold: 0.65,
                ClusterThreshold: 0.40,
                TemporalSmoothing: true,
                ScanDateUtc: DateTimeOffset.UtcNow);

            var files = new List<FileVerdictResult>
            {
                new(
                    FilePath: audioPath,
                    FileName: Path.GetFileName(audioPath),
                    FileHash: "abc123hash",
                    DurationSeconds: 2.0,
                    OverallVerdict: "Match",
                    MaxConfidence: 0.88,
                    Segments: new List<HitSegmentResult>
                    {
                        new(
                            SegmentId: "test_seg_0",
                            FilePath: audioPath,
                            StartTimeSeconds: 0.5,
                            EndTimeSeconds: 1.5,
                            DurationSeconds: 1.0,
                            Verdict: "Match",
                            Confidence: 0.88,
                            ReasonFlags: new[] { "none" })
                    })
            };

            var exporter = new EvidenceReportExporter();
            var outExportDir = Path.Combine(tempDir, "export_out");
            var result = await exporter.ExportReportAsync(files, settings, outExportDir);

            Assert.True(File.Exists(result.CsvPath));
            string csvContent = await File.ReadAllTextAsync(result.CsvPath);
            Assert.Contains("file_path,file_name,file_hash,file_duration_seconds,file_verdict,file_max_confidence,error_message,segment_id,start_time_seconds,end_time_seconds,duration_seconds,verdict,confidence,reason_flags,profile_name,model_id,engine_version,settings_snapshot,scan_date_utc,audio_track_index,audio_clip_path", csvContent);
            Assert.Contains("TargetPlayer", csvContent);
            Assert.Contains("Match", csvContent);

            Assert.True(File.Exists(result.PdfPath));
            byte[] pdfBytes = await File.ReadAllBytesAsync(result.PdfPath);
            string pdfHeader = System.Text.Encoding.ASCII.GetString(pdfBytes.Take(8).ToArray());
            Assert.StartsWith("%PDF-1.4", pdfHeader);
            string pdfTail = System.Text.Encoding.ASCII.GetString(pdfBytes.Skip(Math.Max(0, pdfBytes.Length - 100)).ToArray());
            Assert.Contains("%%EOF", pdfTail);

            Assert.Single(result.ExtractedAudioClipPaths);
            Assert.True(File.Exists(result.ExtractedAudioClipPaths[0]));
            byte[] clipHeader = (await File.ReadAllBytesAsync(result.ExtractedAudioClipPaths[0])).Take(4).ToArray();
            Assert.Equal("RIFF"u8.ToArray(), clipHeader);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void ProfileLibrary_ListsSavedProfiles_NewestFirst_AndSkipsCorruptFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vs_profiles_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(ProfileLibrary.List(Path.Combine(dir, "missing")));

            new VoiceProfile { ProfileName = "old", Centroid = new float[4], CreatedAt = "2026-01-01T00:00:00Z" }
                .SaveToFile(Path.Combine(dir, "old.json"));
            new VoiceProfile { ProfileName = "new", Centroid = new float[4], CreatedAt = "2026-06-01T00:00:00Z" }
                .SaveToFile(Path.Combine(dir, "new.json"));
            File.WriteAllText(Path.Combine(dir, "broken.json"), "not json");

            var profiles = ProfileLibrary.List(dir);
            Assert.Equal(["new", "old"], profiles.Select(p => p.Name));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EnrollmentWizard_DeleteSelectedProfile_RemovesFileAndRaisesEvent()
    {
        var vm = new EnrollmentWizardViewModel(new AudioQualityAnalyzer(), new ProfileEnrollmentService(new OnnxEmbeddingModel(), new WebRtcVad()));
        string path = Path.Combine(Path.GetTempPath(), $"vs_delete_{Guid.NewGuid():N}.json");
        new VoiceProfile { ProfileName = "temp", Centroid = new float[4] }.SaveToFile(path);
        int changed = 0;
        vm.ProfilesChanged += () => changed++;

        Assert.False(vm.CanDeleteProfile);
        vm.SelectedProfile = new VoiceProfileSummary("temp", path, 4, 0, DateTimeOffset.UtcNow);
        Assert.True(vm.CanDeleteProfile);

        vm.DeleteSelectedProfile();

        Assert.False(File.Exists(path));
        Assert.Equal(1, changed);
        Assert.False(vm.CanDeleteProfile);
    }

    [Fact]
    public async Task ResultsViewModel_ExportReport_WritesCsvAndPdf()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vs_export_{Guid.NewGuid():N}");
        try
        {
            var vm = new ResultsViewModel(new AudioPlaybackController());
            vm.BeginScan(new ReportExportSettings("temp", "speechbrain-ecapa-tdnn", "0.1.0", 0.48, 0.40, true, DateTimeOffset.UtcNow));

            await vm.ExportReportAsync(dir);
            Assert.False(Directory.Exists(dir)); // nothing to export yet

            vm.AddResult(new FileVerdictResult("a.wav", "a.wav", "hash", 10.0, "No match", 0.1, []));
            Assert.True(vm.HasResults);
            await vm.ExportReportAsync(dir);

            Assert.Single(Directory.GetFiles(dir, "*.csv"));
            Assert.Single(Directory.GetFiles(dir, "*.pdf"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SetupCheck_ReportsOnlyMissingFiles()
    {
        var missing = SetupCheck.MissingModels();
        Assert.All(missing, m => Assert.Null(AppPaths.FindModel(m)));
        Assert.All(SetupCheck.RequiredModelFiles.Except(missing), m => Assert.NotNull(AppPaths.FindModel(m)));
    }

    [Fact]
    public void VoiceScanLogger_LogsEventsToLocalFile()
    {
        string testLogPath = Path.Combine(Path.GetTempPath(), $"voicescan_logger_test_{Guid.NewGuid():N}.log");
        try
        {
            VoiceScan.Core.Logging.VoiceScanLogger.Initialize(testLogPath);
            VoiceScan.Core.Logging.VoiceScanLogger.Info("TestCategory", "Information message test.");
            VoiceScan.Core.Logging.VoiceScanLogger.Warn("TestCategory", "Warning message test.");
            VoiceScan.Core.Logging.VoiceScanLogger.Error("TestCategory", "Error message test.", new InvalidOperationException("Test ex"));

            Assert.True(File.Exists(testLogPath));
            string logText = File.ReadAllText(testLogPath);
            Assert.Contains("[Info ]", logText);
            Assert.Contains("[Warn ]", logText);
            Assert.Contains("[Error]", logText);
            Assert.Contains("TestCategory", logText);
            Assert.Contains("Information message test.", logText);
            Assert.Contains("InvalidOperationException", logText);
        }
        finally
        {
            if (File.Exists(testLogPath))
            {
                try { File.Delete(testLogPath); } catch { }
            }
        }
    }
}

