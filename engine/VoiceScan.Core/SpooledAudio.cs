namespace VoiceScan.Core;

using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Decoded 16 kHz float PCM spooled to a temporary file so multi-hour recordings never sit in RAM.
/// Supports random-access reads and builds a min/max waveform envelope while data is appended.
/// The file is deleted when the instance is disposed.
/// </summary>
internal sealed class SpooledAudio : IDisposable
{
    private readonly FileStream _file = new(
        Path.Combine(Path.GetTempPath(), $"voicescan_{Guid.NewGuid():N}.pcm"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);

    private readonly EnvelopeAccumulator _envelope = new();

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
}
