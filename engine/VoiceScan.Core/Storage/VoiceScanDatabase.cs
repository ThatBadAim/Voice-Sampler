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
    private readonly string _connectionString;
    private readonly string _dbFilePath;

    public VoiceScanDatabase(string? dbFilePath = null)
    {
        _dbFilePath = dbFilePath ?? ResolveDefaultDatabasePath();
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

    public static string ResolveDefaultDatabasePath()
    {
        string baseDir = AppContext.BaseDirectory;
        // Check if inside repo
        var cur = new DirectoryInfo(baseDir);
        while (cur != null && !File.Exists(Path.Combine(cur.FullName, "VoiceScan.sln")))
        {
            cur = cur.Parent;
        }

        if (cur != null)
        {
            return Path.Combine(cur.FullName, "voicescan.db");
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "VoiceScan", "voicescan.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Apply migrations
        await ApplyMigrationsAsync(connection, cancellationToken);
    }

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
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        byte[] centroidBytes = FloatArrayToBytes(profile.Centroid);
        string embsJson = JsonSerializer.Serialize(profile.EnrollmentEmbeddings);
        byte[] embsBytes = Encoding.UTF8.GetBytes(embsJson);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO profiles (name, created_at, model_version, centroid, enrollment_embeddings, clip_count, total_speech_duration_seconds)
            VALUES (@name, @created_at, @model_version, @centroid, @enrollment_embeddings, @clip_count, @total_speech_duration_seconds)
            ON CONFLICT(name) DO UPDATE SET
                created_at = excluded.created_at,
                model_version = excluded.model_version,
                centroid = excluded.centroid,
                enrollment_embeddings = excluded.enrollment_embeddings,
                clip_count = excluded.clip_count,
                total_speech_duration_seconds = excluded.total_speech_duration_seconds;
        ";

        cmd.Parameters.AddWithValue("@name", profile.ProfileName);
        cmd.Parameters.AddWithValue("@created_at", profile.CreatedAt);
        cmd.Parameters.AddWithValue("@model_version", profile.ModelId);
        cmd.Parameters.AddWithValue("@centroid", centroidBytes);
        cmd.Parameters.AddWithValue("@enrollment_embeddings", embsBytes);
        cmd.Parameters.AddWithValue("@clip_count", profile.ClipCount);
        cmd.Parameters.AddWithValue("@total_speech_duration_seconds", profile.TotalSpeechDurationSeconds);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VoiceProfile?> GetProfileAsync(string profileName, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, created_at, model_version, centroid, enrollment_embeddings, clip_count, total_speech_duration_seconds FROM profiles WHERE name = @name;";
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

        float[] centroid = BytesToFloatArray(centroidBytes);
        var embs = JsonSerializer.Deserialize<List<float[]>>(Encoding.UTF8.GetString(embsBytes)) ?? new List<float[]>();

        return new VoiceProfile
        {
            ProfileName = name,
            CreatedAt = createdAt,
            ModelId = modelId,
            Centroid = centroid,
            EnrollmentEmbeddings = embs,
            ClipCount = clipCount,
            TotalSpeechDurationSeconds = speechDur
        };
    }

    public async Task<IReadOnlyList<StoredProfileInfo>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
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
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE name = @name;";
        cmd.Parameters.AddWithValue("@name", profileName);

        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    #endregion

    #region Embeddings Cache

    public async Task<IReadOnlyList<CachedWindow>?> GetCachedWindowsAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var getCacheCmd = connection.CreateCommand();
        getCacheCmd.CommandText = "SELECT id, window_count FROM embeddings_cache WHERE cache_key = @key;";
        getCacheCmd.Parameters.AddWithValue("@key", cacheKey);

        long? cacheId = null;
        using (var reader = await getCacheCmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                cacheId = reader.GetInt64(0);
            }
        }

        if (!cacheId.HasValue)
        {
            return null; // Cache miss
        }

        // Fetch windows
        using var fetchCmd = connection.CreateCommand();
        fetchCmd.CommandText = @"
            SELECT start_time_seconds, end_time_seconds, embedding
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
            list.Add(new CachedWindow(start, end, emb));
        }

        return list;
    }

    public async Task SaveCachedWindowsAsync(
        string cacheKey,
        string fileHash,
        string modelVersion,
        string vadSettings,
        string windowSettings,
        IReadOnlyList<CachedWindow> windows,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var tx = connection.BeginTransaction();
        try
        {
            // Insert cache parent record
            using var insCacheCmd = connection.CreateCommand();
            insCacheCmd.Transaction = tx;
            insCacheCmd.CommandText = @"
                INSERT INTO embeddings_cache (cache_key, file_hash, model_version, vad_settings, window_settings, window_count, created_at)
                VALUES (@key, @fhash, @model, @vad, @win, @wcount, datetime('now'))
                ON CONFLICT(cache_key) DO UPDATE SET
                    window_count = excluded.window_count,
                    created_at = excluded.created_at;
                SELECT id FROM embeddings_cache WHERE cache_key = @key;";

            insCacheCmd.Parameters.AddWithValue("@key", cacheKey);
            insCacheCmd.Parameters.AddWithValue("@fhash", fileHash);
            insCacheCmd.Parameters.AddWithValue("@model", modelVersion);
            insCacheCmd.Parameters.AddWithValue("@vad", vadSettings);
            insCacheCmd.Parameters.AddWithValue("@win", windowSettings);
            insCacheCmd.Parameters.AddWithValue("@wcount", windows.Count);

            var cidObj = await insCacheCmd.ExecuteScalarAsync(cancellationToken);
            long cacheId = Convert.ToInt64(cidObj);

            // Delete old windows if replacing
            using var delCmd = connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM cached_window_embeddings WHERE cache_id = @cid;";
            delCmd.Parameters.AddWithValue("@cid", cacheId);
            await delCmd.ExecuteNonQueryAsync(cancellationToken);

            // Batch insert window embeddings
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                byte[] embBytes = FloatArrayToBytes(w.Embedding);

                using var insWinCmd = connection.CreateCommand();
                insWinCmd.Transaction = tx;
                insWinCmd.CommandText = @"
                    INSERT INTO cached_window_embeddings (cache_id, window_index, start_time_seconds, end_time_seconds, embedding)
                    VALUES (@cid, @widx, @start, @end, @emb);";

                insWinCmd.Parameters.AddWithValue("@cid", cacheId);
                insWinCmd.Parameters.AddWithValue("@widx", i);
                insWinCmd.Parameters.AddWithValue("@start", w.StartTimeSeconds);
                insWinCmd.Parameters.AddWithValue("@end", w.EndTimeSeconds);
                insWinCmd.Parameters.AddWithValue("@emb", embBytes);

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
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        if (!string.IsNullOrEmpty(modelVersion) && !string.IsNullOrEmpty(fileHash))
        {
            cmd.CommandText = "DELETE FROM embeddings_cache WHERE model_version = @model AND file_hash = @hash;";
            cmd.Parameters.AddWithValue("@model", modelVersion);
            cmd.Parameters.AddWithValue("@hash", fileHash);
        }
        else if (!string.IsNullOrEmpty(modelVersion))
        {
            cmd.CommandText = "DELETE FROM embeddings_cache WHERE model_version = @model;";
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

    public async Task SaveScanResultAsync(
        string filePath,
        string fileHash,
        string profileName,
        string modelId,
        double threshold,
        string verdict,
        double maxConfidence,
        string segmentsJson,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO scan_results (file_path, file_hash, profile_name, model_id, threshold, verdict, max_confidence, segments_json, scanned_at)
            VALUES (@path, @fhash, @prof, @model, @thresh, @verdict, @maxconf, @segjson, datetime('now'));";

        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@fhash", fileHash);
        cmd.Parameters.AddWithValue("@prof", profileName);
        cmd.Parameters.AddWithValue("@model", modelId);
        cmd.Parameters.AddWithValue("@thresh", threshold);
        cmd.Parameters.AddWithValue("@verdict", verdict);
        cmd.Parameters.AddWithValue("@maxconf", maxConfidence);
        cmd.Parameters.AddWithValue("@segjson", segmentsJson);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        SqliteConnection.ClearAllPools();
    }
}
