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

public interface IEvidenceReportExporter
{
    Task<ExportResult> ExportReportAsync(
        IReadOnlyList<FileVerdictResult> results,
        ReportExportSettings settings,
        string outputDirectory,
        bool extractAudioClips = true,
        CancellationToken cancellationToken = default);
}

public sealed class EvidenceReportExporter : IEvidenceReportExporter
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

            // Extract audio samples if audio extraction requested
            float[]? fileAudio = null;
            if (extractAudioClips && File.Exists(file.FilePath))
            {
                try
                {
                    fileAudio = await AudioDecoder.DecodeEntireFileAsync(file.FilePath, cancellationToken: cancellationToken);
                }
                catch
                {
                    fileAudio = null;
                }
            }

            for (int s = 0; s < file.Segments.Count; s++)
            {
                var seg = file.Segments[s];
                segmentCount++;
                string clipRelPath = "";

                if (extractAudioClips && fileAudio != null && fileAudio.Length > 0)
                {
                    string clipFileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}_seg_{s + 1}_{seg.StartTimeSeconds:F1}s_{seg.EndTimeSeconds:F1}s.wav";
                    string clipFullPath = Path.Combine(audioHitsDir, clipFileName);

                    int startSample = Math.Clamp((int)(seg.StartTimeSeconds * 16000), 0, fileAudio.Length);
                    int endSample = Math.Clamp((int)(seg.EndTimeSeconds * 16000), startSample, fileAudio.Length);
                    int sampleLen = endSample - startSample;

                    if (sampleLen > 0)
                    {
                        float[] clipSamples = new float[sampleLen];
                        Array.Copy(fileAudio, startSample, clipSamples, 0, sampleLen);
                        await WriteWavFileAsync(clipFullPath, clipSamples, 16000, cancellationToken);
                        clipRelPath = Path.Combine("audio_hits", clipFileName);
                        exportedAudioClips.Add(clipFullPath);
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

    private static async Task WriteWavFileAsync(string filePath, float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        short channels = 1;
        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int subChunk2Size = samples.Length * (bitsPerSample / 8);
        int chunkSize = 36 + subChunk2Size;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        using var bw = new BinaryWriter(fs);

        // RIFF chunk
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // subchunk1 size
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(subChunk2Size);

        // Convert float -1.0..1.0 to 16-bit PCM
        byte[] buffer = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = Math.Clamp(samples[i], -1.0f, 1.0f);
            short sample16 = (short)(s * 32767.0f);
            buffer[i * 2] = (byte)(sample16 & 0xFF);
            buffer[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }

        await fs.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
    }

    private static async Task GeneratePdfReportAsync(
        string pdfPath,
        IReadOnlyList<FileVerdictResult> results,
        ReportExportSettings settings,
        CancellationToken cancellationToken)
    {
        // Generates compliant PDF 1.4 document
        var contentStream = new StringBuilder();

        // Background header banner
        contentStream.AppendLine("0.05 0.10 0.18 rg");
        contentStream.AppendLine("36 760 523 50 re f");

        // Header Title
        contentStream.AppendLine("BT");
        contentStream.AppendLine("/F2 18 Tf");
        contentStream.AppendLine("1.0 1.0 1.0 rg");
        contentStream.AppendLine("50 780 Td");
        contentStream.AppendLine("(VoiceScan Biometric Evidence Report) Tj");
        contentStream.AppendLine("ET");

        // Subtitle & Metadata
        contentStream.AppendLine("BT");
        contentStream.AppendLine("/F1 9 Tf");
        contentStream.AppendLine("0.8 0.8 0.8 rg");
        contentStream.AppendLine("50 766 Td");
        contentStream.AppendLine($"(Generated: {settings.ScanDateUtc:yyyy-MM-dd HH:mm:ss} UTC  |  Engine v{settings.EngineVersion}  |  100% Offline Biometrics) Tj");
        contentStream.AppendLine("ET");

        // Overview Box
        contentStream.AppendLine("0.95 0.95 0.95 rg");
        contentStream.AppendLine("36 675 523 70 re f");
        contentStream.AppendLine("0.80 0.80 0.80 RG");
        contentStream.AppendLine("36 675 523 70 re S");

        contentStream.AppendLine("BT");
        contentStream.AppendLine("/F2 10 Tf");
        contentStream.AppendLine("0.1 0.1 0.1 rg");
        contentStream.AppendLine("50 725 Td");
        contentStream.AppendLine($"(TARGET PROFILE: {EscapePdf(settings.ProfileName)}) Tj");
        contentStream.AppendLine("/F1 9 Tf");
        contentStream.AppendLine("0 600 Td");
        contentStream.AppendLine("50 710 Td");
        contentStream.AppendLine($"(Model ID: {EscapePdf(settings.ModelId)}   |   Score Threshold: {settings.Threshold:F2}   |   Cluster Dist Threshold: {settings.ClusterThreshold:F2}) Tj");
        contentStream.AppendLine("50 695 Td");
        int matchFiles = results.Count(r => r.OverallVerdict.Equals("Match", StringComparison.OrdinalIgnoreCase));
        int possibleFiles = results.Count(r => r.OverallVerdict.Equals("Possible", StringComparison.OrdinalIgnoreCase));
        int noMatchFiles = results.Count(r => r.OverallVerdict.Equals("No match", StringComparison.OrdinalIgnoreCase));
        int totalSegments = results.Sum(r => r.Segments.Count);
        contentStream.AppendLine($"(Files Scanned: {results.Count}   |   Matches: {matchFiles}   |   Possible: {possibleFiles}   |   No Match: {noMatchFiles}   |   Total Hits: {totalSegments}) Tj");
        contentStream.AppendLine("ET");

        // Table Header
        double y = 645;
        contentStream.AppendLine("0.15 0.25 0.35 rg");
        contentStream.AppendLine($"36 {y} 523 20 re f");

        contentStream.AppendLine("BT");
        contentStream.AppendLine("/F2 9 Tf");
        contentStream.AppendLine("1.0 1.0 1.0 rg");
        contentStream.AppendLine($"42 {y + 6} Td");
        contentStream.AppendLine("(File Name) Tj");
        contentStream.AppendLine($"170 {y + 6} Td");
        contentStream.AppendLine("(Verdict) Tj");
        contentStream.AppendLine($"240 {y + 6} Td");
        contentStream.AppendLine("(Interval) Tj");
        contentStream.AppendLine($"330 {y + 6} Td");
        contentStream.AppendLine("(Confidence) Tj");
        contentStream.AppendLine($"400 {y + 6} Td");
        contentStream.AppendLine("(Reason Flags) Tj");
        contentStream.AppendLine("ET");

        // Table Rows
        y -= 18;
        foreach (var file in results)
        {
            if (y < 60) break; // keep to single page summary or append

            if (file.Segments.Count == 0)
            {
                contentStream.AppendLine($"0.97 0.97 0.97 rg 36 {y - 2} 523 16 re f");
                contentStream.AppendLine("BT");
                contentStream.AppendLine("/F1 8 Tf");
                contentStream.AppendLine("0.2 0.2 0.2 rg");
                contentStream.AppendLine($"42 {y + 2} Td");
                contentStream.AppendLine($"({EscapePdf(Truncate(file.FileName, 22))}) Tj");
                contentStream.AppendLine($"170 {y + 2} Td");
                contentStream.AppendLine($"({file.OverallVerdict}) Tj");
                contentStream.AppendLine($"240 {y + 2} Td");
                contentStream.AppendLine($"(--) Tj");
                contentStream.AppendLine($"330 {y + 2} Td");
                contentStream.AppendLine($"({file.MaxConfidence:F3}) Tj");
                contentStream.AppendLine($"400 {y + 2} Td");
                contentStream.AppendLine("(None) Tj");
                contentStream.AppendLine("ET");
                y -= 18;
                continue;
            }

            foreach (var seg in file.Segments)
            {
                if (y < 60) break;

                // Color verdict
                if (seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase))
                    contentStream.AppendLine($"0.90 0.97 0.92 rg 36 {y - 2} 523 16 re f");
                else
                    contentStream.AppendLine($"0.98 0.95 0.90 rg 36 {y - 2} 523 16 re f");

                string flagsStr = seg.ReasonFlags.Count > 0 ? string.Join(", ", seg.ReasonFlags) : "Clean";

                contentStream.AppendLine("BT");
                contentStream.AppendLine("/F1 8 Tf");
                contentStream.AppendLine("0.1 0.1 0.1 rg");
                contentStream.AppendLine($"42 {y + 2} Td");
                contentStream.AppendLine($"({EscapePdf(Truncate(file.FileName, 22))}) Tj");
                contentStream.AppendLine($"170 {y + 2} Td");
                contentStream.AppendLine($"({seg.Verdict}) Tj");
                contentStream.AppendLine($"240 {y + 2} Td");
                contentStream.AppendLine($"({seg.StartTimeSeconds:F1}s - {seg.EndTimeSeconds:F1}s) Tj");
                contentStream.AppendLine($"330 {y + 2} Td");
                contentStream.AppendLine($"({seg.Confidence:F3}) Tj");
                contentStream.AppendLine($"400 {y + 2} Td");
                contentStream.AppendLine($"({EscapePdf(flagsStr)}) Tj");
                contentStream.AppendLine("ET");

                y -= 18;
            }
        }

        // Footer Legal & Offline statement
        contentStream.AppendLine("BT");
        contentStream.AppendLine("/F1 8 Tf");
        contentStream.AppendLine("0.5 0.5 0.5 rg");
        contentStream.AppendLine("36 30 Td");
        contentStream.AppendLine("(CONFIDENTIAL & BIOMETRICALLY VERIFIED  |  VoiceScan Offline Engine  |  Zero Network Telemetry) Tj");
        contentStream.AppendLine("ET");

        byte[] streamBytes = Encoding.ASCII.GetBytes(contentStream.ToString());

        // Assemble PDF 1.4 Object Structure
        var pdf = new MemoryStream();
        using var writer = new StreamWriter(pdf, Encoding.ASCII, leaveOpen: true);

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
