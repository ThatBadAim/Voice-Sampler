namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VoiceScan.Core;
using VoiceScan.Core.Storage;
using Xunit;

public class StorageCacheTests
{
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "VoiceScan.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    [Fact]
    public void FastFileHasher_ProducesDeterministicHybridAndFullHashes()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            // Write 512 KB of test data
            byte[] data = new byte[512 * 1024];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(tempFile, data);

            string hash1 = FastFileHasher.ComputeFastHash(tempFile);
            string hash2 = FastFileHasher.ComputeFastHash(tempFile);
            string fullHash = FastFileHasher.ComputeFullSha256(tempFile);

            Assert.Equal(hash1, hash2);
            Assert.StartsWith("sha256-", hash1); // <= 8MB files use full SHA-256
            Assert.Equal(fullHash, hash1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FastFileHasher_LargeFile_UsesHybridStrategy()
    {
        string tempLargeFile = Path.GetTempFileName();
        try
        {
            // Create a sparse 9 MB file to trigger hybrid strategy (> 8 MB)
            using (var fs = new FileStream(tempLargeFile, FileMode.OpenOrCreate, FileAccess.Write))
            {
                fs.SetLength(9 * 1024 * 1024);
                fs.Seek(0, SeekOrigin.Begin);
                fs.WriteByte(0xAA);
                fs.Seek(fs.Length - 1, SeekOrigin.Begin);
                fs.WriteByte(0xBB);
            }

            string hybridHash = FastFileHasher.ComputeFastHash(tempLargeFile);
            Assert.StartsWith("hybrid-", hybridHash);

            string hybridHash2 = FastFileHasher.ComputeFastHash(tempLargeFile);
            Assert.Equal(hybridHash, hybridHash2);
        }
        finally
        {
            if (File.Exists(tempLargeFile)) File.Delete(tempLargeFile);
        }
    }

    [Fact]
    public async Task Database_Profiles_PersistAndRoundtripAcrossConnections()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_voicescan_{Guid.NewGuid():N}.db");
        try
        {
            var profileA = new VoiceProfile
            {
                ProfileName = "speaker_alpha_test",
                ModelId = "speechbrain-ecapa-tdnn",
                Centroid = new float[] { 0.1f, 0.2f, -0.3f, 0.4f },
                EnrollmentEmbeddings = new List<float[]>
                {
                    new float[] { 0.1f, 0.2f, -0.3f, 0.4f },
                    new float[] { 0.12f, 0.19f, -0.28f, 0.41f }
                },
                ClipCount = 2,
                TotalSpeechDurationSeconds = 5.4
            };

            // 1. Save profile in connection 1
            using (var db1 = new VoiceScanDatabase(tempDb))
            {
                await db1.InitializeAsync();
                await db1.SaveProfileAsync(profileA);
            }

            // 2. Open connection 2, read and verify persistence
            using (var db2 = new VoiceScanDatabase(tempDb))
            {
                await db2.InitializeAsync();
                var loaded = await db2.GetProfileAsync("speaker_alpha_test");

                Assert.NotNull(loaded);
                Assert.Equal("speaker_alpha_test", loaded.ProfileName);
                Assert.Equal("speechbrain-ecapa-tdnn", loaded.ModelId);
                Assert.Equal(profileA.Centroid.Length, loaded.Centroid.Length);
                for (int i = 0; i < profileA.Centroid.Length; i++)
                {
                    Assert.Equal(profileA.Centroid[i], loaded.Centroid[i], precision: 4);
                }
                Assert.Equal(2, loaded.EnrollmentEmbeddings.Count);

                var list = await db2.ListProfilesAsync();
                Assert.Single(list);
                Assert.Equal("speaker_alpha_test", list[0].Name);

                bool deleted = await db2.DeleteProfileAsync("speaker_alpha_test");
                Assert.True(deleted);

                var afterDelete = await db2.GetProfileAsync("speaker_alpha_test");
                Assert.Null(afterDelete);
            }
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task Database_EmbeddingCache_HitMissAndInvalidation()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_cache_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new VoiceScanDatabase(tempDb);
            await db.InitializeAsync();

            string fileHash = "hash-12345";
            string model = "speechbrain-ecapa-tdnn";
            string vad = "silero-0.5";
            string win = "w2.0_h1.0";
            string cacheKey = VoiceScanDatabase.ComputeCacheKey(fileHash, model, vad, win);

            // 1. Initial lookup -> Miss (null)
            var miss = await db.GetCachedWindowsAsync(cacheKey);
            Assert.Null(miss);

            // 2. Save windows to cache
            var windows = new List<CachedWindow>
            {
                new CachedWindow(0.0, 2.0, new float[] { 0.5f, 0.5f }),
                new CachedWindow(1.0, 3.0, new float[] { 0.6f, 0.4f })
            };
            await db.SaveCachedWindowsAsync(cacheKey, fileHash, model, vad, win, windows);

            // 3. Subsequent lookup -> Hit
            var hit = await db.GetCachedWindowsAsync(cacheKey);
            Assert.NotNull(hit);
            Assert.Equal(2, hit.Count);
            Assert.Equal(0.0, hit[0].StartTimeSeconds);
            Assert.Equal(2.0, hit[0].EndTimeSeconds);
            Assert.Equal(0.5f, hit[0].Embedding[0], precision: 4);

            var stats = await db.GetCacheStatsAsync();
            Assert.Equal(1, stats.TotalCachedFiles);
            Assert.Equal(2, stats.TotalCachedWindows);

            // 4. Invalidation by model
            int cleared = await db.InvalidateCacheAsync(modelVersion: "speechbrain-ecapa-tdnn");
            Assert.Equal(1, cleared);

            var afterClear = await db.GetCachedWindowsAsync(cacheKey);
            Assert.Null(afterClear);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task Benchmark_Scanning50Files_ProfileB_RunsInFractionOfTime()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"voicescan_50files_{Guid.NewGuid():N}");
        string tempDb = Path.Combine(tempDir, "bench_cache.db");
        Directory.CreateDirectory(tempDir);

        try
        {
            int fileCount = 50;
            int sr = 16000;

            // Silero correctly ignores pure tones, so derive the 50 distinct files from a real speech clip (JFK, public domain).
            float[] source = (await AudioDecoder.DecodeEntireFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk_speech.wav")))
                .Take(6 * sr).ToArray();

            // 1. Synthesize 50 distinct test WAV files (distinct gain => distinct content hash)
            var filePaths = new List<string>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                string path = Path.Combine(tempDir, $"test_audio_{i:03d}.wav");
                float gain = 0.5f + (i * 0.01f);

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(fs))
                {
                    writer.Write("RIFF"u8.ToArray());
                    writer.Write(36 + (source.Length * 2));
                    writer.Write("WAVE"u8.ToArray());
                    writer.Write("fmt "u8.ToArray());
                    writer.Write(16); // subchunk1 size
                    writer.Write((short)1); // PCM
                    writer.Write((short)1); // Mono
                    writer.Write(sr); // Sample rate
                    writer.Write(sr * 2); // Byte rate
                    writer.Write((short)2); // Block align
                    writer.Write((short)16); // Bits per sample
                    writer.Write("data"u8.ToArray());
                    writer.Write(source.Length * 2);

                    for (int s = 0; s < source.Length; s++)
                    {
                        writer.Write((short)(Math.Clamp(source[s] * gain, -1f, 1f) * 32767f));
                    }
                }

                filePaths.Add(path);
            }

            using var embeddingModel = new OnnxEmbeddingModel("ecapa");
            var vad = new WebRtcVad();
            using var db = new VoiceScanDatabase(tempDb);
            await db.InitializeAsync();

            // Create Profile A and Profile B
            var profileA = new VoiceProfile
            {
                ProfileName = "profile_A",
                ModelId = embeddingModel.ModelId,
                ModelVersion = embeddingModel.ModelVersion,
                Centroid = new float[192]
            };
            profileA.Centroid[0] = 1.0f;

            var profileB = new VoiceProfile
            {
                ProfileName = "profile_B",
                ModelId = embeddingModel.ModelId,
                ModelVersion = embeddingModel.ModelVersion,
                Centroid = new float[192]
            };
            profileB.Centroid[1] = 1.0f;

            var scanner = new PipelineScanner(embeddingModel, vad, database: db, batchSize: 16);

            // 2. Scan 50 files for Profile A (Cache Miss: Full decode + VAD + Embedding extraction)
            var swA = Stopwatch.StartNew();
            var docA = await scanner.ScanDirectoryAsync(tempDir, profileA, threshold: 0.48);
            swA.Stop();
            double timeA = swA.Elapsed.TotalSeconds;

            Assert.Equal(fileCount, docA.Files.Count);

            // Verify cache is populated
            var stats = await db.GetCacheStatsAsync();
            Assert.Equal(fileCount, stats.TotalCachedFiles);
            Assert.True(stats.TotalCachedWindows >= fileCount);

            // 3. Scan the same 50 files for Profile B (Cache Hit: Skips decode, VAD, and embedding inference)
            var swB = Stopwatch.StartNew();
            var docB = await scanner.ScanDirectoryAsync(tempDir, profileB, threshold: 0.48);
            swB.Stop();
            double timeB = swB.Elapsed.TotalSeconds;

            Assert.Equal(fileCount, docB.Files.Count);

            double speedup = timeA / Math.Max(0.001, timeB);

            Console.WriteLine($"[BENCHMARK] Scanned {fileCount} files for Profile A (Cache Miss): {timeA:F3}s");
            Console.WriteLine($"[BENCHMARK] Re-scanned {fileCount} files for Profile B (Cache Hit):  {timeB:F3}s");
            Console.WriteLine($"[BENCHMARK] Cache Hit Speedup: {speedup:F1}x faster (Runs in {(timeB / timeA):P1} of original time)!");

            // Profile B must run in a small fraction of the time (at least 3x faster, typically 10x-50x)
            Assert.True(timeB < timeA * 0.35, $"Expected Profile B ({timeB:F3}s) to run in <35% of Profile A time ({timeA:F3}s). Speedup was {speedup:F1}x");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
