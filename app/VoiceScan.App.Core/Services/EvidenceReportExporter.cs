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
    DateTimeOffset ScanDateUtc,
    bool ClusteringEnabled = true);

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

        string timestamp = settings.ScanDateUtc.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string csvPath = Path.Combine(outputDirectory, $"voicescan_evidence_{timestamp}.csv");
        string pdfPath = Path.Combine(outputDirectory, $"voicescan_evidence_{timestamp}.pdf");

        List<string> exportedAudioClips = [];
        int segmentCount = 0;

        // 1. Export CSV
        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("file_path,file_name,file_hash,file_duration_seconds,file_verdict,file_max_confidence,error_message,segment_id,start_time_seconds,end_time_seconds,duration_seconds,verdict,confidence,reason_flags,profile_name,model_id,engine_version,settings_snapshot,scan_date_utc,audio_track_index,audio_clip_path");

        string settingsJson = JsonSerializer.Serialize(new
        {
            settings.Threshold,
            settings.ClusteringEnabled,
            settings.ClusterThreshold,
            settings.TemporalSmoothing
        });
        string scanDate = settings.ScanDateUtc.ToString("o", CultureInfo.InvariantCulture);

        foreach (var file in results)
        {
            string fileColumns = string.Join(",",
                Csv(file.FilePath), Csv(file.FileName), Csv(file.FileHash), Num(file.DurationSeconds, "F2"),
                Csv(file.OverallVerdict), Num(file.MaxConfidence, "F4"), Csv(file.ErrorMessage ?? ""));
            string settingsColumns = string.Join(",",
                Csv(settings.ProfileName), Csv(settings.ModelId), Csv(settings.EngineVersion), Csv(settingsJson), Csv(scanDate),
                file.AudioTrackIndex.ToString(CultureInfo.InvariantCulture));

            if (file.Segments.Count == 0)
            {
                // One row per file even without hits, so the report also documents every negative and failed file.
                csvBuilder.AppendLine(string.Join(",", fileColumns, "\"\"", "", "", "", "\"\"", "", "\"[]\"", settingsColumns, "\"\""));
                continue;
            }

            string clipTag = ClipTag(file);
            for (int s = 0; s < file.Segments.Count; s++)
            {
                var seg = file.Segments[s];
                segmentCount++;
                string clipRelPath = "";

                if (extractAudioClips && File.Exists(file.FilePath) && seg.DurationSeconds > 0)
                {
                    // The tag (content hash, else full path) keeps clips of same-named files in different folders apart.
                    string clipFileName = string.Create(CultureInfo.InvariantCulture,
                        $"{Path.GetFileNameWithoutExtension(file.FileName)}_{clipTag}_seg_{s + 1}_{seg.StartTimeSeconds:F1}s_{seg.EndTimeSeconds:F1}s.wav");
                    string clipFullPath = Path.Combine(audioHitsDir, clipFileName);

                    try
                    {
                        await AudioDecoder.ExtractAudioSegmentAsync(
                            file.FilePath,
                            clipFullPath,
                            seg.StartTimeSeconds,
                            seg.DurationSeconds,
                            audioTrackIndex: file.AudioTrackIndex,
                            cancellationToken: cancellationToken);

                        clipRelPath = Path.Combine("audio_hits", clipFileName);
                        exportedAudioClips.Add(clipFullPath);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        VoiceScan.Core.Logging.VoiceScanLogger.Warn("EvidenceReportExporter", $"Failed to extract clip {clipFileName}: {ex.Message}");
                    }
                }

                csvBuilder.AppendLine(string.Join(",",
                    fileColumns,
                    Csv(seg.SegmentId), Num(seg.StartTimeSeconds, "F2"), Num(seg.EndTimeSeconds, "F2"), Num(seg.DurationSeconds, "F2"),
                    Csv(seg.Verdict), Num(seg.Confidence, "F4"), Csv(JsonSerializer.Serialize(seg.ReasonFlags)),
                    settingsColumns, Csv(clipRelPath)));
            }
        }

        await File.WriteAllTextAsync(csvPath, csvBuilder.ToString(), Encoding.UTF8, cancellationToken);

        // 2. Export PDF
        await GeneratePdfReportAsync(pdfPath, results, settings, cancellationToken);

        return new ExportResult(csvPath, pdfPath, exportedAudioClips, segmentCount);
    }

    private sealed record PdfRow(string FileName, string Verdict, string Interval, string Confidence, string Flags, bool IsMatch, bool IsPossible);

    private static async Task GeneratePdfReportAsync(
        string pdfPath,
        IReadOnlyList<FileVerdictResult> results,
        ReportExportSettings settings,
        CancellationToken cancellationToken)
    {
        // 1. Flatten all display rows
        var allRows = new List<PdfRow>();

        foreach (var file in results)
        {
            if (file.Segments.Count == 0)
            {
                allRows.Add(new PdfRow(
                    TruncateMiddle(file.FileName, 26),
                    file.OverallVerdict,
                    "--",
                    file.MaxConfidence.ToString("F3", CultureInfo.InvariantCulture),
                    file.IsError ? TruncateMiddle(file.ErrorMessage ?? "Scan failed", 40) : "(None)",
                    false,
                    false));
            }
            else
            {
                foreach (var seg in file.Segments)
                {
                    bool isMatch = seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase);
                    bool isPoss = seg.Verdict.Equals("Possible", StringComparison.OrdinalIgnoreCase);
                    string flagsStr = seg.ReasonFlags.Count > 0 ? string.Join(", ", seg.ReasonFlags) : "Clean";
                    allRows.Add(new PdfRow(
                        TruncateMiddle(file.FileName, 26),
                        seg.Verdict,
                        string.Format(CultureInfo.InvariantCulture, "{0:F1}s - {1:F1}s", seg.StartTimeSeconds, seg.EndTimeSeconds),
                        seg.Confidence.ToString("F3", CultureInfo.InvariantCulture),
                        flagsStr,
                        isMatch,
                        isPoss));
                }
            }
        }

        // 2. Partition rows into pages
        const int page1Capacity = 32;
        const int subsequentPageCapacity = 38;

        var pages = new List<List<PdfRow>>();
        if (allRows.Count <= page1Capacity)
        {
            pages.Add(allRows);
        }
        else
        {
            pages.Add(allRows.Take(page1Capacity).ToList());
            int offset = page1Capacity;
            while (offset < allRows.Count)
            {
                int count = Math.Min(subsequentPageCapacity, allRows.Count - offset);
                pages.Add(allRows.Skip(offset).Take(count).ToList());
                offset += count;
            }
        }

        int totalPages = pages.Count;
        var pageStreams = new List<byte[]>();

        for (int p = 0; p < totalPages; p++)
        {
            var contentStream = new StringBuilder();
            void AppendLine(string line) => contentStream.Append(line).Append('\n');

            void DrawText(double x, double y, string font, double size, string text, string rgb = "0 0 0")
            {
                AppendLine("BT");
                AppendLine(string.Format(CultureInfo.InvariantCulture, "/{0} {1:F1} Tf", font, size));
                AppendLine($"{rgb} rg");
                AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} Td", x, y));
                AppendLine($"({EscapePdf(text)}) Tj");
                AppendLine("ET");
            }

            double y;
            if (p == 0)
            {
                // Background header banner
                AppendLine("0.05 0.10 0.18 rg");
                AppendLine("36 760 523 50 re f");

                // Header Title
                DrawText(50, 780, "F2", 18, "VoiceScan Biometric Evidence Report", "1.0 1.0 1.0");

                // Subtitle & Metadata
                DrawText(50, 766, "F1", 9, string.Create(CultureInfo.InvariantCulture, $"Scan started: {settings.ScanDateUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC  |  Engine v{settings.EngineVersion}  |  Processed offline"), "0.8 0.8 0.8");

                // Overview Box
                AppendLine("0.95 0.95 0.95 rg");
                AppendLine("36 675 523 70 re f");
                AppendLine("0.80 0.80 0.80 RG");
                AppendLine("36 675 523 70 re S");

                DrawText(50, 725, "F2", 10, $"TARGET PROFILE: {settings.ProfileName}", "0.1 0.1 0.1");
                DrawText(50, 710, "F1", 9, string.Create(CultureInfo.InvariantCulture, $"Model ID: {settings.ModelId}   |   Score Threshold: {settings.Threshold:F2}   |   Clustering: {(settings.ClusteringEnabled ? $"on, distance {settings.ClusterThreshold:F2}" : "off")}   |   Smoothing: {(settings.TemporalSmoothing ? "on" : "off")}"), "0.2 0.2 0.2");

                int matchFiles = results.Count(r => r.OverallVerdict.Equals("Match", StringComparison.OrdinalIgnoreCase));
                int possibleFiles = results.Count(r => r.OverallVerdict.Equals("Possible", StringComparison.OrdinalIgnoreCase));
                int noMatchFiles = results.Count(r => r.OverallVerdict.Equals("No match", StringComparison.OrdinalIgnoreCase));
                int errorFiles = results.Count(r => r.IsError);
                int totalSegments = results.Sum(r => r.Segments.Count);
                DrawText(50, 695, "F1", 9, $"Files Scanned: {results.Count}   |   Matches: {matchFiles}   |   Possible: {possibleFiles}   |   No Match: {noMatchFiles}   |   Errors: {errorFiles}   |   Total Hits: {totalSegments}", "0.2 0.2 0.2");

                // Table Header
                y = 645;
                AppendLine("0.15 0.25 0.35 rg");
                AppendLine(string.Format(CultureInfo.InvariantCulture, "36 {0:F1} 523 20 re f", y));

                DrawText(42, y + 6, "F2", 9, "File Name", "1.0 1.0 1.0");
                DrawText(170, y + 6, "F2", 9, "Verdict", "1.0 1.0 1.0");
                DrawText(240, y + 6, "F2", 9, "Interval", "1.0 1.0 1.0");
                DrawText(330, y + 6, "F2", 9, "Confidence", "1.0 1.0 1.0");
                DrawText(400, y + 6, "F2", 9, "Reason Flags", "1.0 1.0 1.0");

                y -= 18;
            }
            else
            {
                // Compact header on subsequent pages
                AppendLine("0.05 0.10 0.18 rg");
                AppendLine("36 780 523 30 re f");

                DrawText(50, 792, "F2", 12, $"VoiceScan Biometric Evidence Report (Cont. - Page {p + 1} of {totalPages})", "1.0 1.0 1.0");
                DrawText(380, 792, "F1", 9, $"Target: {settings.ProfileName}", "0.8 0.8 0.8");

                // Table Header
                y = 750;
                AppendLine("0.15 0.25 0.35 rg");
                AppendLine(string.Format(CultureInfo.InvariantCulture, "36 {0:F1} 523 20 re f", y));

                DrawText(42, y + 6, "F2", 9, "File Name", "1.0 1.0 1.0");
                DrawText(170, y + 6, "F2", 9, "Verdict", "1.0 1.0 1.0");
                DrawText(240, y + 6, "F2", 9, "Interval", "1.0 1.0 1.0");
                DrawText(330, y + 6, "F2", 9, "Confidence", "1.0 1.0 1.0");
                DrawText(400, y + 6, "F2", 9, "Reason Flags", "1.0 1.0 1.0");

                y -= 18;
            }

            // Draw Page Rows
            foreach (var row in pages[p])
            {
                if (row.IsMatch)
                    AppendLine(string.Format(CultureInfo.InvariantCulture, "0.90 0.97 0.92 rg 36 {0:F1} 523 16 re f", y - 2));
                else if (row.IsPossible)
                    AppendLine(string.Format(CultureInfo.InvariantCulture, "0.98 0.95 0.90 rg 36 {0:F1} 523 16 re f", y - 2));
                else
                    AppendLine(string.Format(CultureInfo.InvariantCulture, "0.97 0.97 0.97 rg 36 {0:F1} 523 16 re f", y - 2));

                DrawText(42, y + 2, "F1", 8, row.FileName, "0.1 0.1 0.1");
                DrawText(170, y + 2, "F1", 8, row.Verdict, "0.1 0.1 0.1");
                DrawText(240, y + 2, "F1", 8, row.Interval, "0.1 0.1 0.1");
                DrawText(330, y + 2, "F1", 8, row.Confidence, "0.1 0.1 0.1");
                DrawText(400, y + 2, "F1", 8, row.Flags, "0.1 0.1 0.1");

                y -= 18;
            }

            // Footer Legal & Offline statement with page numbering
            DrawText(36, 30, "F1", 8, $"CONFIDENTIAL  |  Confidence is a similarity score, not a calibrated probability  |  Page {p + 1} of {totalPages}", "0.5 0.5 0.5");

            pageStreams.Add(Encoding.Latin1.GetBytes(contentStream.ToString()));
        }

        // Assemble PDF 1.4 Object Structure
        var pdf = new MemoryStream();
        // PDF cross-reference entries must be exactly 20 bytes, so lines end in LF on every platform.
        using var writer = new StreamWriter(pdf, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };
        writer.AutoFlush = true;

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("%\xE2\xE3\xCF\xD3");

        var objOffsets = new Dictionary<int, long>();

        // Obj 1: Catalog
        objOffsets[1] = pdf.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");

        // Object ID layout:
        // 1: Catalog
        // 2: Pages
        // 3 to 2 + 2*totalPages: Pairs of (Page, ContentStream)
        // Font F1: 2 + 2*totalPages + 1
        // Font F2: 2 + 2*totalPages + 2
        int fontF1Id = 2 + (2 * totalPages) + 1;
        int fontF2Id = 2 + (2 * totalPages) + 2;

        // Obj 2: Pages
        var kidsList = string.Join(" ", Enumerable.Range(0, totalPages).Select(i => $"{3 + (2 * i)} 0 R"));
        objOffsets[2] = pdf.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine($"<< /Type /Pages /Kids [{kidsList}] /Count {totalPages} >>");
        writer.WriteLine("endobj");

        for (int i = 0; i < totalPages; i++)
        {
            int pageObjId = 3 + (2 * i);
            int streamObjId = pageObjId + 1;
            byte[] sBytes = pageStreams[i];

            // Page Obj
            objOffsets[pageObjId] = pdf.Position;
            writer.WriteLine($"{pageObjId} 0 obj");
            writer.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {streamObjId} 0 R /Resources << /Font << /F1 {fontF1Id} 0 R /F2 {fontF2Id} 0 R >> >> >>");
            writer.WriteLine("endobj");

            // Content Stream Obj
            objOffsets[streamObjId] = pdf.Position;
            writer.WriteLine($"{streamObjId} 0 obj");
            writer.WriteLine($"<< /Length {sBytes.Length} >>");
            writer.WriteLine("stream");
            writer.Flush();
            pdf.Write(sBytes, 0, sBytes.Length);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
        }

        // Font F1 (Helvetica)
        objOffsets[fontF1Id] = pdf.Position;
        writer.WriteLine($"{fontF1Id} 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");

        // Font F2 (Helvetica-Bold)
        objOffsets[fontF2Id] = pdf.Position;
        writer.WriteLine($"{fontF2Id} 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        writer.WriteLine("endobj");

        // XRef Table
        long xrefPos = pdf.Position;
        int totalObjs = fontF2Id;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {totalObjs + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (int id = 1; id <= totalObjs; id++)
        {
            long off = objOffsets[id];
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        // Trailer
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {totalObjs + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos);
        writer.WriteLine("%%EOF");
        writer.Flush();

        await File.WriteAllBytesAsync(pdfPath, pdf.ToArray(), cancellationToken);
    }

    private static string EscapePdf(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '(') sb.Append("\\(");
            else if (c == ')') sb.Append("\\)");
            else if (c >= 32 && c <= 126) sb.Append(c);
            else if (c >= 160 && c <= 255) sb.Append(c);
            else sb.Append('?');
        }
        return sb.ToString();
    }

    /// <summary>Keeps both ends of a long name so files that share a prefix stay distinguishable.</summary>
    private static string TruncateMiddle(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        int head = (maxLen - 2) / 2;
        int tail = maxLen - 2 - head;
        return string.Concat(text.AsSpan(0, head), "..", text.AsSpan(text.Length - tail));
    }

    /// <summary>
    /// Quotes a CSV text field (RFC 4180) and neutralizes values that spreadsheet programs would run as formulas.
    /// </summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string ClipTag(FileVerdictResult file)
    {
        string source = string.IsNullOrEmpty(file.FileHash) ? Path.GetFullPath(file.FilePath) : file.FileHash;
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..8];
    }
}
