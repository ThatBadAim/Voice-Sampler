using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.App;

public static class AppServiceBootstrap
{
    public static MainAppViewModel CreateMainViewModel()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);

        var embeddingModel = new OnnxEmbeddingModel();
        var vad = new SileroVad();
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, vad);

        var scanDb = new VoiceScan.Core.Storage.VoiceScanDatabase();
        var scanner = new PipelineScanner(embeddingModel, vad, scanDb);

        var qualityAnalyzer = new AudioQualityAnalyzer(vad);
        var waveformService = new WaveformService();
        var playbackController = new AudioPlaybackController();
        var scanController = new BackgroundScanController(scanner, waveformService);
        var reviewRepo = new ReviewSqliteRepository(Path.Combine(AppPaths.DataRoot, "voice_scan_reviews.db"), enrollmentService);

        var enrollmentVm = new EnrollmentWizardViewModel(qualityAnalyzer, enrollmentService);
        var scanVm = new ScanDashboardViewModel(scanController);
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
