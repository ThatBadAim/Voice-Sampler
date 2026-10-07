namespace VoiceScan.Core.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite storage layer for VoiceScan profiles, file metadata, embedding cache, and scan results.
/// </summary>
public sealed class VoiceScanDatabase : IDisposable
{
    public const string SchemaVersion = "1.1.0";

    private readonly string _connectionString;
    private readonly string _dbFilePath;

    public VoiceScanDatabase(string? dbFilePath = null)
    {
        _dbFilePath = dbFilePath ?? AppPaths.DatabasePath;
        var dir = Path.GetDirectoryName(_dbFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Apply migrations
            await ApplyMigrationsAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnsureInitializedAsync(cancellationToken);

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var checkTableCmd = connection.CreateCommand();
        checkTableCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT NOT NULL
            );";
        await checkTableCmd.ExecuteNonQueryAsync(cancellationToken);

        int currentVersion = 0;
        using var getVerCmd = connection.CreateCommand();
        getVerCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var verObj = await getVerCmd.ExecuteScalarAsync(cancellationToken);
        if (verObj is long v)
        {
            currentVersion = (int)v;
        }

        if (currentVersion < 1)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS profiles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT UNIQUE NOT NULL,
                    created_at TEXT NOT NULL,
                    model_version TEXT NOT NULL,
                    centroid BLOB NOT NULL,
                    enrollment_embeddings BLOB NOT NULL,
                    clip_count INTEGER NOT NULL,
                    total_speech_duration_seconds REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL,
                    file_hash TEXT UNIQUE NOT NULL,
                    duration_seconds REAL NOT NULL,
                    tracks_json TEXT NOT NULL,
                    last_scanned_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_files_hash ON files (file_hash);

                CREATE TABLE IF NOT EXISTS embeddings_cache (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    cache_key TEXT UNIQUE NOT NULL,
                    file_hash TEXT NOT NULL,
                    model_version TEXT NOT NULL,
                    vad_settings TEXT NOT NULL,
                    window_settings TEXT NOT NULL,
                    window_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_embeddings_cache_key ON embeddings_cache (cache_key);
                CREATE INDEX IF NOT EXISTS idx_embeddings_cache_file ON embeddings_cache (file_hash);
                CREATE INDEX IF NOT EXISTS idx_embeddings_cache_model ON embeddings_cache (model_version);

                CREATE TABLE IF NOT EXISTS cached_window_embeddings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    cache_id INTEGER NOT NULL REFERENCES embeddings_cache(id) ON DELETE CASCADE,
                    window_index INTEGER NOT NULL,
                    start_time_seconds REAL NOT NULL,
                    end_time_seconds REAL NOT NULL,
                    embedding BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_cached_windows ON cached_window_embeddings (cache_id, window_index);

                CREATE TABLE IF NOT EXISTS scan_results (
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
                CREATE INDEX IF NOT EXISTS idx_scan_results ON scan_results (file_hash, profile_name, model_id);

                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (1, datetime('now'), 'Initial schema: profiles, files, embeddings_cache, scan_results');
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        if (currentVersion < 2)
        {
            // Cached rows from before per-window diagnostics cannot be reused: they carry no SNR/overlap evidence.
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM embeddings_cache;
                ALTER TABLE cached_window_embeddings ADD COLUMN snr_db REAL NOT NULL DEFAULT 20.0;
                ALTER TABLE cached_window_embeddings ADD COLUMN suspected_overlap INTEGER NOT NULL DEFAULT 0;
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (2, datetime('now'), 'Per-window SNR and overlap diagnostics in the embedding cache');
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        if (currentVersion < 3)
        {
            // Rows cached before this migration keep duration 0 and no waveform; the scanner falls back to ffprobe for them.
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                ALTER TABLE embeddings_cache ADD COLUMN duration_seconds REAL NOT NULL DEFAULT 0;
                ALTER TABLE embeddings_cache ADD COLUMN waveform_min BLOB;
                ALTER TABLE embeddings_cache ADD COLUMN waveform_max BLOB;
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (3, datetime('now'), 'Duration and waveform envelope in the embedding cache');
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        if (currentVersion < 4)
        {
            // Embeddings cached before the WebRTC VAD port and the model-specific front-ends can never be hit again
            // (their cache keys changed), so they are dropped instead of left to bloat the file.
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM embeddings_cache;
                ALTER TABLE profiles ADD COLUMN model_fingerprint TEXT NOT NULL DEFAULT '';
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (4, datetime('now'), 'Model fingerprint on profiles; drop embeddings from the old front-end');
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        if (currentVersion < 5)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                ALTER TABLE scan_results ADD COLUMN speaker_label TEXT;
                ALTER TABLE scan_results ADD COLUMN transcript TEXT;
                ALTER TABLE scan_results ADD COLUMN is_offensive INTEGER DEFAULT 0;
                ALTER TABLE scan_results ADD COLUMN moderation_violations TEXT;
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (5, datetime('now'), 'SchemaVersion 1.1.0: Add speaker_label, transcript, is_offensive, and moderation_violations to scan_results');
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        if (currentVersion < 6)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sidecar_cache (
                    cache_key TEXT PRIMARY KEY,
                    file_hash TEXT NOT NULL,
                    response_json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sidecar_cache_file ON sidecar_cache (file_hash);
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (6, datetime('now'), 'Cache of sidecar analysis responses');
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
    }

    #region Cache Key Computation

    public static string ComputeCacheKey(
        string fileHash,
        string modelVersion,
        string vadSettings,
        string windowSettings)
    {
        string raw = $"{fileHash}|{modelVersion}|{vadSettings}|{windowSettings}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    #endregion

    #region Profiles CRUD

    public async Task SaveProfileAsync(VoiceProfile profile, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        byte[] centroidBytes = FloatArrayToBytes(profile.Centroid);
        string embsJson = JsonSerializer.Serialize(profile.EnrollmentEmbeddings);
        byte[] embsBytes = Encoding.UTF8.GetBytes(embsJson);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO profiles (name, created_at, model_version, model_fingerprint, centroid, enrollment_embeddings, clip_count, total_speech_duration_seconds)
            VALUES (@name, @created_at, @model_version, @model_fingerprint, @centroid, @enrollment_embeddings, @clip_count, @total_speech_duration_seconds)
            ON CONFLICT(name) DO UPDATE SET
                created_at = excluded.created_at,
                model_version = excluded.model_version,
                model_fingerprint = excluded.model_fingerprint,
                centroid = excluded.centroid,
                enrollment_embeddings = excluded.enrollment_embeddings,
                clip_count = excluded.clip_count,
                total_speech_duration_seconds = excluded.total_speech_duration_seconds;
        ";

        cmd.Parameters.AddWithValue("@name", profile.ProfileName);
        cmd.Parameters.AddWithValue("@created_at", profile.CreatedAt);
        cmd.Parameters.AddWithValue("@model_version", profile.ModelId);
        cmd.Parameters.AddWithValue("@model_fingerprint", profile.ModelVersion);
        cmd.Parameters.AddWithValue("@centroid", centroidBytes);
        cmd.Parameters.AddWithValue("@enrollment_embeddings", embsBytes);
        cmd.Parameters.AddWithValue("@clip_count", profile.ClipCount);
        cmd.Parameters.AddWithValue("@total_speech_duration_seconds", profile.TotalSpeechDurationSeconds);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VoiceProfile?> GetProfileAsync(string profileName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, created_at, model_version, centroid, enrollment_embeddings, clip_count, total_speech_duration_seconds, model_fingerprint FROM profiles WHERE name = @name;";
        cmd.Parameters.AddWithValue("@name", profileName);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string name = reader.GetString(0);
        string createdAt = reader.GetString(1);
        string modelId = reader.GetString(2);
        byte[] centroidBytes = (byte[])reader[3];
        byte[] embsBytes = (byte[])reader[4];
        int clipCount = reader.GetInt32(5);
        double speechDur = reader.GetDouble(6);
        string modelVersion = reader.GetString(7);

        float[] centroid = BytesToFloatArray(centroidBytes);
        var embs = JsonSerializer.Deserialize<List<float[]>>(Encoding.UTF8.GetString(embsBytes)) ?? new List<float[]>();

        return new VoiceProfile
        {
            ProfileName = name,
            CreatedAt = createdAt,
            ModelId = modelId,
            ModelVersion = modelVersion,
            Centroid = centroid,
            EnrollmentEmbeddings = embs,
            ClipCount = clipCount,
            TotalSpeechDurationSeconds = speechDur
        };
    }

    public async Task<IReadOnlyList<StoredProfileInfo>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, model_version, created_at, clip_count, total_speech_duration_seconds FROM profiles ORDER BY name ASC;";

        var list = new List<StoredProfileInfo>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new StoredProfileInfo(
                Name: reader.GetString(0),
                ModelVersion: reader.GetString(1),
                CreatedAt: reader.GetString(2),
                ClipCount: reader.GetInt32(3),
                TotalSpeechDurationSeconds: reader.GetDouble(4)));
        }

        return list;
    }

    public async Task<bool> DeleteProfileAsync(string profileName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE name = @name;";
        cmd.Parameters.AddWithValue("@name", profileName);

        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    #endregion

    #region Sidecar Cache

    /// <summary>The cached sidecar response JSON for <paramref name="cacheKey"/>, or null.</summary>
    public async Task<string?> GetCachedSidecarResponseAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT response_json FROM sidecar_cache WHERE cache_key = @key;";
        cmd.Parameters.AddWithValue("@key", cacheKey);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SaveCachedSidecarResponseAsync(
        string cacheKey,
        string fileHash,
        string responseJson,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sidecar_cache (cache_key, file_hash, response_json, created_at)
            VALUES (@key, @fhash, @json, datetime('now'))
            ON CONFLICT(cache_key) DO UPDATE SET
                response_json = excluded.response_json,
                created_at = excluded.created_at;";
        cmd.Parameters.AddWithValue("@key", cacheKey);
        cmd.Parameters.AddWithValue("@fhash", fileHash);
        cmd.Parameters.AddWithValue("@json", responseJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    #endregion

    #region Embeddings Cache

    public async Task<IReadOnlyList<CachedWindow>?> GetCachedWindowsAsync(string cacheKey, CancellationToken cancellationToken = default) =>
        (await GetCachedScanAsync(cacheKey, cancellationToken))?.Windows;

    public async Task<CachedScan?> GetCachedScanAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var getCacheCmd = connection.CreateCommand();
        getCacheCmd.CommandText = "SELECT id, duration_seconds, waveform_min, waveform_max FROM embeddings_cache WHERE cache_key = @key;";
        getCacheCmd.Parameters.AddWithValue("@key", cacheKey);

        long? cacheId = null;
        CachedFileInfo info = new(0.0);
        using (var reader = await getCacheCmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                cacheId = reader.GetInt64(0);
                info = new CachedFileInfo(
                    reader.GetDouble(1),
                    reader.IsDBNull(2) ? null : BytesToFloatArray((byte[])reader[2]),
                    reader.IsDBNull(3) ? null : BytesToFloatArray((byte[])reader[3]));
            }
        }

        if (!cacheId.HasValue)
        {
            return null; // Cache miss
        }

        using var fetchCmd = connection.CreateCommand();
        fetchCmd.CommandText = @"
            SELECT start_time_seconds, end_time_seconds, embedding, snr_db
            FROM cached_window_embeddings
            WHERE cache_id = @cid
            ORDER BY window_index ASC;";
        fetchCmd.Parameters.AddWithValue("@cid", cacheId.Value);

        var list = new List<CachedWindow>();
        using var winReader = await fetchCmd.ExecuteReaderAsync(cancellationToken);
        while (await winReader.ReadAsync(cancellationToken))
        {
            double start = winReader.GetDouble(0);
            double end = winReader.GetDouble(1);
            byte[] embBytes = (byte[])winReader[2];
            float[] emb = BytesToFloatArray(embBytes);
            list.Add(new CachedWindow(start, end, emb, winReader.GetDouble(3)));
        }

        return new CachedScan(list, info);
    }

    public async Task SaveCachedWindowsAsync(
        string cacheKey,
        string fileHash,
        string modelVersion,
        string vadSettings,
        string windowSettings,
        IReadOnlyList<CachedWindow> windows,
        CancellationToken cancellationToken = default,
        CachedFileInfo? fileInfo = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var tx = connection.BeginTransaction();
        try
        {
            // Insert cache parent record
            using var insCacheCmd = connection.CreateCommand();
            insCacheCmd.Transaction = tx;
            insCacheCmd.CommandText = @"
                INSERT INTO embeddings_cache (cache_key, file_hash, model_version, vad_settings, window_settings, window_count, created_at, duration_seconds, waveform_min, waveform_max)
                VALUES (@key, @fhash, @model, @vad, @win, @wcount, datetime('now'), @dur, @wmin, @wmax)
                ON CONFLICT(cache_key) DO UPDATE SET
                    window_count = excluded.window_count,
                    created_at = excluded.created_at,
                    duration_seconds = excluded.duration_seconds,
                    waveform_min = excluded.waveform_min,
                    waveform_max = excluded.waveform_max;
                SELECT id FROM embeddings_cache WHERE cache_key = @key;";

            insCacheCmd.Parameters.AddWithValue("@key", cacheKey);
            insCacheCmd.Parameters.AddWithValue("@fhash", fileHash);
            insCacheCmd.Parameters.AddWithValue("@model", modelVersion);
            insCacheCmd.Parameters.AddWithValue("@vad", vadSettings);
            insCacheCmd.Parameters.AddWithValue("@win", windowSettings);
            insCacheCmd.Parameters.AddWithValue("@wcount", windows.Count);
            insCacheCmd.Parameters.AddWithValue("@dur", fileInfo?.DurationSeconds ?? 0.0);
            insCacheCmd.Parameters.AddWithValue("@wmin", fileInfo?.WaveformMinPeaks is { } mn ? FloatArrayToBytes(mn) : DBNull.Value);
            insCacheCmd.Parameters.AddWithValue("@wmax", fileInfo?.WaveformMaxPeaks is { } mx ? FloatArrayToBytes(mx) : DBNull.Value);

            var cidObj = await insCacheCmd.ExecuteScalarAsync(cancellationToken);
            long cacheId = Convert.ToInt64(cidObj);

            // Delete old windows if replacing
            using var delCmd = connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM cached_window_embeddings WHERE cache_id = @cid;";
            delCmd.Parameters.AddWithValue("@cid", cacheId);
            await delCmd.ExecuteNonQueryAsync(cancellationToken);

            // Batch insert window embeddings using a single reusable parameterized command
            using var insWinCmd = connection.CreateCommand();
            insWinCmd.Transaction = tx;
            insWinCmd.CommandText = @"
                INSERT INTO cached_window_embeddings (cache_id, window_index, start_time_seconds, end_time_seconds, embedding, snr_db, suspected_overlap)
                VALUES (@cid, @widx, @start, @end, @emb, @snr, 0);";

            var pCid = insWinCmd.Parameters.Add("@cid", SqliteType.Integer);
            var pWidx = insWinCmd.Parameters.Add("@widx", SqliteType.Integer);
            var pStart = insWinCmd.Parameters.Add("@start", SqliteType.Real);
            var pEnd = insWinCmd.Parameters.Add("@end", SqliteType.Real);
            var pEmb = insWinCmd.Parameters.Add("@emb", SqliteType.Blob);
            var pSnr = insWinCmd.Parameters.Add("@snr", SqliteType.Real);

            pCid.Value = cacheId;

            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                byte[] embBytes = FloatArrayToBytes(w.Embedding);

                pWidx.Value = i;
                pStart.Value = w.StartTimeSeconds;
                pEnd.Value = w.EndTimeSeconds;
                pEmb.Value = embBytes;
                pSnr.Value = w.SnrDb;

                await insWinCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> InvalidateCacheAsync(
        string? modelVersion = null,
        string? fileHash = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        if (!string.IsNullOrEmpty(modelVersion) && !string.IsNullOrEmpty(fileHash))
        {
            cmd.CommandText = "DELETE FROM embeddings_cache WHERE (model_version = @model OR model_version LIKE @modelPrefix) AND file_hash = @hash;";
            cmd.Parameters.AddWithValue("@modelPrefix", modelVersion + "@%");
            cmd.Parameters.AddWithValue("@model", modelVersion);
            cmd.Parameters.AddWithValue("@hash", fileHash);
        }
        else if (!string.IsNullOrEmpty(modelVersion))
        {
            cmd.CommandText = "DELETE FROM embeddings_cache WHERE model_version = @model OR model_version LIKE @modelPrefix;";
            cmd.Parameters.AddWithValue("@modelPrefix", modelVersion + "@%");
            cmd.Parameters.AddWithValue("@model", modelVersion);
        }
        else if (!string.IsNullOrEmpty(fileHash))
        {
            cmd.CommandText = "DELETE FROM embeddings_cache WHERE file_hash = @hash;";
            cmd.Parameters.AddWithValue("@hash", fileHash);
        }
        else
        {
            cmd.CommandText = "DELETE FROM embeddings_cache;";
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CacheStats> GetCacheStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        int files = 0, windows = 0, profiles = 0, results = 0;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 
                    (SELECT COUNT(DISTINCT file_hash) FROM embeddings_cache),
                    (SELECT COUNT(*) FROM cached_window_embeddings),
                    (SELECT COUNT(*) FROM profiles),
                    (SELECT COUNT(*) FROM scan_results);";

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                files = reader.GetInt32(0);
                windows = reader.GetInt32(1);
                profiles = reader.GetInt32(2);
                results = reader.GetInt32(3);
            }
        }

        long sizeBytes = File.Exists(_dbFilePath) ? new FileInfo(_dbFilePath).Length : 0;
        return new CacheStats(files, windows, profiles, results, sizeBytes);
    }

    #endregion

    #region Scan Results Persistence

    public Task SaveScanResultAsync(
        string filePath,
        string fileHash,
        string profileName,
        string modelId,
        double threshold,
        string verdict,
        double maxConfidence,
        string segmentsJson,
        CancellationToken cancellationToken = default) =>
        SaveScanResultAsync(
            filePath,
            fileHash,
            profileName,
            modelId,
            threshold,
            verdict,
            maxConfidence,
            segmentsJson,
            speakerLabel: null,
            transcript: null,
            isOffensive: false,
            moderationViolations: null,
            cancellationToken: cancellationToken);

    public Task SaveScanResultAsync(
        StoredScanResult result,
        CancellationToken cancellationToken = default) =>
        SaveScanResultAsync(
            result.FilePath,
            result.FileHash,
            result.ProfileName,
            result.ModelId,
            result.Threshold,
            result.Verdict,
            result.MaxConfidence,
            result.SegmentsJson,
            result.SpeakerLabel,
            result.Transcript,
            result.IsOffensive,
            result.ModerationViolations,
            cancellationToken);

    public async Task SaveScanResultAsync(
        string filePath,
        string fileHash,
        string profileName,
        string modelId,
        double threshold,
        string verdict,
        double maxConfidence,
        string segmentsJson,
        string? speakerLabel,
        string? transcript = null,
        bool isOffensive = false,
        IReadOnlyList<string>? moderationViolations = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // A rescan replaces the stored result instead of growing the table on every run.
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            DELETE FROM scan_results
            WHERE file_hash = @fhash AND profile_name = @prof AND model_id = @model AND threshold = @thresh;
            INSERT INTO scan_results (
                file_path, file_hash, profile_name, model_id, threshold, verdict, max_confidence,
                segments_json, scanned_at, speaker_label, transcript, is_offensive, moderation_violations
            )
            VALUES (
                @path, @fhash, @prof, @model, @thresh, @verdict, @maxconf,
                @segjson, datetime('now'), @speaker, @transcript, @is_offensive, @mod_violations
            );";

        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@fhash", fileHash);
        cmd.Parameters.AddWithValue("@prof", profileName);
        cmd.Parameters.AddWithValue("@model", modelId);
        cmd.Parameters.AddWithValue("@thresh", threshold);
        cmd.Parameters.AddWithValue("@verdict", verdict);
        cmd.Parameters.AddWithValue("@maxconf", maxConfidence);
        cmd.Parameters.AddWithValue("@segjson", segmentsJson);
        cmd.Parameters.AddWithValue("@speaker", (object?)speakerLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@transcript", (object?)transcript ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_offensive", isOffensive ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@mod_violations",
            moderationViolations != null ? JsonSerializer.Serialize(moderationViolations) : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<StoredScanResult?> GetScanResultAsync(
        string fileHash,
        string profileName,
        string modelId,
        double threshold,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, file_path, file_hash, profile_name, model_id, threshold, verdict, max_confidence,
                   segments_json, scanned_at, speaker_label, transcript, is_offensive, moderation_violations
            FROM scan_results
            WHERE file_hash = @fhash AND profile_name = @prof AND model_id = @model AND threshold = @thresh
            LIMIT 1;";

        cmd.Parameters.AddWithValue("@fhash", fileHash);
        cmd.Parameters.AddWithValue("@prof", profileName);
        cmd.Parameters.AddWithValue("@model", modelId);
        cmd.Parameters.AddWithValue("@thresh", threshold);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadStoredScanResult(reader);
    }

    public async Task<IReadOnlyList<StoredScanResult>> GetScanResultsAsync(
        string? fileHash = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        var sb = new StringBuilder(@"
            SELECT id, file_path, file_hash, profile_name, model_id, threshold, verdict, max_confidence,
                   segments_json, scanned_at, speaker_label, transcript, is_offensive, moderation_violations
            FROM scan_results");

        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(fileHash))
        {
            conditions.Add("file_hash = @fhash");
            cmd.Parameters.AddWithValue("@fhash", fileHash);
        }
        if (!string.IsNullOrEmpty(profileName))
        {
            conditions.Add("profile_name = @prof");
            cmd.Parameters.AddWithValue("@prof", profileName);
        }

        if (conditions.Count > 0)
        {
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", conditions));
        }

        sb.Append(" ORDER BY id DESC;");
        cmd.CommandText = sb.ToString();

        var results = new List<StoredScanResult>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadStoredScanResult(reader));
        }

        return results;
    }

    private static StoredScanResult ReadStoredScanResult(SqliteDataReader reader)
    {
        long id = reader.GetInt64(0);
        string filePath = reader.GetString(1);
        string fileHash = reader.GetString(2);
        string profileName = reader.GetString(3);
        string modelId = reader.GetString(4);
        double threshold = reader.GetDouble(5);
        string verdict = reader.GetString(6);
        double maxConfidence = reader.GetDouble(7);
        string segmentsJson = reader.GetString(8);
        string scannedAt = reader.GetString(9);

        string? speakerLabel = reader.IsDBNull(10) ? null : reader.GetString(10);
        string? transcript = reader.IsDBNull(11) ? null : reader.GetString(11);
        bool isOffensive = !reader.IsDBNull(12) && reader.GetInt64(12) != 0;

        IReadOnlyList<string>? moderationViolations = null;
        if (!reader.IsDBNull(13))
        {
            string modJson = reader.GetString(13);
            if (!string.IsNullOrWhiteSpace(modJson))
            {
                try
                {
                    moderationViolations = JsonSerializer.Deserialize<List<string>>(modJson);
                }
                catch
                {
                    moderationViolations = Array.Empty<string>();
                }
            }
        }

        return new StoredScanResult(
            id,
            filePath,
            fileHash,
            profileName,
            modelId,
            threshold,
            verdict,
            maxConfidence,
            segmentsJson,
            scannedAt,
            speakerLabel,
            transcript,
            isOffensive,
            moderationViolations ?? Array.Empty<string>());
    }

    #endregion

    #region Buffer Helpers

    private static byte[] FloatArrayToBytes(float[] floats)
    {
        byte[] bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloatArray(byte[] bytes)
    {
        int count = bytes.Length / sizeof(float);
        float[] floats = new float[count];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    #endregion

    public void Dispose()
    {
        _initLock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
