using System.Text.Json;
using VoiceScan.App.Core.Models;

namespace VoiceScan.App.Core.Services;

public interface IBenchmarkService
{
    Task<IReadOnlyList<BenchmarkReportSummary>> LoadAvailableReportsAsync(string reportsRoot = "eval/reports", CancellationToken cancellationToken = default);
    Task<BenchmarkReportSummary?> LoadReportFromFileAsync(string jsonFilePath, CancellationToken cancellationToken = default);
}

public sealed class BenchmarkService : IBenchmarkService
{
    public async Task<IReadOnlyList<BenchmarkReportSummary>> LoadAvailableReportsAsync(string reportsRoot = "eval/reports", CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(reportsRoot))
        {
            string[] searchAncestors = [
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, reportsRoot)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../..", reportsRoot)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", reportsRoot)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", reportsRoot))
            ];
            foreach (var candidate in searchAncestors)
            {
                if (Directory.Exists(candidate))
                {
                    reportsRoot = candidate;
                    break;
                }
            }
        }

        if (!Directory.Exists(reportsRoot))
        {
            return Array.Empty<BenchmarkReportSummary>();
        }

        var list = new List<BenchmarkReportSummary>();
        var jsonFiles = Directory.GetFiles(reportsRoot, "eval_results.json", SearchOption.AllDirectories);

        foreach (var file in jsonFiles)
        {
            try
            {
                var summary = await LoadReportFromFileAsync(file, cancellationToken);
                if (summary != null)
                {
                    list.Add(summary);
                }
            }
            catch
            {
                // Skip malformed individual report files
            }
        }

        return list.OrderByDescending(r => r.TimestampUtc).ToList();
    }

    public async Task<BenchmarkReportSummary?> LoadReportFromFileAsync(string jsonFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jsonFilePath)) return null;

        string json = await File.ReadAllTextAsync(jsonFilePath, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string reportDirName = Path.GetFileName(Path.GetDirectoryName(jsonFilePath) ?? "report");

        // Parse Metadata
        var meta = root.GetProperty("metadata");
        string timestampStr = meta.GetProperty("timestamp").GetString() ?? DateTime.UtcNow.ToString("o");
        DateTimeOffset timestamp = DateTimeOffset.TryParse(timestampStr, out var dto) ? dto : DateTimeOffset.UtcNow;
        string gitCommit = meta.TryGetProperty("git_commit", out var gc) ? (gc.GetString() ?? "") : "";
        string profile = meta.TryGetProperty("profile", out var pr) ? (pr.GetString() ?? "") : "unknown";
        string split = meta.TryGetProperty("split", out var sp) ? (sp.GetString() ?? "") : "dev";
        double totalDuration = meta.TryGetProperty("total_audio_duration_seconds", out var td) ? td.GetDouble() : 0.0;
        double elapsedSec = meta.TryGetProperty("elapsed_seconds", out var es) ? es.GetDouble() : 0.0;
        double speedMultiple = meta.TryGetProperty("speed_realtime_multiple", out var sm) ? sm.GetDouble() : 0.0;

        // Parse Metrics
        var metrics = root.GetProperty("metrics");
        double recall = metrics.TryGetProperty("recall", out var rc) ? rc.GetDouble() : 0.0;
        double precision = metrics.TryGetProperty("precision", out var pc) ? pc.GetDouble() : 0.0;
        double faPerHour = metrics.TryGetProperty("fa_per_hour", out var fa) ? fa.GetDouble() : 0.0;
        double timingError = metrics.TryGetProperty("mean_timing_error_sec", out var te) ? te.GetDouble() : 0.0;

        // Parse Overall CIs
        double recallLow = recall, recallHigh = recall;
        double precLow = precision, precHigh = precision;
        double faLow = faPerHour, faHigh = faPerHour;

        if (root.TryGetProperty("confidence_intervals", out var ciObj))
        {
            if (ciObj.TryGetProperty("recall", out var rci) && rci.GetArrayLength() == 2)
            {
                recallLow = rci[0].GetDouble();
                recallHigh = rci[1].GetDouble();
            }
            if (ciObj.TryGetProperty("precision", out var pci) && pci.GetArrayLength() == 2)
            {
                precLow = pci[0].GetDouble();
                precHigh = pci[1].GetDouble();
            }
            if (ciObj.TryGetProperty("fa_per_hour", out var fci) && fci.GetArrayLength() == 2)
            {
                faLow = fci[0].GetDouble();
                faHigh = fci[1].GetDouble();
            }
        }

        // Parse SNR Breakdown
        List<SnrTierBenchmarkItem> snrTiers = [];
        if (root.TryGetProperty("snr_breakdown", out var snrObj))
        {
            foreach (var tierProp in snrObj.EnumerateObject())
            {
                string tierName = tierProp.Name;
                var tierMetrics = tierProp.Value.GetProperty("metrics");
                int files = tierMetrics.TryGetProperty("total_files", out var tf) ? (int)tf.GetDouble() : 0;
                double tRecall = tierMetrics.TryGetProperty("recall", out var tr) ? tr.GetDouble() : 0.0;
                double tPrecision = tierMetrics.TryGetProperty("precision", out var tp) ? tp.GetDouble() : 0.0;
                double tFa = tierMetrics.TryGetProperty("fa_per_hour", out var tfp) ? tfp.GetDouble() : 0.0;
                double tTiming = tierMetrics.TryGetProperty("mean_timing_error_sec", out var tt) ? tt.GetDouble() : 0.0;

                double trLow = tRecall, trHigh = tRecall;
                double tpLow = tPrecision, tpHigh = tPrecision;
                double tfLow = tFa, tfHigh = tFa;

                if (tierProp.Value.TryGetProperty("confidence_intervals", out var tierCi))
                {
                    if (tierCi.TryGetProperty("recall", out var trci) && trci.GetArrayLength() == 2)
                    {
                        trLow = trci[0].GetDouble();
                        trHigh = trci[1].GetDouble();
                    }
                    if (tierCi.TryGetProperty("precision", out var tpci) && tpci.GetArrayLength() == 2)
                    {
                        tpLow = tpci[0].GetDouble();
                        tpHigh = tpci[1].GetDouble();
                    }
                    if (tierCi.TryGetProperty("fa_per_hour", out var tfci) && tfci.GetArrayLength() == 2)
                    {
                        tfLow = tfci[0].GetDouble();
                        tfHigh = tfci[1].GetDouble();
                    }
                }

                snrTiers.Add(new SnrTierBenchmarkItem(
                    TierName: tierName,
                    TotalFiles: files,
                    Recall: tRecall,
                    RecallLowCi: trLow,
                    RecallHighCi: trHigh,
                    Precision: tPrecision,
                    PrecisionLowCi: tpLow,
                    PrecisionHighCi: tpHigh,
                    FaPerHour: tFa,
                    FaLowCi: tfLow,
                    FaHighCi: tfHigh,
                    MeanTimingErrorSec: tTiming));
            }
        }

        string title = FormatReportTitle(reportDirName);

        return new BenchmarkReportSummary(
            ReportId: reportDirName,
            ReportTitle: title,
            TimestampUtc: timestamp,
            GitCommit: gitCommit,
            TargetProfile: profile,
            Split: split,
            TotalAudioDurationSeconds: totalDuration,
            ElapsedSeconds: elapsedSec,
            SpeedMultiple: speedMultiple,
            OverallRecall: recall,
            OverallRecallLowCi: recallLow,
            OverallRecallHighCi: recallHigh,
            OverallPrecision: precision,
            OverallPrecisionLowCi: precLow,
            OverallPrecisionHighCi: precHigh,
            OverallFaPerHour: faPerHour,
            OverallFaLowCi: faLow,
            OverallFaHighCi: faHigh,
            MeanTimingErrorSec: timingError,
            SnrTiers: snrTiers);
    }

    private static string FormatReportTitle(string dirName) => dirName switch
    {
        "step1_clustering" => "Step 1: Speaker Clustering (AHC, τ=0.40)",
        "step2_temporal_smoothing" => "Step 2: Temporal Smoothing & Aggregation",
        "step3_asnorm" => "Step 3: AS-Norm Impostor Normalization",
        "step4_three_state" => "Step 4: Three-State Verdicts & Reason Flags",
        "step5_multi_condition" => "Step 5: Multi-Condition Enrollment",
        "baseline_cli_initial" => "Baseline (Naive Cosine T=0.48)",
        "latest" => "Latest Harness Evaluation Run",
        _ => dirName.Replace('_', ' ')
    };
}
