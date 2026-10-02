using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceScan.App.Core.Models;
using VoiceScan.Core;

namespace VoiceScan.App.Core.Services;

public interface IReviewRepository : IDisposable
{
    Task InitializeAsync();
    Task RecordDecisionAsync(ReviewDecisionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewDecisionRecord>> GetDecisionsAsync(string? profileName = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewDecisionRecord>> GetNegativeCohortAsync(CancellationToken cancellationToken = default);
    Task<bool> AugmentProfileWithConfirmedHitAsync(string profilePath, ReviewDecisionRecord record, CancellationToken cancellationToken = default);
}

public sealed class ReviewSqliteRepository : IReviewRepository
{
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly ProfileEnrollmentService? _enrollmentService;
    private bool _initialized;

    public ReviewSqliteRepository(string dbPath = "voice_scan_reviews.db", ProfileEnrollmentService? enrollmentService = null)
    {
        _connectionString = $"Data Source={dbPath}";
        _connection = new SqliteConnection(_connectionString);
        _enrollmentService = enrollmentService;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _connection.OpenAsync();

        string ddl = @"
            CREATE TABLE IF NOT EXISTS review_decisions (
                segment_id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                profile_name TEXT NOT NULL,
                start_time REAL NOT NULL,
                end_time REAL NOT NULL,
                confidence REAL NOT NULL,
                original_verdict TEXT NOT NULL,
                reason_flags TEXT NOT NULL,
                decision TEXT NOT NULL,
                decided_at_utc TEXT NOT NULL,
                notes TEXT,
                embedding_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_reviews_profile ON review_decisions(profile_name);
            CREATE INDEX IF NOT EXISTS idx_reviews_decision ON review_decisions(decision);
        ";

        using var cmd = new SqliteCommand(ddl, _connection);
        await cmd.ExecuteNonQueryAsync();
        _initialized = true;
    }

    public async Task RecordDecisionAsync(ReviewDecisionRecord record, CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        string sql = @"
            INSERT INTO review_decisions (
                segment_id, file_path, file_hash, profile_name,
                start_time, end_time, confidence, original_verdict,
                reason_flags, decision, decided_at_utc, notes, embedding_json
            ) VALUES (
                @segId, @filePath, @fileHash, @profile,
                @start, @end, @conf, @verdict,
                @flags, @decision, @timestamp, @notes, @emb
            )
            ON CONFLICT(segment_id) DO UPDATE SET
                decision = excluded.decision,
                decided_at_utc = excluded.decided_at_utc,
                notes = excluded.notes,
                embedding_json = excluded.embedding_json;
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@segId", record.SegmentId);
        cmd.Parameters.AddWithValue("@filePath", record.FilePath);
        cmd.Parameters.AddWithValue("@fileHash", record.FileHash);
        cmd.Parameters.AddWithValue("@profile", record.ProfileName);
        cmd.Parameters.AddWithValue("@start", record.StartTimeSeconds);
        cmd.Parameters.AddWithValue("@end", record.EndTimeSeconds);
        cmd.Parameters.AddWithValue("@conf", record.Confidence);
        cmd.Parameters.AddWithValue("@verdict", record.OriginalVerdict);
        cmd.Parameters.AddWithValue("@flags", JsonSerializer.Serialize(record.ReasonFlags));
        cmd.Parameters.AddWithValue("@decision", record.Decision.ToString());
        cmd.Parameters.AddWithValue("@timestamp", record.DecidedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@notes", (object?)record.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emb", record.SegmentEmbedding != null ? JsonSerializer.Serialize(record.SegmentEmbedding) : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReviewDecisionRecord>> GetDecisionsAsync(string? profileName = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        string sql = profileName == null
            ? "SELECT * FROM review_decisions ORDER BY decided_at_utc DESC"
            : "SELECT * FROM review_decisions WHERE profile_name = @profile ORDER BY decided_at_utc DESC";

        using var cmd = new SqliteCommand(sql, _connection);
        if (profileName != null)
        {
            cmd.Parameters.AddWithValue("@profile", profileName);
        }

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        List<ReviewDecisionRecord> records = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapFromReader(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<ReviewDecisionRecord>> GetNegativeCohortAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        string sql = "SELECT * FROM review_decisions WHERE decision = 'Rejected' AND embedding_json IS NOT NULL";
        using var cmd = new SqliteCommand(sql, _connection);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        List<ReviewDecisionRecord> records = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapFromReader(reader));
        }

        return records;
    }

    public async Task<bool> AugmentProfileWithConfirmedHitAsync(string profilePath, ReviewDecisionRecord record, CancellationToken cancellationToken = default)
    {
        if (record.Decision != ReviewDecision.Confirmed || !File.Exists(profilePath))
        {
            return false;
        }

        // Load existing profile
        var doc = VoiceProfile.LoadFromFile(profilePath);
        if (doc.Centroid.Length == 0) return false;

        float[] confirmedVec = record.SegmentEmbedding ?? [];
        if (confirmedVec.Length != doc.Centroid.Length)
        {
            return false;
        }

        // Weighted moving average: (Centroid * N + confirmedVec) / (N + 1)
        int currentCount = Math.Max(1, doc.ClipCount);
        float[] updatedCentroid = new float[doc.Centroid.Length];
        double sumSq = 0.0;

        for (int i = 0; i < updatedCentroid.Length; i++)
        {
            float val = (doc.Centroid[i] * currentCount + confirmedVec[i]) / (currentCount + 1);
            updatedCentroid[i] = val;
            sumSq += val * val;
        }

        // Re-normalize to unit length
        float norm = (float)Math.Sqrt(sumSq);
        if (norm > 1e-12f)
        {
            for (int i = 0; i < updatedCentroid.Length; i++)
            {
                updatedCentroid[i] /= norm;
            }
        }

        doc.Centroid = updatedCentroid;
        doc.ClipCount = currentCount + 1;
        doc.TotalSpeechDurationSeconds += (record.EndTimeSeconds - record.StartTimeSeconds);
        doc.EnrollmentEmbeddings.Add(confirmedVec);
        doc.CreatedAt = DateTime.UtcNow.ToString("o");

        doc.SaveToFile(profilePath);
        return true;
    }

    private static ReviewDecisionRecord MapFromReader(SqliteDataReader reader)
    {
        string flagsJson = reader.GetString(reader.GetOrdinal("reason_flags"));
        var flags = JsonSerializer.Deserialize<List<string>>(flagsJson) ?? [];

        string? embJson = reader.IsDBNull(reader.GetOrdinal("embedding_json")) ? null : reader.GetString(reader.GetOrdinal("embedding_json"));
        float[]? emb = embJson != null ? JsonSerializer.Deserialize<float[]>(embJson) : null;

        return new ReviewDecisionRecord(
            SegmentId: reader.GetString(reader.GetOrdinal("segment_id")),
            FilePath: reader.GetString(reader.GetOrdinal("file_path")),
            FileHash: reader.GetString(reader.GetOrdinal("file_hash")),
            ProfileName: reader.GetString(reader.GetOrdinal("profile_name")),
            StartTimeSeconds: reader.GetDouble(reader.GetOrdinal("start_time")),
            EndTimeSeconds: reader.GetDouble(reader.GetOrdinal("end_time")),
            Confidence: reader.GetDouble(reader.GetOrdinal("confidence")),
            OriginalVerdict: reader.GetString(reader.GetOrdinal("original_verdict")),
            ReasonFlags: flags,
            Decision: Enum.Parse<ReviewDecision>(reader.GetString(reader.GetOrdinal("decision"))),
            DecidedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("decided_at_utc"))),
            Notes: reader.IsDBNull(reader.GetOrdinal("notes")) ? null : reader.GetString(reader.GetOrdinal("notes")),
            SegmentEmbedding: emb);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
