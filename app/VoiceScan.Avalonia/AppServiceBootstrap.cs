using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.App;

public static class AppServiceBootstrap
{
    private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "settings.json");

    /// <summary>
    /// Loads and verifies the embedding model chosen on the Models page (the built-in default if it is unavailable);
    /// slow (hashing, session creation, warm-up), so call it off the UI thread.
    /// </summary>
    public static StartupModel LoadEmbeddingModel() => ModelManager.LoadStartupModel(
        new EmbeddingModelCatalog(),
        new UserSettingsStore(SettingsPath).Current.EmbeddingModelId,
        entry => new OnnxEmbeddingModel(entry),
        () => new OnnxEmbeddingModel());

    /// <summary>Builds the view models. Call on the UI thread: they capture its synchronization context.</summary>
    public static MainAppViewModel CreateMainViewModel(StartupModel startup)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);

        // Every consumer holds the swappable wrapper, so the Models page can replace the model while the app runs.
        var embeddingModel = new SwappableEmbeddingModel(startup.Model);
        var vad = new WebRtcVad();
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, vad);

        var scanDb = new VoiceScan.Core.Storage.VoiceScanDatabase();
        var sidecarClient = new VoiceScan.Core.Inference.SidecarClient();
        var scanner = new PipelineScanner(embeddingModel, vad, scanDb, sidecarClient: sidecarClient);

        var qualityAnalyzer = new AudioQualityAnalyzer(vad);
        var waveformService = new WaveformService();
        var playbackController = new AudioPlaybackController();
        var moderationStore = new VoiceScan.Core.Storage.ModerationStore();
        var scanController = new BackgroundScanController(scanner, waveformService, moderationStore);
        var reviewRepo = new ReviewSqliteRepository(Path.Combine(AppPaths.DataRoot, "voice_scan_reviews.db"), enrollmentService);

        var enrollmentVm = new EnrollmentWizardViewModel(qualityAnalyzer, enrollmentService);
        var settingsStore = new UserSettingsStore(SettingsPath);
        var scanVm = new ScanDashboardViewModel(scanController, settings: settingsStore);
        var resultsVm = new ResultsViewModel(playbackController);
        var reviewVm = new ReviewViewModel(reviewRepo, playbackController);

        var modelManager = new ModelManager(
            new EmbeddingModelCatalog(), embeddingModel, entry => new OnnxEmbeddingModel(entry), sidecarClient, settingsStore);
        var reanalysis = new ReanalysisService(new ClipAnalyzer(embeddingModel, sidecarClient), moderationStore);

        // Each moderation page owns its own player so selecting on one page never changes what another plays.
        var moderationSettings = new ModerationSettings(settingsStore);
        var incidentsVm = new IncidentsViewModel(moderationStore, moderationSettings, new ClipPlayer(new AudioPlaybackController()));
        var clipsVm = new ClipsViewModel(moderationStore, moderationSettings, new ClipPlayer(new AudioPlaybackController()), reanalysis, modelManager);
        var speakersVm = new SpeakersViewModel(moderationStore, moderationSettings, new ClipPlayer(new AudioPlaybackController()));
        var modelsVm = new ModelsViewModel(modelManager, moderationStore, reanalysis, startup.Warning);

        var main = new MainAppViewModel(enrollmentVm, scanVm, resultsVm, reviewVm, incidentsVm, clipsVm, speakersVm, modelsVm, settingsStore)
        {
            GpuStatusMessage = Banner(embeddingModel, startup.Warning)
        };
        embeddingModel.Swapped += () => main.OnEmbeddingModelChanged(Banner(embeddingModel, null));
        return main;
    }

    private static string? Banner(ISpeakerEmbeddingModel model, string? warning)
    {
        string? cpu = model.IsCudaActive ? null : $"Running on CPU ({model.ActiveProvider}). {OnnxEmbeddingModel.CudaSetupHint}";
        var parts = new[] { warning, cpu }.Where(p => p != null).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
