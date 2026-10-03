using System.Globalization;
using System.Text;
using System.Text.Json;
using VoiceScan.App.Core.Models;
using VoiceScan.Core;

namespace VoiceScan.App.Core.Services;

public sealed record ReportExportSettings(
    string ProfileName,
    string ModelId,
    string EngineVersion,
    double Threshold,
    double ClusterThreshold,
    bool TemporalSmoothing,
    DateTimeOffset ScanDateUtc);

public sealed record ExportResult(
    string CsvPath,
    string PdfPath,
    IReadOnlyList<string> ExtractedAudioClipPaths,
    int TotalSegmentsExported);

public sealed class EvidenceReportExporter
{
    public async Task<ExportResult> ExportReportAsync(
        IReadOnlyList<FileVerdictResult> results,
        ReportExportSettings settings,
        string outputDirectory,
        bool extractAudioClips = true,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string audioHitsDir = Path.Combine(outputDirectory, "audio_hits");
        if (extractAudioClips)
        {
            Directory.CreateDirectory(audioHitsDir);
        }

        string timestamp = settings.ScanDateUtc.ToString("yyyyMMdd_HHmmss");
        string csvPath = Path.Combine(outputDirectory, $"voicescan_evidence_{timestamp}.csv");
        string pdfPath = Path.Combine(outputDirectory, $"voicescan_evidence_{timestamp}.pdf");

        List<string> exportedAudioClips = [];
        int segmentCount = 0;

        // 1. Export CSV
        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("file_path,file_name,file_hash,file_duration_seconds,file_verdict,segment_id,start_time_seconds,end_time_seconds,duration_seconds,verdict,confidence,reason_flags,profile_name,model_id,engine_version,settings_snapshot,scan_date_utc,audio_clip_path");

        string settingsJson = JsonSerializer.Serialize(new
        {
            settings.Threshold,
            settings.ClusterThreshold,
            settings.TemporalSmoothing
        }).Replace("\"", "\"\"");

        foreach (var file in results)
        {
            if (file.Segments.Count == 0)
            {
                // Record file with no match
                csvBuilder.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\",\"{1}\",\"{2}\",{3:F2},\"{4}\",\"\",0,0,0,\"{4}\",0.000,\"[]\",\"{5}\",\"{6}\",\"{7}\",\"{8}\",\"{9}\",\"\"",
                    file.FilePath, file.FileName, file.FileHash, file.DurationSeconds, file.OverallVerdict,
                    settings.ProfileName, settings.ModelId, settings.EngineVersion, settingsJson, settings.ScanDateUtc.ToString("o")));
                continue;
            }

            for (int s = 0; s < file.Segments.Count; s++)
            {
                var seg = file.Segments[s];
                segmentCount++;
                string clipRelPath = "";

                if (extractAudioClips && File.Exists(file.FilePath) && seg.DurationSeconds > 0)
                {
                    string clipFileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}_seg_{s + 1}_{seg.StartTimeSeconds:F1}s_{seg.EndTimeSeconds:F1}s.wav";
                    string clipFullPath = Path.Combine(audioHitsDir, clipFileName);

                    try
                    {
                        await AudioDecoder.ExtractAudioSegmentAsync(
                            file.FilePath,
                            clipFullPath,
                            seg.StartTimeSeconds,
                            seg.DurationSeconds,
                            cancellationToken: cancellationToken);

                        clipRelPath = Path.Combine("audio_hits", clipFileName);
                        exportedAudioClips.Add(clipFullPath);
                    }
                    catch (Exception ex)
                    {
                        VoiceScan.Core.Logging.VoiceScanLogger.Warn("EvidenceReportExporter", $"Failed to extract clip {clipFileName}: {ex.Message}");
                    }
                }

