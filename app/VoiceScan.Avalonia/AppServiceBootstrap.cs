using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.App;

public static class AppServiceBootstrap
{
    /// <summary>Loads and verifies the embedding model; slow (hashing, session creation, warm-up), so call it off the UI thread.</summary>
    public static OnnxEmbeddingModel LoadEmbeddingModel() => new();

    /// <summary>Builds the view models. Call on the UI thread: they capture its synchronization context.</summary>
    public static MainAppViewModel CreateMainViewModel(OnnxEmbeddingModel embeddingModel)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);

        var vad = new WebRtcVad();
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, vad);

        var scanDb = new VoiceScan.Core.Storage.VoiceScanDatabase();
        var scanner = new PipelineScanner(embeddingModel, vad, scanDb);

        var qualityAnalyzer = new AudioQualityAnalyzer(vad);
        var waveformService = new WaveformService();
        var playbackController = new AudioPlaybackController();
        var scanController = new BackgroundScanController(scanner, waveformService);
        var reviewRepo = new ReviewSqliteRepository(Path.Combine(AppPaths.DataRoot, "voice_scan_reviews.db"), enrollmentService);

        var enrollmentVm = new EnrollmentWizardViewModel(qualityAnalyzer, enrollmentService);
        var scanVm = new ScanDashboardViewModel(
            scanController,
            settings: new UserSettingsStore(Path.Combine(AppPaths.DataRoot, "settings.json")));
        var resultsVm = new ResultsViewModel(playbackController);
        var reviewVm = new ReviewViewModel(reviewRepo, playbackController);

        return new MainAppViewModel(enrollmentVm, scanVm, resultsVm, reviewVm)
        {
            GpuStatusMessage = embeddingModel.IsCudaActive
                ? null
                : $"Running on CPU ({embeddingModel.ActiveProvider}). {OnnxEmbeddingModel.CudaSetupHint}"
        };
    }
}
