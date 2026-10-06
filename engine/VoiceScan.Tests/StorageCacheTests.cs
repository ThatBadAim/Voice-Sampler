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

            ISpeakerEmbeddingModel embeddingModel;
            try
            {
                embeddingModel = new OnnxEmbeddingModel("ecapa");
            }
            catch (FileNotFoundException)
            {
                embeddingModel = new BenchMockEmbeddingModel();
            }
            using var _ = embeddingModel;
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

            // Profile B must run in a fraction of the time (cache hit vs miss)
            Assert.True(timeB < timeA * 0.55, $"Expected Profile B ({timeB:F3}s) to run in <55% of Profile A time ({timeA:F3}s). Speedup was {speedup:F1}x");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ScanResultModels_SchemaVersion110_AndBackwardsCompatibility()
    {
        // 1. Default ScanOutputDocument has SchemaVersion 1.1.0
        var doc = new ScanOutputDocument();
        Assert.Equal("1.1.0", doc.SchemaVersion);
        Assert.Equal("1.1.0", ScanOutputDocument.CurrentSchemaVersion);

        // 2. Legacy JSON (v1.0.0 without diarization / transcription / moderation fields) deserializes cleanly
        string legacyJson = @"{
            ""schema_version"": ""1.0.0"",
            ""scan_metadata"": {
                ""timestamp"": ""2026-10-01T22:00:00Z"",
                ""profile_name"": ""TargetUser"",
                ""model_id"": ""ecapa_tdnn""
            },
            ""files"": [
                {
                    ""file_path"": ""/test.wav"",
                    ""clip_id"": ""test.wav"",
                    ""file_hash"": ""hash123"",
                    ""verdict"": ""Match"",
                    ""max_confidence"": 0.91,
                    ""segments"": [
                        {
                            ""start_time_seconds"": 1.0,
                            ""end_time_seconds"": 3.5,
                            ""confidence"": 0.91,
                            ""verdict"": ""Match"",
                            ""reason_flags"": [""strong_match""]
                        }
                    ]
                }
            ]
        }";

        var deserializedLegacy = System.Text.Json.JsonSerializer.Deserialize<ScanOutputDocument>(legacyJson);
        Assert.NotNull(deserializedLegacy);
        Assert.Equal("1.0.0", deserializedLegacy.SchemaVersion);
        Assert.Single(deserializedLegacy.Files);
        var segLegacy = deserializedLegacy.Files[0].Segments[0];
        Assert.Null(segLegacy.SpeakerLabel);
        Assert.Null(segLegacy.Transcript);
        Assert.False(segLegacy.IsOffensive);
        Assert.NotNull(segLegacy.ModerationViolations);
        Assert.Empty(segLegacy.ModerationViolations);

        // 3. New segment with transcription, diarization, and moderation
        var segment = new DetectedSegment
        {
            StartTimeSeconds = 2.0,
            EndTimeSeconds = 5.0,
            Confidence = 0.88,
            Verdict = "Match",
            ReasonFlags = new List<string> { "hit" },
            SpeakerLabel = "SPEAKER_00",
            Transcript = "Flagged abusive language detected",
            IsOffensive = true,
            ModerationViolations = new List<string> { "harassment", "toxicity" }
        };

        string json = System.Text.Json.JsonSerializer.Serialize(segment);
        Assert.Contains("\"speaker_label\":\"SPEAKER_00\"", json);
        Assert.Contains("\"transcript\":\"Flagged abusive language detected\"", json);
        Assert.Contains("\"is_offensive\":true", json);
        Assert.Contains("\"moderation_violations\":[\"harassment\",\"toxicity\"]", json);

        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<DetectedSegment>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal("SPEAKER_00", roundTripped.SpeakerLabel);
        Assert.Equal("Flagged abusive language detected", roundTripped.Transcript);
        Assert.True(roundTripped.IsOffensive);
        Assert.Equal(2, roundTripped.ModerationViolations.Count);
        Assert.Contains("harassment", roundTripped.ModerationViolations);
        Assert.Contains("toxicity", roundTripped.ModerationViolations);

        // 4. Constructor initialization
        var ctorSeg = new DetectedSegment(
            1.5, 4.0, 0.75, "Possible",
            speakerLabel: "SPEAKER_01",
            transcript: "Clean speech",
            isOffensive: false,
            moderationViolations: Array.Empty<string>());
        Assert.Equal("SPEAKER_01", ctorSeg.SpeakerLabel);
        Assert.Equal("Clean speech", ctorSeg.Transcript);
        Assert.False(ctorSeg.IsOffensive);
        Assert.Empty(ctorSeg.ModerationViolations);

        // 5. Backwards-compatible constructor overload (6-arg: start, end, conf, verdict, flags, embedding)
        var legacyCtorSeg = new DetectedSegment(1.0, 2.0, 0.9, "Match", new List<string> { "test" }, new float[] { 0.5f });
        Assert.Null(legacyCtorSeg.SpeakerLabel);
        Assert.Null(legacyCtorSeg.Transcript);
        Assert.False(legacyCtorSeg.IsOffensive);
        Assert.Empty(legacyCtorSeg.ModerationViolations);
        Assert.NotNull(legacyCtorSeg.Embedding);

        // 6. Equality checks
        var segA = new DetectedSegment(1.0, 2.0, 0.9, "Match", new List<string> { "a" }, "SPK1", "Hello", false, new[] { "none" });
        var segB = new DetectedSegment(1.0, 2.0, 0.9, "Match", new List<string> { "a" }, "SPK1", "Hello", false, new[] { "none" });
        var segC = new DetectedSegment(1.0, 2.0, 0.9, "Match", new List<string> { "a" }, "SPK2", "Hello", false, new[] { "none" });
        Assert.Equal(segA, segB);
        Assert.True(segA == segB);
        Assert.False(segA != segB);
        Assert.NotEqual(segA, segC);
        Assert.True(segA != segC);

        // 7. StoredScanResult record equality check
        var resA = new StoredScanResult(1, "/f.wav", "h", "p", "m", 0.5, "Match", 0.9, "[]", "now", "SPK", "text", true, new[] { "v1" });
        var resB = new StoredScanResult(1, "/f.wav", "h", "p", "m", 0.5, "Match", 0.9, "[]", "now", "SPK", "text", true, new List<string> { "v1" });
        Assert.Equal(resA, resB);
    }

    [Fact]
    public async Task Database_SchemaVersion110_MigrationAndPersistenceRoundtrip()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_v110_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new VoiceScanDatabase(tempDb);
            await db.InitializeAsync();

            // Verify SchemaVersion constant
            Assert.Equal("1.1.0", VoiceScanDatabase.SchemaVersion);

            // 1. Save scan result with the new v1.1.0 columns
            string segmentsJson = "[{\"start_time_seconds\":1.0,\"end_time_seconds\":3.0,\"confidence\":0.85}]";
            var violations = new List<string> { "threat", "harassment" };

            await db.SaveScanResultAsync(
                filePath: "/media/clip1.mp4",
                fileHash: "hash-clip1",
                profileName: "GamerProfile",
                modelId: "speechbrain-ecapa-tdnn",
                threshold: 0.55,
                verdict: "Match",
                maxConfidence: 0.85,
                segmentsJson: segmentsJson,
                speakerLabel: "SPEAKER_00",
                transcript: "I see the player approaching",
                isOffensive: true,
                moderationViolations: violations);

            // 2. Read single scan result
            var single = await db.GetScanResultAsync("hash-clip1", "GamerProfile", "speechbrain-ecapa-tdnn", 0.55);
            Assert.NotNull(single);
            Assert.Equal("/media/clip1.mp4", single.FilePath);
            Assert.Equal("hash-clip1", single.FileHash);
            Assert.Equal("GamerProfile", single.ProfileName);
            Assert.Equal("SPEAKER_00", single.SpeakerLabel);
            Assert.Equal("I see the player approaching", single.Transcript);
            Assert.True(single.IsOffensive);
            Assert.NotNull(single.ModerationViolations);
            Assert.Equal(2, single.ModerationViolations.Count);
            Assert.Equal("threat", single.ModerationViolations[0]);
            Assert.Equal("harassment", single.ModerationViolations[1]);

            // 3. Query scan results list
            var list = await db.GetScanResultsAsync(fileHash: "hash-clip1");
            Assert.Single(list);
            Assert.Equal("SPEAKER_00", list[0].SpeakerLabel);

            // 4. Save another result without optional fields (backwards compatibility test)
            await db.SaveScanResultAsync(
                filePath: "/media/clip2.mp4",
                fileHash: "hash-clip2",
                profileName: "GamerProfile",
                modelId: "speechbrain-ecapa-tdnn",
                threshold: 0.55,
                verdict: "No match",
                maxConfidence: 0.20,
                segmentsJson: "[]");

            var second = await db.GetScanResultAsync("hash-clip2", "GamerProfile", "speechbrain-ecapa-tdnn", 0.55);
            Assert.NotNull(second);
            Assert.Null(second.SpeakerLabel);
            Assert.Null(second.Transcript);
            Assert.False(second.IsOffensive);
            Assert.NotNull(second.ModerationViolations);
            Assert.Empty(second.ModerationViolations);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task Database_UpgradesExistingDatabaseFromV4ToV5()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_upgrade_v4_v5_{Guid.NewGuid():N}.db");
        try
        {
            // Create a legacy v4 database manually
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tempDb}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE schema_migrations (
                        version INTEGER PRIMARY KEY,
                        applied_at TEXT NOT NULL,
                        description TEXT NOT NULL
                    );
                    INSERT INTO schema_migrations (version, applied_at, description) VALUES
                        (1, datetime('now'), 'Initial schema'),
                        (2, datetime('now'), 'SNR and overlap'),
                        (3, datetime('now'), 'Waveform envelope'),
                        (4, datetime('now'), 'Model fingerprint');

                    CREATE TABLE scan_results (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_path TEXT NOT NULL,
                        file_hash TEXT NOT NULL,
                        profile_name TEXT NOT NULL,
                        model_id TEXT NOT NULL,
                        threshold REAL NOT NULL,
                        verdict TEXT NOT NULL,
                        max_confidence REAL NOT NULL,
                        segments_json TEXT NOT NULL,
                        scanned_at TEXT NOT NULL
                    );

                    INSERT INTO scan_results (file_path, file_hash, profile_name, model_id, threshold, verdict, max_confidence, segments_json, scanned_at)
                    VALUES ('/legacy.mp4', 'legacy-hash', 'User1', 'model1', 0.5, 'Match', 0.95, '[]', datetime('now'));
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            // Open with VoiceScanDatabase, which should run migration 5
            using (var db = new VoiceScanDatabase(tempDb))
            {
                await db.InitializeAsync();

                // 1. Existing row should now be readable with null/default values
                var legacyResult = await db.GetScanResultAsync("legacy-hash", "User1", "model1", 0.5);
                Assert.NotNull(legacyResult);
                Assert.Equal("/legacy.mp4", legacyResult.FilePath);
                Assert.Null(legacyResult.SpeakerLabel);
                Assert.Null(legacyResult.Transcript);
                Assert.False(legacyResult.IsOffensive);
                Assert.NotNull(legacyResult.ModerationViolations);
                Assert.Empty(legacyResult.ModerationViolations);

                // 2. Writing a new row with new columns works seamlessly
                await db.SaveScanResultAsync(
                    filePath: "/new.mp4",
                    fileHash: "new-hash",
                    profileName: "User1",
                    modelId: "model1",
                    threshold: 0.5,
                    verdict: "Match",
                    maxConfidence: 0.99,
                    segmentsJson: "[]",
                    speakerLabel: "SPEAKER_02",
                    transcript: "Upgraded DB works",
                    isOffensive: false,
                    moderationViolations: new[] { "none" });

                var newResult = await db.GetScanResultAsync("new-hash", "User1", "model1", 0.5);
                Assert.NotNull(newResult);
                Assert.Equal("SPEAKER_02", newResult.SpeakerLabel);
                Assert.Equal("Upgraded DB works", newResult.Transcript);
                Assert.NotNull(newResult.ModerationViolations);
                Assert.Single(newResult.ModerationViolations);
                Assert.Equal("none", newResult.ModerationViolations[0]);
            }
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    private sealed class BenchMockEmbeddingModel : ISpeakerEmbeddingModel
    {
        public string ModelId => "speechbrain-ecapa-tdnn";
        public string ModelVersion => "ecapa-bench-mock@v1";
        public int EmbeddingDimension => 192;
        public string ActiveProvider => "CPU";
        public bool IsCudaActive => false;
        public ModelOperatingPoint OperatingPoint => new(0.5, 0.4);

        public float[] ExtractEmbedding(float[] audioWindow)
        {
            var emb = new float[192];
            emb[0] = 1.0f;
            return emb;
        }

        public float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows)
        {
            // Simulate realistic neural net inference latency (typically 20-40ms per window on CPU)
            System.Threading.Thread.Sleep(audioWindows.Count * 25);
            return audioWindows.Select(_ => ExtractEmbedding(null!)).ToArray();
        }

        public void Dispose() { }
    }
}

