namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
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
    private const int StderrTailChars = 2000;

    /// <summary>
    /// Builds an FFmpeg/ffprobe launch with a discrete argument list (no shell-style quoting, so file names cannot inject options)
    /// and a protocol whitelist so media containers can never open network or other non-local inputs.
    /// </summary>
    private static ProcessStartInfo CreateStartInfo(string tool, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-protocol_whitelist");
        info.ArgumentList.Add("file,pipe");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

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

        var startInfo = CreateStartInfo("ffprobe",
            "-v", "error", "-select_streams", "a",
            "-show_entries", "stream=index,codec_name,channels,sample_rate:stream_tags=title,language",
            "-of", "json", "-i", mediaFilePath);

        var fallback = new[] { new AudioTrackInfo(0, "default", 1, DefaultSampleRate, null, null) };
        try
        {
            var (exitCode, json, _) = await RunToCompletionAsync(startInfo, cancellationToken);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return fallback;
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

            return list.Count > 0 ? list : fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If ffprobe is unavailable or errors, fall back to default track 0
            Logging.VoiceScanLogger.Warn("AudioDecoder", $"ffprobe could not list tracks of {mediaFilePath}: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// Probe media container duration in seconds using ffprobe.
    /// Returns 0.0 if duration cannot be determined.
    /// </summary>
    public static async Task<double> GetMediaDurationSecondsAsync(string mediaFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
        {
            return 0.0;
        }

        var startInfo = CreateStartInfo("ffprobe",
            "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", "-i", mediaFilePath);

        try
        {
            var (_, output, _) = await RunToCompletionAsync(startInfo, cancellationToken);
            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
                ? duration
                : 0.0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0.0;
        }
    }

    /// <summary>
    /// Stream audio chunks from media file via FFmpeg process.
    /// Memory consumption is strictly bounded by channel capacity (default 8 chunks ~ 512KB RAM).
    /// A decode that FFmpeg reports as failed surfaces as an <see cref="InvalidDataException"/> after the
    /// chunks it did produce, so a truncated decode is never mistaken for the whole file.
    /// </summary>
    /// <param name="maxDurationSeconds">Decode at most this much audio from the start of the file.</param>
    public static async IAsyncEnumerable<DecodedAudioChunk> StreamDecodeAsync(
        string mediaFilePath,
        int audioTrackIndex = 0,
        int sampleRate = DefaultSampleRate,
        int chunkSize = DefaultChunkSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        double? maxDurationSeconds = null)
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

        var inv = CultureInfo.InvariantCulture;
        var args = new List<string> { "-v", "error", "-i", mediaFilePath, "-map", MapArgument(audioTrackIndex) };
        if (maxDurationSeconds is > 0)
        {
            args.Add("-t");
            args.Add(maxDurationSeconds.Value.ToString("F3", inv));
        }
        args.AddRange(["-f", "f32le", "-acodec", "pcm_f32le", "-ac", "1", "-ar", sampleRate.ToString(inv), "-"]);
        var startInfo = CreateStartInfo("ffmpeg", [.. args]);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to launch FFmpeg for {mediaFilePath}");
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Producer task: reads raw stdout and pushes chunks into bounded channel
        var producer = Task.Run(async () =>
        {
            try
            {
                // Drained concurrently so a chatty FFmpeg can never block on a full stderr pipe.
                var stderrTask = ReadTailAsync(process.StandardError, linkedCts.Token);

                var stream = process.StandardOutput.BaseStream;
                byte[] byteBuffer = new byte[chunkSize * sizeof(float)];
                long totalSamplesRead = 0;

                int bytesRead;
                while ((bytesRead = await ReadExactOrEofAsync(stream, byteBuffer, linkedCts.Token)) > 0)
                {
                    int floatCount = bytesRead / sizeof(float);
                    if (floatCount == 0) continue;

                    float[] floatChunk = new float[floatCount];
                    Buffer.BlockCopy(byteBuffer, 0, floatChunk, 0, floatCount * sizeof(float));

                    double startTime = (double)totalSamplesRead / sampleRate;
                    totalSamplesRead += floatCount;

                    await channel.Writer.WriteAsync(
                        new DecodedAudioChunk(floatChunk, totalSamplesRead - floatCount, startTime, false),
                        linkedCts.Token);
                }

                await process.WaitForExitAsync(linkedCts.Token);
                string stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        $"FFmpeg could not decode '{Path.GetFileName(mediaFilePath)}' (exit code {process.ExitCode}): {LastLine(stderr)}");
                }
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Logging.VoiceScanLogger.Warn("AudioDecoder", $"FFmpeg reported problems decoding {mediaFilePath}: {LastLine(stderr)}");
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                if (!linkedCts.IsCancellationRequested)
                {
                    Logging.VoiceScanLogger.Error("AudioDecoder", $"Error streaming audio from {mediaFilePath}", ex);
                }
                channel.Writer.Complete(ex);
            }
            finally
            {
                // Kill before Dispose: a disposed Process can no longer be queried or killed, and an FFmpeg
                // left blocked on a full stdout pipe would otherwise linger until the pipe is finalized.
                KillQuietly(process);
                process.Dispose();
            }
        });

        // Consumer: yields decoded chunks as they arrive
        try
        {
            while (await channel.Reader.WaitToReadAsync(linkedCts.Token))
            {
                while (channel.Reader.TryRead(out var chunk))
                {
                    yield return chunk;
                }
            }
        }
        finally
        {
            linkedCts.Cancel();
            await producer;
        }
    }

    /// <summary>
    /// Extracts a segment of audio directly to a 16kHz mono WAV file using FFmpeg fast seek.
    /// Does not load the entire audio file into RAM.
    /// </summary>
    public static async Task ExtractAudioSegmentAsync(
        string mediaFilePath,
        string outputWavPath,
        double startTimeSeconds,
        double durationSeconds,
        int audioTrackIndex = 0,
        int sampleRate = DefaultSampleRate,
        CancellationToken cancellationToken = default)
    {
        string? parent = Path.GetDirectoryName(outputWavPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var inv = CultureInfo.InvariantCulture;
        var startInfo = CreateStartInfo("ffmpeg",
            "-y", "-v", "error",
            "-ss", Math.Max(0.0, startTimeSeconds).ToString("F3", inv),
            "-t", Math.Max(0.1, durationSeconds).ToString("F3", inv),
            "-i", mediaFilePath, "-map", MapArgument(audioTrackIndex),
            "-ar", sampleRate.ToString(inv), "-ac", "1", "-c:a", "pcm_s16le", outputWavPath);

        var (exitCode, _, err) = await RunToCompletionAsync(startInfo, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg extraction failed ({exitCode}): {LastLine(err)}");
        }
    }

    /// <summary>
    /// Reads a whole (short) file into a float array, e.g. enrollment clips. Use <paramref name="maxDurationSeconds"/>
    /// to bound memory when the input may be long.
    /// </summary>
    public static async Task<float[]> DecodeEntireFileAsync(
        string mediaFilePath,
        int audioTrackIndex = 0,
        int sampleRate = DefaultSampleRate,
        CancellationToken cancellationToken = default,
        double? maxDurationSeconds = null)
    {
        var allChunks = new List<float[]>();
        long totalSamples = 0;

        await foreach (var chunk in StreamDecodeAsync(mediaFilePath, audioTrackIndex, sampleRate, DefaultChunkSize, cancellationToken, maxDurationSeconds))
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

    private static string MapArgument(int audioTrackIndex) =>
        $"0:a:{Math.Max(0, audioTrackIndex).ToString(CultureInfo.InvariantCulture)}?";

    /// <summary>Runs a short-lived tool to completion, killing it if the caller cancels.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToCompletionAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to launch {startInfo.FileName}");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = ReadTailAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            KillQuietly(process);
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var tail = new StringBuilder();
        char[] buf = new char[1024];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory(), cancellationToken)) > 0)
            {
                tail.Append(buf, 0, n);
                if (tail.Length > StderrTailChars) tail.Remove(0, tail.Length - StderrTailChars);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The process was killed or the caller gave up; whatever was read is still useful.
        }
        return tail.ToString();
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "no error details";
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited, or never started.
        }
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
