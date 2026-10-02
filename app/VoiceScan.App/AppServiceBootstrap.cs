using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;

namespace VoiceScan.App;

public static class AppServiceBootstrap
{
    public static MainAppViewModel CreateMainViewModel()
    {
        // 1. Initialize Core Engine Models (Silicon VAD & WeSpeaker)
        var embeddingModel = new OnnxEmbeddingModel();
        var vad = new SileroVad();
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, vad);

        // 2. Initialize Scanner & Database
        var scanDb = new VoiceScan.Core.Storage.VoiceScanDatabase();
        var scanner = new PipelineScanner(embeddingModel, vad, scanDb);

        // 3. Initialize Presentation Services
        var qualityAnalyzer = new AudioQualityAnalyzer(vad);
        var waveformService = new WaveformService();
        var playbackController = new AudioPlaybackController();
        var scanController = new BackgroundScanController(scanner, waveformService);
        var reviewRepo = new ReviewSqliteRepository("voice_scan_reviews.db", enrollmentService);

        // 4. Initialize ViewModels
        var enrollmentVm = new EnrollmentWizardViewModel(qualityAnalyzer, enrollmentService);
        var scanVm = new ScanDashboardViewModel(scanController);
        var resultsVm = new ResultsViewModel(playbackController);
        var reviewVm = new ReviewViewModel(reviewRepo, playbackController);
        var benchmarkService = new BenchmarkService();
        var benchmarkVm = new BenchmarkViewModel(benchmarkService);

        return new MainAppViewModel(enrollmentVm, scanVm, resultsVm, reviewVm, benchmarkVm);
    }
}
