namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;

/// <summary>
/// Re-analyses a clip, or a section of it, without a voice profile: the sidecar detects speech, diarizes, transcribes
/// and scores it, and each line's voice embedding is computed with the active embedding model.
/// </summary>
public sealed class ClipAnalyzer
{
    private const int SampleRate = AudioDecoder.DefaultSampleRate;
    private const int MinEmbeddingSamples = SampleRate / 10;

    private readonly IInferenceClient _sidecar;

    public ClipAnalyzer(ISpeakerEmbeddingModel embeddingModel, IInferenceClient sidecar)
    {
        EmbeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
        _sidecar = sidecar ?? throw new ArgumentNullException(nameof(sidecar));
    }

    public ISpeakerEmbeddingModel EmbeddingModel { get; }

    /// <summary>
    /// Returns the analysed lines with embeddings. For a section, only lines whose midpoint lies in it are returned.
    /// Throws when the file is missing or the sidecar does not answer, so a failed re-analysis is never stored as clean.
    /// </summary>
    public async Task<FileScanResult> AnalyseAsync(
        string filePath, SidecarAnalysisRequest request, int audioTrackIndex = 0, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Recording not found: {filePath}", filePath);
        }

        string fullPath = Path.GetFullPath(filePath);
        string fileHash = FastFileHasher.ComputeFastHash(fullPath);
        double duration = await AudioDecoder.GetMediaDurationSecondsAsync(fullPath, ct);

        var response = await _sidecar.AnalyseAsync(fullPath, request with { AudioTrackIndex = audioTrackIndex }, ct);
        double from = request.StartSeconds ?? double.NegativeInfinity;
        double to = request.EndSeconds ?? double.PositiveInfinity;
        var segments = (response.HasSpeech ? response.Segments : [])
            .Where(s => s.EndTimeSeconds > s.StartTimeSeconds)
            .Where(s => (s.StartTimeSeconds + s.EndTimeSeconds) / 2.0 is var mid && mid >= from && mid <= to)
            .OrderBy(s => s.StartTimeSeconds)
            .ToList();

        if (segments.Count > 0)
        {
            await EmbedAsync(fullPath, segments, audioTrackIndex, ct);
        }

        return new FileScanResult
        {
            FilePath = fullPath,
            ClipId = Path.GetFileName(fullPath),
            FileHash = fileHash,
            DurationSeconds = Math.Round(duration > 0 ? duration : segments.Select(s => s.EndTimeSeconds).DefaultIfEmpty(0).Max(), 3),
            AudioTrackIndex = audioTrackIndex,
            Segments = segments,
            AnalyzerUsed = true,
            AnalyzerModels = response.Models
        };
    }

    /// <summary>Decodes only the span the lines cover (spooled to disk, never held in RAM) and embeds each line.</summary>
    private async Task EmbedAsync(string filePath, List<DetectedSegment> segments, int audioTrackIndex, CancellationToken ct)
    {
        double spanStart = Math.Max(0.0, segments.Min(s => s.StartTimeSeconds));
        double spanEnd = segments.Max(s => s.EndTimeSeconds);

        using var spool = new SpooledAudio();
        await foreach (var chunk in AudioDecoder.StreamDecodeAsync(
            filePath, audioTrackIndex, SampleRate, AudioDecoder.DefaultChunkSize, ct,
            maxDurationSeconds: spanEnd - spanStart, startSeconds: spanStart))
        {
            spool.Append(chunk.Samples);
        }

        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();
            long first = Math.Clamp((long)((seg.StartTimeSeconds - spanStart) * SampleRate), 0, spool.SampleCount);
            long last = Math.Clamp((long)((seg.EndTimeSeconds - spanStart) * SampleRate), 0, spool.SampleCount);
            if (last - first < MinEmbeddingSamples) continue;
            seg.Embedding = EmbeddingModel.ExtractEmbedding(spool.Read(first, (int)(last - first)));
        }
    }
}
