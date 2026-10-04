namespace VoiceScan.Core;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Decoded 16 kHz float PCM spooled to a file under <see cref="AppPaths.SpoolDirectory"/> so multi-hour recordings
/// never sit in RAM (the system temp directory is often a RAM-backed tmpfs on Linux).
/// Supports random-access reads and builds a min/max waveform envelope while data is appended.
/// The file is deleted when the instance is disposed.
/// </summary>
internal sealed class SpooledAudio : IDisposable
{
    private const string FilePrefix = "voicescan_";
    private const string FileExtension = ".pcm";

    private static readonly ConcurrentDictionary<string, bool> SweptDirectories = new(StringComparer.Ordinal);

    private readonly FileStream _file;
    private readonly EnvelopeAccumulator _envelope = new();

    public SpooledAudio(string? directory = null)
    {
        directory ??= AppPaths.SpoolDirectory;
        Directory.CreateDirectory(directory);
        if (SweptDirectories.TryAdd(Path.GetFullPath(directory), true))
        {
            DeleteStaleFiles(directory);
        }

        _file = new FileStream(
            Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid():N}{FileExtension}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
    }

    public long SampleCount { get; private set; }

    public void Append(float[] samples)
    {
        _file.Seek(0, SeekOrigin.End);
        _file.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        SampleCount += samples.Length;
        _envelope.Add(samples);
    }

    public float[] Read(long start, int count)
    {
        var samples = new float[count];
        _file.Seek(start * sizeof(float), SeekOrigin.Begin);
        _file.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
        return samples;
    }

    public (float[] Min, float[] Max) Envelope(int buckets) => _envelope.Build(buckets);

    public void Dispose() => _file.Dispose();

    /// <summary>Removes spool files left behind by a crashed process; files still open elsewhere are locked and skipped.</summary>
    private static void DeleteStaleFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, $"{FilePrefix}*{FileExtension}"))
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by another VoiceScan process.
            }
        }
    }
}
