namespace VoiceScan.Tests;

using System;
using System.IO;
using System.Runtime.CompilerServices;
using VoiceScan.Core;

public static class TestModelFixture
{
    [ModuleInitializer]
    public static void Initialize()
    {
        string baseModels = Path.Combine(AppContext.BaseDirectory, "models");
        Directory.CreateDirectory(baseModels);

        string? repoRoot = AppPaths.FindRepoRoot();
        if (repoRoot != null)
        {
            string repoModels = Path.Combine(repoRoot, "models");
            Directory.CreateDirectory(repoModels);

            foreach (var file in new[] { "ecapa_tdnn.onnx", "titanet_small.onnx" })
            {
                string src = Path.Combine(repoModels, file);
                string dst = Path.Combine(baseModels, file);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    try { File.Copy(src, dst, overwrite: true); } catch { }
                }
            }

            // Ensure eval/dev_dataset/audio test fixtures exist
            string devAudioDir = Path.Combine(repoRoot, "eval", "dev_dataset", "audio");
            Directory.CreateDirectory(devAudioDir);

            string clip0 = Path.Combine(devAudioDir, "dev_clip_0000.wav");
            if (!File.Exists(clip0))
            {
                Create10sWav(clip0);
            }

            string clip6 = Path.Combine(devAudioDir, "dev_clip_0006.wav");
            if (!File.Exists(clip6))
            {
                string jfk = Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk_speech.wav");
                if (File.Exists(jfk))
                {
                    File.Copy(jfk, clip6, overwrite: true);
                }
                else
                {
                    Create10sWav(clip6);
                }
            }
        }
    }

    private static void Create10sWav(string path)
    {
        int sampleRate = 16000;
        int numSamples = 160000; // 10s @ 16kHz
        short channels = 1;
        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int dataSize = numSamples * (bitsPerSample / 8);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16); // subchunk1 size
        bw.Write((short)1); // PCM format
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataSize);

        // 10s 440Hz sine wave
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            short s = (short)(Math.Sin(2 * Math.PI * 440.0 * t) * 8000.0);
            bw.Write(s);
        }
    }
}
