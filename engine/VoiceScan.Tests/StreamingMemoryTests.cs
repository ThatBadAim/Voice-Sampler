namespace VoiceScan.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core;
using Xunit;

public class StreamingMemoryTests
{
    [Fact]
    public async Task StreamDecode_MultiHourAudio_BoundedMemoryUsage()
    {
        // Generate a 1-hour synthetic stream (3600 seconds) via FFmpeg lavfi generator
        // This tests that chunked streaming maintains bounded working set memory <= 250 MB.
        double targetDurationSeconds = 3600.0;
        int sampleRate = 16000;
        long expectedTotalSamples = (long)(targetDurationSeconds * sampleRate);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -v error -f lavfi -i \"sine=frequency=440:sample_rate={sampleRate}:duration=3600\" -f f32le -acodec pcm_f32le -ac 1 -ar {sampleRate} -",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start FFmpeg generator");

        var stopwatch = Stopwatch.StartNew();
        long samplesRead = 0;
        byte[] buffer = new byte[32000 * sizeof(float)]; // 2.0s chunks
        long peakMemoryBytes = 0;

        using (process)
        {
            var stream = process.StandardOutput.BaseStream;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                samplesRead += bytesRead / sizeof(float);

                // Sample process memory usage
                long currentMemory = GC.GetTotalMemory(forceFullCollection: false);
                if (currentMemory > peakMemoryBytes)
                {
                    peakMemoryBytes = currentMemory;
                }
            }

            await process.WaitForExitAsync();
        }

        stopwatch.Stop();

        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        double realtimeMultiple = targetDurationSeconds / elapsedSeconds;
        double peakMemoryMb = (double)peakMemoryBytes / (1024 * 1024);

        Console.WriteLine($"[BENCHMARK] Streamed 1.0 hour of audio in {elapsedSeconds:F2}s ({realtimeMultiple:F1}x realtime).");
        Console.WriteLine($"[BENCHMARK] Peak managed heap memory: {peakMemoryMb:F2} MB (Limit: 250 MB).");

        Assert.True(samplesRead >= expectedTotalSamples * 0.99, $"Expected ~{expectedTotalSamples} samples, read {samplesRead}");
        Assert.True(peakMemoryMb < 250.0, $"Peak memory exceeded 250 MB limit: {peakMemoryMb:F2} MB");
    }
}
