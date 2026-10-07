namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;
using Xunit;

/// <summary>Re-analysis from the Clips and Models pages (docs/SPEC-model-swap.md).</summary>
public sealed class ReanalysisViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_reanalysis_vm_" + Guid.NewGuid().ToString("N"));
    private readonly string _profiles;
    private readonly ModerationStore _store;

    public ReanalysisViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _profiles = Path.Combine(_dir, "profiles");
        _store = new ModerationStore(Path.Combine(_dir, "moderation.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly EmbeddingModelEntry EntryA = new(
        "model-a", "Model A", "/models/a.onnx", "", FeatureFrontEnd.SpeechBrainFbank, new(0.48, 0.60), IsBuiltIn: true);
    private static readonly EmbeddingModelEntry EntryB = new(
        "model-b", "Model B", "/models/b.onnx", "", FeatureFrontEnd.SpeechBrainFbank, new(0.48, 0.60), IsBuiltIn: true);

    private static DetectedSegment Line(double start, double end, string text, double insult = 0.01) => new()
    {
        StartTimeSeconds = start,
        EndTimeSeconds = end,
        SpeakerLabel = "SPEAKER_00",
        Transcript = text,
        Embedding = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f],
        ModerationScores = new Dictionary<string, double> { ["insult"] = insult }
    };

    /// <summary>Scans a real 30 s recording into the store as if a scan had analysed it with <paramref name="modelVersion"/>.</summary>
    private async Task<(long ClipId, string Path)> ScannedClipAsync(string modelVersion, IReadOnlyDictionary<string, string>? sidecarModels, params DetectedSegment[] lines)
    {
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "match.wav"), 30);
        await _store.IngestAsync(new FileScanResult
        {
            FilePath = wav,
            FileHash = FastFileHasher.ComputeFastHash(wav),
            DurationSeconds = 30,
            AnalyzerUsed = true,
            AnalyzerModels = sidecarModels?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Segments = lines.ToList()
        }, new AnalysisRunInfo(modelVersion), [], 0.48);
        return (Assert.Single(await _store.GetClipsAsync(0.5)).Id, wav);
    }

    private (ModelManager Manager, SwappableEmbeddingModel Model) Models(string activeModelId, FakeSidecar sidecar, string? settingsPath = null)
    {
        var model = new SwappableEmbeddingModel(new FakeEmbeddingModel(activeModelId));
        var catalog = new EmbeddingModelCatalog(Path.Combine(_dir, "models"), () => [EntryA, EntryB]);
        var manager = new ModelManager(catalog, model, e => new FakeEmbeddingModel(e.ModelId), sidecar,
            settingsPath != null ? new UserSettingsStore(settingsPath) : null, _profiles);
        return (manager, model);
    }

    private ReanalysisService Service(SwappableEmbeddingModel model, FakeSidecar sidecar) =>
        new(new ClipAnalyzer(model, sidecar), _store, _profiles);

    [Fact]
    public async Task ReanalysisService_ReplacesTheClipAndRecordsFailedRuns()
    {
        var (clipId, wav) = await ScannedClipAsync("model-a@v1", null, Line(1, 3, "old words"));
        var sidecar = new FakeSidecar
        {
            Respond = _ => new SidecarScanResponse { Segments = [new DetectedSegment(1.1, 3.2, 0.9, speakerLabel: "SPEAKER_03", transcript: "new words")] }
        };
        var (_, model) = Models("model-a", sidecar);
        using var service = Service(model, sidecar);
        var finished = new List<long>();
        service.ClipFinished += finished.Add;

        service.Enqueue([new ReanalysisJob(clipId, wav, "match.wav", new SidecarAnalysisRequest(AnalysisPreset.Detailed))]);
        await service.WhenIdleAsync();

        Assert.Equal("new words", Assert.Single(await _store.GetUtterancesAsync()).Transcript);
        var run = (await _store.GetAnalysisRunsAsync(clipId))[0];
        Assert.Equal((AnalysisRunInfo.ReanalysisTrigger, AnalysisPreset.Detailed), (run.Trigger, run.Preset));
        Assert.False(service.IsRunning);
        Assert.Equal((1, 0), (service.Completed, service.Failed));

        sidecar.Respond = null; // the sidecar stops answering
        service.Enqueue([new ReanalysisJob(clipId, wav, "match.wav", SidecarAnalysisRequest.Standard)]);
        await service.WhenIdleAsync();

        Assert.Equal("new words", Assert.Single(await _store.GetUtterancesAsync()).Transcript);
        var failed = (await _store.GetAnalysisRunsAsync(clipId))[0];
        Assert.Equal(ClipAnalysisStatus.Error, failed.Status);
        Assert.Contains("Connection refused", failed.Error);
        Assert.Equal(1, service.Failed);
        Assert.Contains("1 failed", service.StatusText);
        Assert.Contains("Connection refused", service.StatusText);
    }

    [Fact]
    public async Task Clips_ReanalyseValidatesTheSectionAndCheckInDetailQueuesADetailedSection()
    {
        await ScannedClipAsync("model-a@v1", null, Line(12.2, 13.6, "you are garbage", insult: 0.9));
        var sidecar = new FakeSidecar
        {
            Respond = _ => new SidecarScanResponse { Segments = [new DetectedSegment(12.0, 14.0, 0.9, speakerLabel: "SPEAKER_00", transcript: "you are garbage, honestly")] }
        };
        var (manager, model) = Models("model-a", sidecar);
        using var service = Service(model, sidecar);
        var vm = new ClipsViewModel(_store, new ModerationSettings(), new ClipPlayer(new AudioPlaybackController(new NullOutput())), service, manager);
        await vm.RefreshAsync();
        vm.SelectedClip = vm.Clips[0];
        await vm.LoadSelectedClipAsync();

        Assert.False(vm.SelectedClip.IsOutdated);
        Assert.StartsWith("Voice: Model A", vm.ModelsText);
        Assert.Single(vm.Runs);

        vm.RangeFrom = "twelve";
        Assert.False(vm.ReanalyseSelected());
        Assert.Contains("m:ss", vm.StatusMessage);
        vm.RangeFrom = "0:20";
        vm.RangeTo = "0:10";
        Assert.False(vm.ReanalyseSelected());
        Assert.Empty(sidecar.Requests);

        vm.CheckLineCommand.Execute(vm.ClipTranscript[0]);
        await service.WhenIdleAsync();

        var request = Assert.Single(sidecar.Requests);
        Assert.Equal(AnalysisPreset.Detailed, request.Preset);
        Assert.Equal((7.0, 19.0), (request.StartSeconds!.Value, request.EndSeconds!.Value));
        Assert.Equal(("00:07", "00:19"), (vm.RangeFrom, vm.RangeTo));

        await vm.RefreshAsync();
        await vm.LoadSelectedClipAsync();
        Assert.Equal("you are garbage, honestly", Assert.Single(vm.ClipTranscript).Transcript);
        Assert.Equal(2, vm.Runs.Count);
        Assert.Contains("section 00:07 – 00:19", vm.Runs[0].TitleText);
    }

    [Fact]
    public async Task Clips_SectionOfAClipAnalysedWithAnotherModelIsRefused()
    {
        await ScannedClipAsync("model-a@v1", null, Line(1, 3, "hello"));
        var sidecar = new FakeSidecar();
        var (manager, model) = Models("model-b", sidecar);
        using var service = Service(model, sidecar);
        var vm = new ClipsViewModel(_store, new ModerationSettings(), new ClipPlayer(new AudioPlaybackController(new NullOutput())), service, manager);
        await vm.RefreshAsync();
        vm.SelectedClip = vm.Clips[0];

        Assert.True(vm.SelectedClip.IsOutdated);
        vm.RangeFrom = "0:05";
        vm.RangeTo = "0:10";
        Assert.False(vm.ReanalyseSelected());
        Assert.Contains("another voice model", vm.StatusMessage);
        Assert.Empty(sidecar.Requests);
    }

    [Fact]
    public async Task Models_SwitchesModelsAppliesSidecarChangesAndReanalysesOutdatedClips()
    {
        var sidecar = new FakeSidecar
        {
            Respond = _ => new SidecarScanResponse { Segments = [new DetectedSegment(1, 3, 0.9, speakerLabel: "SPEAKER_00", transcript: "hello again")] }
        };
        await ScannedClipAsync("model-a@v1", sidecar.Models, Line(1, 3, "hello"));
        Directory.CreateDirectory(_profiles);
        new VoiceProfile { ProfileName = "Alex", ModelVersion = "model-a@v1", Centroid = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f] }
            .SaveToFile(Path.Combine(_profiles, "alex.json"));

        string settingsPath = Path.Combine(_dir, "settings.json");
        var (manager, model) = Models("model-a", sidecar, settingsPath);
        using var service = Service(model, sidecar);
        var vm = new ModelsViewModel(manager, _store, service);
        await vm.RefreshAsync();

        Assert.Equal(["Model A", "Model B"], vm.EmbeddingModels.Select(m => m.DisplayName));
        Assert.True(vm.EmbeddingModels[0].IsActive);
        Assert.Equal(0, vm.OutdatedCount);
        Assert.Empty(vm.ReenrollText);
        Assert.Contains("5 of 5", vm.SidecarStatusText);

        vm.SelectedModel = vm.EmbeddingModels[1];
        Assert.True(vm.UseSelectedModelCommand.CanExecute(null));
        await vm.UseSelectedModelAsync();

        Assert.Equal("model-b", model.ModelId);
        Assert.True(vm.EmbeddingModels.Single(m => m.IsActive).Entry.ModelId == "model-b");
        Assert.Contains("Alex", vm.ReenrollText);
        Assert.Equal(1, vm.OutdatedCount);
        Assert.Equal("model-b", new UserSettingsStore(settingsPath).Current.EmbeddingModelId);

        sidecar.Broken.Add("detoxify-typo");
        vm.SidecarStages.Single(s => s.Stage == SidecarModelStages.Asr).Value = "large-v3";
        vm.SidecarStages.Single(s => s.Stage == SidecarModelStages.Moderation).Value = "detoxify-typo";
        await vm.ApplySidecarModelsAsync();

        var sent = Assert.Single(sidecar.SetCalls);
        Assert.Equal(["asr", "moderation"], sent.Keys.OrderBy(k => k));
        Assert.Equal("Loaded: large-v3", vm.SidecarStages.Single(s => s.Stage == SidecarModelStages.Asr).StateText);
        Assert.True(vm.SidecarStages.Single(s => s.Stage == SidecarModelStages.Moderation).HasError);
        Assert.Contains("Could not load: moderation", vm.StatusMessage);

        vm.ReanalysePreset = AnalysisPreset.Detailed;
        vm.ReanalyseOutdated();
        await service.WhenIdleAsync();
        await vm.RefreshAsync();

        var clip = Assert.Single(await _store.GetClipsAsync(0.5));
        Assert.Equal("model-b@v1", clip.EmbeddingModel);
        Assert.Equal("large-v3", clip.SidecarModels["asr"]);
        Assert.Equal(AnalysisPreset.Detailed, sidecar.Requests.Last().Preset);
        Assert.Equal(0, vm.OutdatedCount);
    }

    private sealed class NullOutput : IAudioOutput
    {
        public bool IsAvailable => true;
        public IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds) => new Session();

        private sealed class Session : IAudioOutputSession
        {
            public bool HasExited => false;
            public void Dispose() { }
        }
    }
}
