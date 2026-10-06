using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.Tests;

/// <summary>Regression tests for the defects fixed in the October 2026 bug sweep.</summary>
public sealed class RegressionTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static readonly string SpeechWav = Path.Combine(Fixtures, "jfk_speech.wav");
    private readonly string _root = Directory.CreateTempSubdirectory("vs_regression_").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static float[] Speech() => AudioDecoder.DecodeEntireFileAsync(SpeechWav).GetAwaiter().GetResult();

    // ---------- Voice activity detection ----------

    [Theory]
    [InlineData("speech")]
    [InlineData("quiet_speech")]
    [InlineData("loud_clipped")]
    public void WebRtcVad_MatchesReferenceImplementationFrameForFrame(string signal)
    {
        float[] speech = Speech();
        float[] audio = signal switch
        {
            "quiet_speech" => speech.Select(x => x * 0.01f).ToArray(),
            "loud_clipped" => speech.Select(x => Math.Clamp(x * 8f, -1f, 1f)).ToArray(),
            _ => speech
        };
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "webrtc_vad_reference.json")));
        var modes = doc.RootElement.GetProperty("signals").GetProperty(signal);

        for (int mode = 0; mode <= 3; mode++)
        {
            string expected = modes.GetProperty(mode.ToString(CultureInfo.InvariantCulture)).GetString()!;
            var stream = new WebRtcVad(mode).StartProbabilityStream();
            stream.Feed(audio, audio.Length);
            string actual = string.Concat(stream.Finish().Take(expected.Length).Select(p => p > 0.5f ? '1' : '0'));
            Assert.Equal(expected, actual);
        }
    }

    // ---------- Feature front-ends and embeddings ----------

    [Fact]
    public void SpeechFeatures_MatchSpeechBrainAndNemoReferences()
    {
        float[] speech = Speech();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "frontend_golden.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            float[] audio = speech.AsSpan(c.GetProperty("start_sample").GetInt32(), c.GetProperty("length").GetInt32()).ToArray();

            var ecapaRef = c.GetProperty("ecapa").EnumerateArray().Select(r => r.EnumerateArray().Select(e => e.GetSingle()).ToArray()).ToArray();
            var ecapa = SpeechFeatures.Compute(audio, FeatureFrontEnd.SpeechBrainFbank);
            Assert.Equal(ecapaRef.Length, ecapa.Frames.GetLength(0));
            for (int f = 0; f < ecapaRef.Length; f++)
                for (int m = 0; m < 80; m++)
                    Assert.True(Math.Abs(ecapaRef[f][m] - ecapa.Frames[f, m]) < 2e-3, $"SpeechBrain fbank differs at frame {f}, mel {m}");

            var titaRef = c.GetProperty("titanet").EnumerateArray().Select(r => r.EnumerateArray().Select(e => e.GetSingle()).ToArray()).ToArray();
            var tita = SpeechFeatures.Compute(audio, FeatureFrontEnd.NemoMelSpectrogram);
            Assert.Equal(c.GetProperty("titanet_valid_frames").GetInt32(), tita.ValidFrames);
            Assert.Equal(titaRef[0].Length, tita.Frames.GetLength(0));
            for (int m = 0; m < 80; m++)
                for (int f = 0; f < titaRef[m].Length; f++)
                    Assert.True(Math.Abs(titaRef[m][f] - tita.Frames[f, m]) < 2e-3, $"NeMo features differ at frame {f}, mel {m}");
        }
    }

    [Fact]
    public void EcapaEmbeddings_MatchSpeechBrainPyTorchModel()
    {
        float[] speech = Speech();
        using var model = new OnnxEmbeddingModel("ecapa");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "ecapa_reference_embeddings.json")));

        foreach (var w in doc.RootElement.GetProperty("windows").EnumerateArray())
        {
            int start = w.GetProperty("start_sample").GetInt32();
            var expected = w.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var actual = model.ExtractEmbedding(speech[start..(start + 32000)]);
            Assert.True(SimilarityScorer.CosineSimilarity(expected, actual) > 0.999f);
        }
    }

    [Fact]
    public void OnnxEmbeddingModel_RejectsUnknownModelNames()
    {
        Assert.Throws<ArgumentException>(() => new OnnxEmbeddingModel("wespeaker"));
    }

    [Fact]
    public void OnnxEmbeddingModel_VersionIdentifiesWeightsAndFrontEnd()
    {
        using var model = new OnnxEmbeddingModel("titanet");
        Assert.StartsWith("nvidia-titanet-small@", model.ModelVersion);
        Assert.EndsWith("+" + SpeechFeatures.Fingerprint(FeatureFrontEnd.NemoMelSpectrogram), model.ModelVersion);
        Assert.Equal(192, model.EmbeddingDimension);
    }

    // ---------- Profiles and scanning ----------

    [Fact]
    public void PipelineScanner_RefusesProfileFromAnotherModelOfTheSameSize()
    {
        using var ecapa = new OnnxEmbeddingModel("ecapa");
        var scanner = new PipelineScanner(ecapa, new WebRtcVad());
        var titanetProfile = new VoiceProfile
        {
            ProfileName = "p",
            ModelId = "nvidia-titanet-small",
            ModelVersion = "nvidia-titanet-small@0123456789ab+nemo-mel80-perfeat-v1",
            Centroid = new float[192]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => scanner.EnsureCompatible(titanetProfile));
        Assert.Contains("Enroll the voice again", ex.Message);
        Assert.Throws<InvalidOperationException>(() => scanner.EnsureCompatible(new VoiceProfile { ProfileName = "old", Centroid = new float[192] }));
    }

    [Fact]
    public async Task Enrollment_RejectsAudioWithoutSpeech()
    {
        // Faint background hiss only. (Steady loud tones are not a fair stand-in: WebRTC VAD itself labels them speech.)
        var rng = new Random(3);
        string tone = Path.Combine(_root, "hiss.wav");
        WriteWav(tone, Enumerable.Range(0, 16000 * 4).Select(_ => (float)((rng.NextDouble() - 0.5) * 2e-4)).ToArray());
        using var model = new OnnxEmbeddingModel("ecapa");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProfileEnrollmentService(model, new WebRtcVad()).EnrollProfileAsync([tone], "tone"));
    }

    [Fact]
    public void ScoreAndAggregate_SegmentDiagnosticsIgnoreOtherSpeakersWindows()
    {
        float[] target = Unit(0);
        float[] other = Unit(1);
        var windows = new List<WindowItem>
        {
            new(0, 0.0, 2.0, target, snrDb: 30.0),
            new(1, 1.0, 3.0, target, snrDb: 30.0),
            new(2, 2.0, 4.0, target, snrDb: 30.0),
            // A loud other speaker overlapping the end of the target's turn.
            new(3, 3.0, 5.0, other, snrDb: 0.0),
            new(4, 2.5, 4.5, other, snrDb: 0.0),
        };
        var profile = new VoiceProfile { ProfileName = "t", Centroid = target };

        var (segments, maxConfidence, verdict) = PipelineScanner.ScoreAndAggregate(windows, profile, threshold: 0.5);

        Assert.Equal("Match", verdict);
        var seg = Assert.Single(segments);
        Assert.DoesNotContain("LOW_SNR", seg.ReasonFlags);
        Assert.Equal(target, seg.Embedding);
        Assert.Equal(1.0, maxConfidence, precision: 4);
    }

    [Fact]
    public void ScoreAndAggregate_ReportsTheMeasuredScoreForPossibleFiles()
    {
        // Scores just above the threshold in noisy audio are downgraded to Possible (LOW_SNR); the file's
        // confidence must still be the measured 0.51, not clamped below the threshold.
        float[] centroid = Unit(0);
        float[] near = new float[16];
        near[0] = 0.51f;
        near[1] = MathF.Sqrt(1 - 0.51f * 0.51f);
        var windows = Enumerable.Range(0, 4).Select(i => new WindowItem(i, i, i + 2.0, near, snrDb: 3.0)).ToList();

        var (segments, maxConfidence, verdict) = PipelineScanner.ScoreAndAggregate(
            windows, new VoiceProfile { ProfileName = "t", Centroid = centroid }, threshold: 0.5);

        Assert.Equal("Possible", verdict);
        Assert.Contains("LOW_SNR", Assert.Single(segments).ReasonFlags);
        Assert.Equal(0.51, maxConfidence, precision: 3);
    }

    [Fact]
    public void SpeakerClusterer_LongRecordingStaysBoundedAndRejoinsDistantTurns()
    {
        // 6,000 windows (well above the dense limit): speaker A at the start and end, speaker B in between.
        var rng = new Random(5);
        float[] Noisy(int axis)
        {
            var v = new float[64];
            v[axis] = 1f;
            for (int d = 0; d < v.Length; d++) v[d] += (float)(rng.NextDouble() * 0.05);
            float n = MathF.Sqrt(v.Sum(x => x * x));
            return v.Select(x => x / n).ToArray();
        }
        var windows = Enumerable.Range(0, 6000)
            .Select(i => new WindowItem(i, i, i + 2.0, Noisy(i < 1500 || i >= 4500 ? 0 : 1)))
            .ToList();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var clusters = SpeakerClusterer.ClusterWindows(windows, distanceThreshold: 0.4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, c => c.Windows.Count == 3000 && c.Windows.Any(w => w.Index == 0) && c.Windows.Any(w => w.Index == 5999));
        // A dense 6,000 x 6,000 matrix alone would be 288 MB.
        Assert.True(allocated < 200L * 1024 * 1024, $"Clustering allocated {allocated / 1048576} MB");
    }

    // ---------- Decoder ----------

    [Fact]
    public async Task AudioDecoder_StoppingEarlyKillsFfmpeg()
    {
        if (!OperatingSystem.IsLinux()) return; // process command lines are read from /proc

        string longWav = Path.Combine(_root, $"long_{Guid.NewGuid():N}.wav");
        float[] speech = Speech();
        WriteWav(longWav, Enumerable.Range(0, 30).SelectMany(_ => speech).ToArray());

        await foreach (var _ in AudioDecoder.StreamDecodeAsync(longWav))
        {
            break;
        }

        Assert.Empty(ProcessesMentioning(longWav));
    }

    [Fact]
    public async Task AudioDecoder_ReportsFfmpegFailureInsteadOfReturningPartialAudio()
    {
        string corrupt = Path.Combine(_root, "corrupt.wav");
        File.WriteAllText(corrupt, "this is not audio");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in AudioDecoder.StreamDecodeAsync(corrupt)) { }
        });
        Assert.Contains("FFmpeg could not decode", ex.Message);
    }

    [Fact]
    public async Task AudioDecoder_MaxDurationLimitsDecodedAudio()
    {
        float[] audio = await AudioDecoder.DecodeEntireFileAsync(SpeechWav, maxDurationSeconds: 2.0);
        Assert.Equal(32000, audio.Length);
    }

    [Fact]
    public void SpooledAudio_UsesGivenDirectoryAndSweepsCrashLeftovers()
    {
        string spoolDir = Path.Combine(_root, "spool");
        Directory.CreateDirectory(spoolDir);
        string stale = Path.Combine(spoolDir, "voicescan_stale.pcm");
        File.WriteAllBytes(stale, new byte[16]);

        using (var spool = new SpooledAudio(spoolDir))
        {
            spool.Append(new float[100]);
            Assert.False(File.Exists(stale));
            Assert.Single(Directory.GetFiles(spoolDir, "voicescan_*.pcm"));
        }

        Assert.Empty(Directory.GetFiles(spoolDir));
    }

    // ---------- App: scan control, results, review ----------

    [Fact]
    public async Task ScanController_RefusesSecondScanUntilCancelledRunHasStopped()
    {
        using var model = new OnnxEmbeddingModel("ecapa");
        var profile = await new ProfileEnrollmentService(model, new WebRtcVad()).EnrollProfileAsync([SpeechWav], "p");
        string profilePath = Path.Combine(_root, "p.json");
        profile.SaveToFile(profilePath);

        using var controller = new BackgroundScanController(new PipelineScanner(model, new WebRtcVad()));
        var states = new List<ScanExecutionState>();
        controller.StateChanged += (_, s) => { lock (states) states.Add(s); };

        var first = controller.StartScanAsync([SpeechWav, SpeechWav, SpeechWav], profilePath);
        controller.Pause();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartScanAsync([SpeechWav], profilePath));

        controller.Cancel();
        await first;

        Assert.Equal(ScanExecutionState.Cancelled, controller.CurrentState);
        lock (states) Assert.Equal(ScanExecutionState.Cancelled, states[^1]);
        await controller.StartScanAsync([SpeechWav], profilePath); // the stopped run no longer blocks a new one
        Assert.Equal(ScanExecutionState.Completed, controller.CurrentState);
    }

    [Fact]
    public async Task ScanController_RejectsIncompatibleProfileBeforeScanning()
    {
        using var model = new OnnxEmbeddingModel("ecapa");
        string profilePath = Path.Combine(_root, "old.json");
        new VoiceProfile { ProfileName = "old", ModelId = model.ModelId, Centroid = new float[192] }.SaveToFile(profilePath);
        using var controller = new BackgroundScanController(new PipelineScanner(model, new WebRtcVad()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartScanAsync([SpeechWav], profilePath));
        Assert.Equal(ScanExecutionState.Idle, controller.CurrentState);
    }

    [Fact]
    public void ResultsViewModel_KeepsSelectionWhenResultsArriveAndResetsPerScan()
    {
        var vm = new ResultsViewModel(new AudioPlaybackController(new SilentOutput()));
        var a = new FileVerdictResult("/r/a.wav", "a.wav", "h1", 10, "Match", 0.9, []);
        vm.AddResult(a);
        vm.SelectedFile = a;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.AddResult(new FileVerdictResult("/r/b.wav", "b.wav", "h2", 10, "No match", 0.1, []));

        Assert.Same(a, vm.SelectedFile);
        Assert.Contains(nameof(ResultsViewModel.SelectedFile), raised); // so the list re-selects it

        var settings = new ReportExportSettings("p", "m", "1", 0.5, 0.4, true, DateTimeOffset.UtcNow);
        vm.BeginScan(settings);
        Assert.False(vm.HasResults);
        Assert.Null(vm.SelectedFile);
        Assert.Same(settings, vm.ExportSettings);
    }

    [Fact]
    public async Task ReviewViewModel_ConfirmAddsSegmentToTheProfileItWasScannedAgainst()
    {
        using var repo = new ReviewSqliteRepository(Path.Combine(_root, "reviews.db"));
        var vm = new ReviewViewModel(repo, new AudioPlaybackController(new SilentOutput()));
        string profilePath = Path.Combine(_root, "alice.json");
        float[] enrolled = Unit(0);
        new VoiceProfile { ProfileName = "alice", Centroid = enrolled, EnrollmentEmbeddings = [enrolled], ClipCount = 1 }.SaveToFile(profilePath);

        var segment = new HitSegmentResult("rec_aaaa_0_1.0", "/r/rec.wav", 1, 3, 2, "Possible", 0.45, [], SegmentEmbedding: Unit(1), FileHash: "h");
        vm.EnqueueSegments([segment], "alice", "rec.wav", profilePath);
        vm.EnqueueSegments([segment], "bob", "rec.wav", profilePath: null);

        await vm.ConfirmSegmentAsync();
        await vm.RejectSegmentAsync();

        Assert.Equal(2, VoiceProfile.LoadFromFile(profilePath).EnrollmentEmbeddings.Count);
        var decisions = await repo.GetDecisionsAsync();
        Assert.Equal(2, decisions.Count); // one per voice; bob's rejection did not overwrite alice's confirmation
        Assert.Contains(decisions, d => d.ProfileName == "alice" && d.Decision == ReviewDecision.Confirmed);
        Assert.Contains(decisions, d => d.ProfileName == "bob" && d.Decision == ReviewDecision.Rejected);
    }

    [Fact]
    public async Task EnrollmentWizard_DoesNotOverwriteAnExistingVoice()
    {
        string profiles = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(profiles);
        string existing = Path.Combine(profiles, "Alice.json");
        new VoiceProfile { ProfileName = "Alice", Centroid = Unit(0), CreatedAt = DateTime.UtcNow.ToString("o") }.SaveToFile(existing);
        string before = File.ReadAllText(existing);

        using var model = new OnnxEmbeddingModel("ecapa");
        var vm = new EnrollmentWizardViewModel(new AudioQualityAnalyzer(), new ProfileEnrollmentService(model, new WebRtcVad()));
        await vm.AddSamplesAsync([SpeechWav]);
        vm.HasConsent = true;
        vm.ProfileName = "alice";

        Assert.Null(await vm.CreateProfileAsync(profiles));
        Assert.Contains("already exists", vm.StatusMessage);
        Assert.Equal(before, File.ReadAllText(existing));
    }

    [Fact]
    public void AudioPlayback_LetsThePlayerFinishTheSegmentInsteadOfCuttingItByTheClock()
    {
        var output = new ControllableOutput();
        using var playback = new AudioPlaybackController(output);
        playback.LoadFile("x.wav", 100);

        playback.PlaySegment(10.0, 10.1);
        Thread.Sleep(400); // well past the segment length by the wall clock

        Assert.True(playback.IsPlaying);
        output.Session!.Exit();
        Assert.True(SpinWait.SpinUntil(() => !playback.IsPlaying, TimeSpan.FromSeconds(5)));
    }

    // ---------- Evidence export ----------

    [Fact]
    public async Task EvidenceExport_QuotesCsvNeutralizesFormulasAndKeepsSameNamedFilesApart()
    {
        string dirA = Path.Combine(_root, "a");
        string dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "=HYPERLINK(test).wav"
            : "=HYPERLINK(\"x\").wav";
        string fileA = Path.Combine(dirA, fileName);
        string fileB = Path.Combine(dirB, fileName);
        File.Copy(SpeechWav, fileA);
        File.Copy(SpeechWav, fileB);

        FileVerdictResult Result(string path, string hash) => new(path, Path.GetFileName(path), hash, 11, "Match", 0.9,
            [new HitSegmentResult(HitSegmentResult.CreateId(path, hash, 0, 1.0), path, 1.0, 3.0, 2.0, "Match", 0.9, [])]);

        var export = await new EvidenceReportExporter().ExportReportAsync(
            [Result(fileA, "hash-a"), Result(fileB, "hash-b")],
            new ReportExportSettings("p", "m", "1", 0.5, 0.4, true, DateTimeOffset.UtcNow),
            Path.Combine(_root, "out"));

        Assert.Equal(2, export.ExtractedAudioClipPaths.Distinct().Count());
        Assert.All(export.ExtractedAudioClipPaths, p => Assert.True(File.Exists(p)));
        string csv = File.ReadAllText(export.CsvPath);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Contains("\"'=HYPERLINK(test).wav\"", csv);
        }
        else
        {
            Assert.Contains("\"'=HYPERLINK(\"\"x\"\").wav\"", csv);
        }
        Assert.DoesNotContain(",\"=HYPERLINK", csv);

        byte[] pdf = File.ReadAllBytes(export.PdfPath);
        Assert.DoesNotContain((byte)'\r', pdf);
    }

    [Fact]
    public void HitSegmentResult_IdsDifferForSameNamedFiles()
    {
        Assert.NotEqual(
            HitSegmentResult.CreateId("/x/rec.mkv", "hash-1", 0, 12.0),
            HitSegmentResult.CreateId("/y/rec.mkv", "hash-2", 0, 12.0));
    }

    [Theory]
    [InlineData(59.4, "00:59")]
    [InlineData(3599.0, "59:59")]
    [InlineData(7512.0, "2:05:12")]
    public void TimeFormat_KeepsHours(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Clock(seconds));
    }

    // ---------- helpers ----------

    private static float[] Unit(int axis)
    {
        var v = new float[16];
        v[axis] = 1f;
        return v;
    }

    private static void WriteWav(string path, float[] samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(16000);
        w.Write(32000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (float s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }

    private static List<int> ProcessesMentioning(string text)
    {
        var found = new List<int>();
        foreach (var dir in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(text, StringComparison.Ordinal)) found.Add(pid);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    private sealed class SilentOutput : IAudioOutput
    {
        public bool IsAvailable => false;
        public IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds) => throw new NotSupportedException();
    }

    private sealed class ControllableOutput : IAudioOutput
    {
        public ControllableSession? Session { get; private set; }
        public bool IsAvailable => true;
        public IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds) => Session = new ControllableSession();
    }

    private sealed class ControllableSession : IAudioOutputSession
    {
        private volatile bool _exited;
        public bool HasExited => _exited;
        public void Exit() => _exited = true;
        public void Dispose() => _exited = true;
    }
}

/// <summary>Runs the CLI entry point, which re-initializes the global logger, so it shares a collection with the logger test.</summary>
[Collection("GlobalLogger")]
public sealed class CliArgumentTests
{
    [Theory]
    [InlineData("scan", "--input", "x", "--profile", "p", "--output", "o.json", "--threshold", "0,5")]
    [InlineData("scan", "--input", "x", "--profile", "p", "--output", "o.json", "--bogus")]
    [InlineData("not-a-command")]
    public async Task Cli_RejectsBadArgumentsWithUsageExitCode(params string[] args)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // "0,5" must not be read as a German decimal
        try
        {
            Assert.Equal(2, await VoiceScan.Cli.Program.Main(args));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

}
