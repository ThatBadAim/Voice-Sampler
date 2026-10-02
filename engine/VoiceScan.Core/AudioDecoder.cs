namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public record AudioTrackInfo(
    int Index,
    string Codec,
    int Channels,
    int SampleRate,
    string? Title,
    string? Language);

public record DecodedAudioChunk(
    float[] Samples,
    long SampleOffset,
    double StartTimeSeconds,
    bool IsLast);

/// <summary>
/// Streams audio from media files directly to 16,000 Hz, 32-bit floating-point mono PCM in bounded chunks.
/// Multi-hour files stream chunk-by-chunk through a bounded channel without buffering the entire file into memory.
/// </summary>
public static class AudioDecoder
{
    public const int DefaultSampleRate = 16000;
    public const int DefaultChunkSize = 32000; // 2.0 seconds of audio @ 16kHz

    /// <summary>
    /// Probe container media files (MKV, MP4, etc.) for available audio tracks.
    /// </summary>
    public static async Task<IReadOnlyList<AudioTrackInfo>> ProbeAudioTracksAsync(string mediaFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
        {
            throw new FileNotFoundException($"Media file not found: {mediaFilePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v error -select_streams a -show_entries stream=index,codec_name,channels,sample_rate:stream_tags=title,language -of json \"{mediaFilePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return Array.Empty<AudioTrackInfo>();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var json = await outputTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return new[] { new AudioTrackInfo(0, "default", 1, DefaultSampleRate, null, null) };
            }

            using var doc = JsonDocument.Parse(json);
            var list = new List<AudioTrackInfo>();

            if (doc.RootElement.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
            {
                int trackIdx = 0;
                foreach (var stream in streamsElement.EnumerateArray())
                {
                    string codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "unknown" : "unknown";
                    int channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 1;
                    int rate = stream.TryGetProperty("sample_rate", out var r) && int.TryParse(r.GetString(), out var sr) ? sr : DefaultSampleRate;

                    string? title = null;
                    string? lang = null;
                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("title", out var t)) title = t.GetString();
                        if (tags.TryGetProperty("language", out var l)) lang = l.GetString();
                    }

                    list.Add(new AudioTrackInfo(trackIdx++, codec, channels, rate, title, lang));
                }
            }

            return list.Count > 0
                ? list
                : new[] { new AudioTrackInfo(0, "default", 1, DefaultSampleRate, null, null) };
        }
        catch (Exception)
        {
            // If ffprobe is unavailable or errors, fallback to default track 0
            return new[] { new AudioTrackInfo(0, "default", 1, DefaultSampleRate, null, null) };
        }
    }

    /// <summary>
    /// Stream audio chunks from media file via FFmpeg process.
    /// Memory consumption is strictly bounded by channel capacity (default 8 chunks ~ 512KB RAM).
    /// </summary>
    public static async IAsyncEnumerable<DecodedAudioChunk> StreamDecodeAsync(
        string mediaFilePath,
        int audioTrackIndex = 0,
        int sampleRate = DefaultSampleRate,
        int chunkSize = DefaultChunkSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
        {
            throw new FileNotFoundException($"Audio file not found: {mediaFilePath}");
        }

        // Bounded channel to enforce streaming backpressure
        var channel = Channel.CreateBounded<DecodedAudioChunk>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

        // Arguments: decode selected audio track directly to raw f32le 16kHz mono stdout stream
        string mapArg = audioTrackIndex >= 0 ? $"-map 0:a:{audioTrackIndex}?" : "-map 0:a:0?";
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -v error -i \"{mediaFilePath}\" {mapArg} -f f32le -acodec pcm_f32le -ac 1 -ar {sampleRate} -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to launch FFmpeg for {mediaFilePath}");

        // Producer task: reads raw stdout and pushes chunks into bounded channel
        _ = Task.Run(async () =>
        {
            try
            {
                using (process)
                {
                    var stream = process.StandardOutput.BaseStream;
                    byte[] byteBuffer = new byte[chunkSize * sizeof(float)];
                    long totalSamplesRead = 0;

                    int bytesRead;
                    while ((bytesRead = await ReadExactOrEofAsync(stream, byteBuffer, cancellationToken)) > 0)
                    {
                        int floatCount = bytesRead / sizeof(float);
                        float[] floatChunk = new float[floatCount];
                        Buffer.BlockCopy(byteBuffer, 0, floatChunk, 0, bytesRead);

                        double startTime = (double)totalSamplesRead / sampleRate;
                        totalSamplesRead += floatCount;

                        await channel.Writer.WriteAsync(
                            new DecodedAudioChunk(floatChunk, totalSamplesRead - floatCount, startTime, false),
                            cancellationToken);
                    }

                    await process.WaitForExitAsync(cancellationToken);
                    channel.Writer.Complete();
                }
            }
            catch (Exception ex)
            {
                Logging.VoiceScanLogger.Error("AudioDecoder", $"Error streaming audio from {mediaFilePath}", ex);
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Consumer: yields decoded chunks as they arrive
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var chunk))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Reads entire file into a float array (useful for short enrollment clips or small test files).
    /// Uses the streaming decoder internally.
    /// </summary>
    public static async Task<float[]> DecodeEntireFileAsync(
        string mediaFilePath,
        int audioTrackIndex = 0,
        int sampleRate = DefaultSampleRate,
        CancellationToken cancellationToken = default)
    {
        var allChunks = new List<float[]>();
        int totalSamples = 0;

        await foreach (var chunk in StreamDecodeAsync(mediaFilePath, audioTrackIndex, sampleRate, DefaultChunkSize, cancellationToken))
        {
            allChunks.Add(chunk.Samples);
            totalSamples += chunk.Samples.Length;
        }

        if (totalSamples == 0)
        {
            Logging.VoiceScanLogger.Warn("AudioDecoder", $"Decoded zero audio samples from: {mediaFilePath}");
            return Array.Empty<float>();
        }

        float[] result = new float[totalSamples];
        int offset = 0;
        foreach (var c in allChunks)
        {
            Array.Copy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }

        return result;
    }

    private static async Task<int> ReadExactOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break; // End of stream
            }
            totalRead += read;
        }
        return totalRead;
    }
}