                string flags = JsonSerializer.Serialize(seg.ReasonFlags).Replace("\"", "\"\"");
                csvBuilder.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\",\"{1}\",\"{2}\",{3:F2},\"{4}\",\"{5}\",{6:F2},{7:F2},{8:F2},\"{9}\",{10:F3},\"{11}\",\"{12}\",\"{13}\",\"{14}\",\"{15}\",\"{16}\",\"{17}\"",
                    file.FilePath, file.FileName, file.FileHash, file.DurationSeconds, file.OverallVerdict,
                    seg.SegmentId, seg.StartTimeSeconds, seg.EndTimeSeconds, seg.DurationSeconds,
                    seg.Verdict, seg.Confidence, flags,
                    settings.ProfileName, settings.ModelId, settings.EngineVersion, settingsJson,
                    settings.ScanDateUtc.ToString("o"), clipRelPath));
            }
        }

        await File.WriteAllTextAsync(csvPath, csvBuilder.ToString(), Encoding.UTF8, cancellationToken);

        // 2. Export PDF
        await GeneratePdfReportAsync(pdfPath, results, settings, cancellationToken);

        return new ExportResult(csvPath, pdfPath, exportedAudioClips, segmentCount);
    }

    private static async Task GeneratePdfReportAsync(
        string pdfPath,
        IReadOnlyList<FileVerdictResult> results,
        ReportExportSettings settings,
        CancellationToken cancellationToken)
    {
        // Generates compliant PDF 1.4 document
        var contentStream = new StringBuilder();

        void DrawText(double x, double y, string font, double size, string text, string rgb = "0 0 0")
        {
            contentStream.AppendLine("BT");
            contentStream.AppendLine(string.Format(CultureInfo.InvariantCulture, "/{0} {1:F1} Tf", font, size));
            contentStream.AppendLine($"{rgb} rg");
            contentStream.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} Td", x, y));
            contentStream.AppendLine($"({EscapePdf(text)}) Tj");
            contentStream.AppendLine("ET");
        }

        // Background header banner
        contentStream.AppendLine("0.05 0.10 0.18 rg");
        contentStream.AppendLine("36 760 523 50 re f");

        // Header Title
        DrawText(50, 780, "F2", 18, "VoiceScan Biometric Evidence Report", "1.0 1.0 1.0");

        // Subtitle & Metadata
        DrawText(50, 766, "F1", 9, $"Generated: {settings.ScanDateUtc:yyyy-MM-dd HH:mm:ss} UTC  |  Engine v{settings.EngineVersion}  |  100% Offline Biometrics", "0.8 0.8 0.8");

        // Overview Box
        contentStream.AppendLine("0.95 0.95 0.95 rg");
        contentStream.AppendLine("36 675 523 70 re f");
        contentStream.AppendLine("0.80 0.80 0.80 RG");
        contentStream.AppendLine("36 675 523 70 re S");

        DrawText(50, 725, "F2", 10, $"TARGET PROFILE: {settings.ProfileName}", "0.1 0.1 0.1");
        DrawText(50, 710, "F1", 9, $"Model ID: {settings.ModelId}   |   Score Threshold: {settings.Threshold:F2}   |   Cluster Dist Threshold: {settings.ClusterThreshold:F2}", "0.2 0.2 0.2");

        int matchFiles = results.Count(r => r.OverallVerdict.Equals("Match", StringComparison.OrdinalIgnoreCase));
        int possibleFiles = results.Count(r => r.OverallVerdict.Equals("Possible", StringComparison.OrdinalIgnoreCase));
        int noMatchFiles = results.Count(r => r.OverallVerdict.Equals("No match", StringComparison.OrdinalIgnoreCase));
        int totalSegments = results.Sum(r => r.Segments.Count);
        DrawText(50, 695, "F1", 9, $"Files Scanned: {results.Count}   |   Matches: {matchFiles}   |   Possible: {possibleFiles}   |   No Match: {noMatchFiles}   |   Total Hits: {totalSegments}", "0.2 0.2 0.2");

        // Table Header
        double y = 645;
        contentStream.AppendLine("0.15 0.25 0.35 rg");
        contentStream.AppendLine(string.Format(CultureInfo.InvariantCulture, "36 {0:F1} 523 20 re f", y));

        DrawText(42, y + 6, "F2", 9, "File Name", "1.0 1.0 1.0");
        DrawText(170, y + 6, "F2", 9, "Verdict", "1.0 1.0 1.0");
        DrawText(240, y + 6, "F2", 9, "Interval", "1.0 1.0 1.0");
        DrawText(330, y + 6, "F2", 9, "Confidence", "1.0 1.0 1.0");
        DrawText(400, y + 6, "F2", 9, "Reason Flags", "1.0 1.0 1.0");

        // Table Rows
        y -= 18;
        foreach (var file in results)
        {
            if (y < 60) break; // keep to single page summary or append

            if (file.Segments.Count == 0)
            {
                contentStream.AppendLine(string.Format(CultureInfo.InvariantCulture, "0.97 0.97 0.97 rg 36 {0:F1} 523 16 re f", y - 2));
                DrawText(42, y + 2, "F1", 8, Truncate(file.FileName, 22), "0.2 0.2 0.2");
                DrawText(170, y + 2, "F1", 8, file.OverallVerdict, "0.2 0.2 0.2");
                DrawText(240, y + 2, "F1", 8, "--", "0.2 0.2 0.2");
                DrawText(330, y + 2, "F1", 8, file.MaxConfidence.ToString("F3", CultureInfo.InvariantCulture), "0.2 0.2 0.2");
                DrawText(400, y + 2, "F1", 8, "(None)", "0.2 0.2 0.2");
                y -= 18;
                continue;
            }

            foreach (var seg in file.Segments)
            {
                if (y < 60) break;

                // Color verdict
                if (seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase))
                    contentStream.AppendLine(string.Format(CultureInfo.InvariantCulture, "0.90 0.97 0.92 rg 36 {0:F1} 523 16 re f", y - 2));
                else
                    contentStream.AppendLine(string.Format(CultureInfo.InvariantCulture, "0.98 0.95 0.90 rg 36 {0:F1} 523 16 re f", y - 2));

                string flagsStr = seg.ReasonFlags.Count > 0 ? string.Join(", ", seg.ReasonFlags) : "Clean";

                DrawText(42, y + 2, "F1", 8, Truncate(file.FileName, 22), "0.1 0.1 0.1");
                DrawText(170, y + 2, "F1", 8, seg.Verdict, "0.1 0.1 0.1");
                DrawText(240, y + 2, "F1", 8, string.Format(CultureInfo.InvariantCulture, "{0:F1}s - {1:F1}s", seg.StartTimeSeconds, seg.EndTimeSeconds), "0.1 0.1 0.1");
                DrawText(330, y + 2, "F1", 8, seg.Confidence.ToString("F3", CultureInfo.InvariantCulture), "0.1 0.1 0.1");
                DrawText(400, y + 2, "F1", 8, flagsStr, "0.1 0.1 0.1");

                y -= 18;
            }
        }

        // Footer Legal & Offline statement
        DrawText(36, 30, "F1", 8, "CONFIDENTIAL & BIOMETRICALLY VERIFIED  |  VoiceScan Offline Engine  |  Zero Network Telemetry", "0.5 0.5 0.5");

        byte[] streamBytes = Encoding.ASCII.GetBytes(contentStream.ToString());

        // Assemble PDF 1.4 Object Structure
        var pdf = new MemoryStream();
        using var writer = new StreamWriter(pdf, Encoding.ASCII, leaveOpen: true);
        writer.AutoFlush = true;

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("%\xE2\xE3\xCF\xD3");

        List<long> objOffsets = [];

        // Obj 1: Catalog
        objOffsets.Add(pdf.Position);
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");

        // Obj 2: Pages
        objOffsets.Add(pdf.Position);
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");

        // Obj 3: Page
        objOffsets.Add(pdf.Position);
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>");
        writer.WriteLine("endobj");

        // Obj 4: Content Stream
        objOffsets.Add(pdf.Position);
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {streamBytes.Length} >>");
        writer.WriteLine("stream");
        writer.Flush();
        pdf.Write(streamBytes, 0, streamBytes.Length);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        // Obj 5: Font F1 (Helvetica)
        objOffsets.Add(pdf.Position);
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");

        // Obj 6: Font F2 (Helvetica-Bold)
        objOffsets.Add(pdf.Position);
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        writer.WriteLine("endobj");

        // XRef Table
        long xrefPos = pdf.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objOffsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (long off in objOffsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        // Trailer
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {objOffsets.Count + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos);
        writer.WriteLine("%%EOF");
        writer.Flush();

        await File.WriteAllBytesAsync(pdfPath, pdf.ToArray(), cancellationToken);
    }

    private static string EscapePdf(string text)
    {
        return text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }

    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return text.Substring(0, maxLen - 2) + "..";
    }
}
