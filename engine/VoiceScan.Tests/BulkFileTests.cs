using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.Tests;

public sealed class BulkFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bulk_{Guid.NewGuid():N}");

    public BulkFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        foreach (var name in new[] { "a.wav", "b.MP3", "nested/c.flac", "notes.txt", "nested/d.jpg" })
            File.WriteAllText(Path.Combine(_root, name), "x");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MediaFileCollector_ExpandsFoldersFiltersTypesAndDeduplicates()
    {
        var a = Path.Combine(_root, "a.wav");
        var files = MediaFileCollector.Collect([_root, a, Path.Combine(_root, "notes.txt"), "", Path.Combine(_root, "missing.wav")]);

        Assert.Equal(3, files.Count);
        Assert.Contains(a, files);
        Assert.Contains(Path.Combine(_root, "nested", "c.flac"), files);
        Assert.DoesNotContain(files, f => f.EndsWith(".txt") || f.EndsWith(".jpg"));
    }

    [Fact]
    public void ScanDashboard_AddPaths_AccumulatesWithoutDuplicates_AndRemoves()
    {
        var vm = new ScanDashboardViewModel(new FakeScanController(), new FakeWaveformService());

        Assert.Equal(3, vm.AddPaths([_root]));
        Assert.Equal(0, vm.AddPaths([Path.Combine(_root, "a.wav")]));
        Assert.Equal(3, vm.TargetFiles.Count);

        vm.RemoveFile(vm.TargetFiles[0]);
        Assert.Equal(2, vm.TargetFiles.Count);

        vm.ClearFiles();
        Assert.Empty(vm.TargetFiles);
    }

    [Fact]
    public async Task WaveformPreview_LoadsOnceAndTogglesVisibility()
    {
        var service = new FakeWaveformService();
        var item = new MediaFileItem(Path.Combine(_root, "a.wav"));

        await WaveformPreview.ToggleAsync(item, service);
        Assert.True(item.IsWaveformVisible);
        Assert.NotNull(item.Waveform);

        await WaveformPreview.ToggleAsync(item, service);
        Assert.False(item.IsWaveformVisible);

        await WaveformPreview.ToggleAsync(item, service);
        Assert.True(item.IsWaveformVisible);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Enrollment_AddSamples_ChecksEachAndCountsOnlyAccepted()
    {
        var vm = new EnrollmentWizardViewModel(
            new FakeAnalyzer(acceptName: "a.wav"),
            new ProfileEnrollmentService(new OnnxEmbeddingModel(), new WebRtcVad()),
            new FakeWaveformService());

        await vm.AddSamplesAsync([_root]);

        Assert.Equal(3, vm.SampleFiles.Count);
        Assert.Equal(1, vm.AcceptedSampleCount);
        Assert.True(vm.CanProceedFromQuality);
        Assert.Equal("a", vm.ProfileName);

        await vm.AddSamplesAsync([Path.Combine(_root, "a.wav")]);
        Assert.Equal(3, vm.SampleFiles.Count);
    }

    private sealed class FakeWaveformService : IWaveformService
    {
        public int Calls { get; private set; }

        public Task<WaveformEnvelope> GenerateEnvelopeAsync(string audioFilePath, int targetBuckets = 300, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new WaveformEnvelope([-0.5f], [0.5f], 1.0, 1));
        }

        public WaveformEnvelope GenerateEnvelopeFromPcm(ReadOnlySpan<float> pcm, int sampleRate = 16000, int targetBuckets = 300) =>
            new([], [], 0.0, 0);
    }

    private sealed class FakeAnalyzer(string acceptName) : IAudioQualityAnalyzer
    {
        public Task<AudioQualityReport> AnalyzeAudioAsync(string audioFilePath, CancellationToken cancellationToken = default)
        {
            bool ok = Path.GetFileName(audioFilePath) == acceptName;
            return Task.FromResult(new AudioQualityReport(
                ok ? AudioQualityTier.Excellent : AudioQualityTier.Rejected, 60, 40, 20, 0.001, ok, ok, [], ok));
        }

        public AudioQualityReport AnalyzePcm(ReadOnlySpan<float> pcm16k, int sampleRate = 16000) => throw new NotSupportedException();
    }

    private sealed class FakeScanController : IBackgroundScanController
    {
        public event EventHandler<OverallScanProgress>? ProgressChanged { add { } remove { } }
        public event EventHandler<FileVerdictResult>? FileCompleted { add { } remove { } }
        public event EventHandler<ScanExecutionState>? StateChanged { add { } remove { } }

        public ScanExecutionState CurrentState => ScanExecutionState.Idle;
        public OverallScanProgress CurrentProgress => default!;
        public ModelOperatingPoint OperatingPoint { get; } = new(0.5, 0.4);
        public string ModelId => "fake-model";

        public Task<IReadOnlyList<FileVerdictResult>> StartScanAsync(IReadOnlyList<string> targetFiles, string profilePath,
            PipelineScanOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileVerdictResult>>([]);

        public void Pause() { }
        public void Resume() { }
        public void Cancel() { }
        public void Dispose() { }
    }
}
