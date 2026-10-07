namespace VoiceScan.Core.Storage;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VoiceScan.Core.Inference;

public enum IncidentStatus
{
    Unreviewed = 0,
    Confirmed = 1,
    Dismissed = 2
}

public enum ClipAnalysisStatus
{
    Analysed = 0,
    NoSpeech = 1,
    NotAnalysed = 2,
    Error = 3
}

/// <summary>An enrolled voice profile used to name speakers whose voice matches it.</summary>
public sealed record KnownVoice(string Name, float[] Centroid);

/// <summary>What produced an analysis; recorded in the clip's history.</summary>
/// <param name="EmbeddingModelVersion">Version of the embedding model that computed the lines' voice vectors.</param>
/// <param name="Trigger"><see cref="ScanTrigger"/> or <see cref="ReanalysisTrigger"/>.</param>
/// <param name="RangeStart">Start of the re-analysed section; both bounds null for the whole clip.</param>
public sealed record AnalysisRunInfo(
    string EmbeddingModelVersion,
    string Trigger = "Scan",
    AnalysisPreset Preset = AnalysisPreset.Standard,
    double? RangeStart = null,
    double? RangeEnd = null)
{
    public const string ScanTrigger = "Scan";
    public const string ReanalysisTrigger = "Re-analysis";

    public bool IsSection => RangeStart.HasValue || RangeEnd.HasValue;
}

/// <summary>One entry of a clip's analysis history.</summary>
public sealed record AnalysisRun(
    long Id,
    long ClipId,
    DateTimeOffset CreatedAt,
    string Trigger,
    AnalysisPreset Preset,
    double? RangeStart,
    double? RangeEnd,
    string EmbeddingModel,
    IReadOnlyDictionary<string, string> SidecarModels,
    int UtteranceCount,
    ClipAnalysisStatus Status,
    string? Error);

public sealed record ClipSummary(
    long Id,
    string FilePath,
    string FileName,
    double DurationSeconds,
    DateTimeOffset ScannedAt,
    DateTimeOffset? RecordedAt,
    ClipAnalysisStatus Status,
    string? ErrorMessage,
    int SpeakerCount,
    int UtteranceCount,
    int IncidentCount,
    string? EmbeddingModel,
    IReadOnlyDictionary<string, string> SidecarModels);

public sealed record SpeakerSummary(
    long Id,
    string DisplayName,
    string? CustomName,
    string? LinkedProfileName,
    string Notes,
    int ClipCount,
    double TalkTimeSeconds,
    int UtteranceCount,
    int IncidentCount,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen);

/// <summary>One speaker's presence in one clip.</summary>
/// <param name="MatchScore">Cosine similarity to the speaker's voice when linked automatically; null for a new speaker.</param>
/// <param name="CarriedOver">The speaker was kept from the clip's previous analysis by time overlap.</param>
public sealed record SpeakerAppearance(
    long Id,
    long ClipId,
    string FilePath,
    string FileName,
    double ClipDurationSeconds,
    long SpeakerId,
    string SpeakerName,
    string LocalLabel,
    double TalkTimeSeconds,
    int UtteranceCount,
    int IncidentCount,
    double FirstStartSeconds,
    double LastEndSeconds,
    double? MatchScore,
    bool AssignedManually,
    bool CarriedOver,
    DateTimeOffset? RecordedAt);

public sealed record UtteranceRecord(
    long Id,
    long ClipId,
    string FilePath,
    string FileName,
    double ClipDurationSeconds,
    long SpeakerId,
    string SpeakerName,
    double StartSeconds,
    double EndSeconds,
    string Transcript,
    IReadOnlyDictionary<string, double> ClassifierScores,
    IReadOnlyList<WordListEntry> WordHits,
    double MaxScore,
    IncidentStatus Status,
    string Note,
    DateTimeOffset? RecordedAt)
{
    public bool IsIncident(double sensitivity) => WordHits.Count > 0 || MaxScore >= sensitivity;

    /// <summary>Classifier categories at or above the sensitivity, then word-list categories, without duplicates.</summary>
    public IReadOnlyList<string> Categories(double sensitivity) =>
        ClassifierScores.Where(kv => kv.Value >= sensitivity).OrderByDescending(kv => kv.Value).Select(kv => kv.Key)
            .Concat(WordHits.Select(h => h.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// SQLite store for the moderation workflow: analysed clips, speakers linked across clips by voice, every transcribed
/// utterance with its classifier scores and word-list hits, review status, and the word list.
/// Each call opens its own connection, so the store can be used from the scan thread and the UI at the same time.
/// </summary>
public sealed class ModerationStore : IDisposable
{
    private const string SpeakerNameSql =
        "COALESCE(NULLIF(s.custom_name, ''), s.linked_profile_name, 'Speaker ' || s.id)";
    private const string IncidentSql = "(u.word_hit_count > 0 OR u.max_score >= @sensitivity)";

    /// <summary>Minimum overlap, as a fraction of the shorter side, for carrying a speaker or review to new lines.</summary>
    private const double MinOverlapFraction = 0.5;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public ModerationStore(string? dbFilePath = null)
    {
        string path = dbFilePath ?? Path.Combine(AppPaths.DataRoot, "moderation.db");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            using var connection = await OpenAsync(ct);
            using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS clips (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    clip_key TEXT UNIQUE NOT NULL,
                    file_path TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    duration_seconds REAL NOT NULL,
                    scanned_at TEXT NOT NULL,
                    recorded_at TEXT,
                    status INTEGER NOT NULL,
                    error TEXT,
                    embedding_model TEXT,
                    sidecar_models_json TEXT
                );
                CREATE TABLE IF NOT EXISTS speakers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    model_version TEXT NOT NULL,
                    custom_name TEXT,
                    linked_profile_name TEXT,
                    notes TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS appearances (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    speaker_id INTEGER NOT NULL REFERENCES speakers(id),
                    local_label TEXT NOT NULL,
                    vector_sum BLOB,
                    vector_count INTEGER NOT NULL,
                    talk_time_seconds REAL NOT NULL,
                    utterance_count INTEGER NOT NULL,
                    first_start REAL NOT NULL,
                    last_end REAL NOT NULL,
                    match_score REAL,
                    assigned_manually INTEGER NOT NULL DEFAULT 0,
                    carried_over INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (clip_id, local_label)
                );
                CREATE INDEX IF NOT EXISTS idx_appearances_speaker ON appearances (speaker_id);
                CREATE TABLE IF NOT EXISTS utterances (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    appearance_id INTEGER NOT NULL REFERENCES appearances(id) ON DELETE CASCADE,
                    start_seconds REAL NOT NULL,
                    end_seconds REAL NOT NULL,
                    transcript TEXT NOT NULL,
                    scores_json TEXT NOT NULL,
                    max_score REAL NOT NULL,
                    word_hits_json TEXT NOT NULL,
                    word_hit_count INTEGER NOT NULL,
                    status INTEGER NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT '',
                    embedding BLOB,
                    run_id INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_utterances_clip ON utterances (clip_id, start_seconds);
                CREATE INDEX IF NOT EXISTS idx_utterances_appearance ON utterances (appearance_id);
                CREATE TABLE IF NOT EXISTS word_list (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    phrase TEXT UNIQUE NOT NULL COLLATE NOCASE,
                    category TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS analysis_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    preset TEXT NOT NULL,
                    range_start REAL,
                    range_end REAL,
                    embedding_model TEXT NOT NULL,
                    sidecar_models_json TEXT NOT NULL,
                    utterance_count INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    error TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_runs_clip ON analysis_runs (clip_id, id);";
            await cmd.ExecuteNonQueryAsync(ct);
            await MigrateAsync(connection, ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Adds the columns introduced with re-analysis to databases created before it.</summary>
    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        (string Table, string Column, string Definition)[] added =
        [
            ("clips", "embedding_model", "TEXT"),
            ("clips", "sidecar_models_json", "TEXT"),
            ("appearances", "carried_over", "INTEGER NOT NULL DEFAULT 0"),
            ("utterances", "embedding", "BLOB"),
            ("utterances", "run_id", "INTEGER"),
        ];
        foreach (var (table, column, definition) in added)
        {
            bool exists = false;
            using (var info = Command(connection, null, $"PRAGMA table_info({table});"))
            using (var r = await info.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) exists |= r.GetString(1) == column;
            }
            if (exists) continue;
            using var alter = Command(connection, null, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
            await alter.ExecuteNonQueryAsync(ct);
        }

        // Before re-analysis, a clip's embedding model was only recorded on the speakers it created.
        using var backfill = Command(connection, null, @"
            UPDATE clips SET embedding_model = (
                SELECT s.model_version FROM appearances a JOIN speakers s ON s.id = a.speaker_id WHERE a.clip_id = clips.id LIMIT 1)
            WHERE embedding_model IS NULL;");
        await backfill.ExecuteNonQueryAsync(ct);
    }

    #region Ingest

    /// <summary>
    /// Records one analysis of a file. A whole-file analysis replaces the clip's lines; a section analysis replaces only
    /// the lines whose midpoint lies in the section. Speakers, manual assignments and review decisions carry over to
    /// the new lines by time overlap (docs/SPEC-model-swap.md). A result the sidecar did not analyse never overwrites
    /// analysed data. Every call is recorded in the clip's analysis history.
    /// </summary>
    /// <param name="linkThreshold">Minimum cosine similarity for linking an appearance to an existing speaker or profile.</param>
    /// <param name="targetClipId">The clip being re-analysed; by default the clip is found by the file's content hash.</param>
    /// <exception cref="InvalidOperationException">
    /// A section analysis of a clip that is not analysed, or that was analysed with another embedding model.
    /// </exception>
    public async Task IngestAsync(
        FileScanResult result,
        AnalysisRunInfo run,
        IReadOnlyList<KnownVoice> knownVoices,
        double linkThreshold,
        long? targetClipId = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        string fullPath = Path.GetFullPath(result.FilePath);
        string clipKey = string.IsNullOrEmpty(result.FileHash) ? "path:" + fullPath : result.FileHash;
        var segments = result.AnalyzerUsed ? result.Segments : new List<DetectedSegment>();
        var status = result.Error is not null ? ClipAnalysisStatus.Error
            : !result.AnalyzerUsed ? ClipAnalysisStatus.NotAnalysed
            : segments.Count == 0 ? ClipAnalysisStatus.NoSpeech
            : ClipAnalysisStatus.Analysed;
        bool hasAnalysis = status is ClipAnalysisStatus.Analysed or ClipAnalysisStatus.NoSpeech;
        string sidecarModels = JsonSerializer.Serialize(result.AnalyzerModels ?? new Dictionary<string, string>());

        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        var existing = targetClipId.HasValue
            ? await FindClipAsync(connection, tx, "id", targetClipId.Value, ct)
                ?? throw new InvalidOperationException($"Clip {targetClipId} does not exist.")
            : await FindClipAsync(connection, tx, "clip_key", clipKey, ct);
        bool existingAnalysed = existing?.Status is ClipAnalysisStatus.Analysed or ClipAnalysisStatus.NoSpeech;

        if (!hasAnalysis && (existingAnalysed || run.IsSection))
        {
            if (existing != null)
            {
                using var touch = Command(connection, tx, "UPDATE clips SET file_path = @path, file_name = @name WHERE id = @id;");
                touch.Parameters.AddWithValue("@path", fullPath);
                touch.Parameters.AddWithValue("@name", Path.GetFileName(fullPath));
                touch.Parameters.AddWithValue("@id", existing.Id);
                await touch.ExecuteNonQueryAsync(ct);
                await InsertRunAsync(connection, tx, existing.Id, run, sidecarModels, 0, status, result.Error, ct);
            }
            await tx.CommitAsync(ct);
            return;
        }

        if (run.IsSection)
        {
            if (existing == null || !existingAnalysed)
            {
                throw new InvalidOperationException("Only an analysed clip can have a section re-analysed. Re-analyse the whole clip.");
            }
            if (existing.EmbeddingModel != null && existing.EmbeddingModel != run.EmbeddingModelVersion)
            {
                throw new InvalidOperationException(
                    $"This clip was analysed with the voice model {existing.EmbeddingModel}, and a section must use the same model. Re-analyse the whole clip.");
            }
            await MergeSectionAsync(connection, tx, existing.Id, segments, status, run, sidecarModels, knownVoices, linkThreshold, ct);
        }
        else
        {
            string? recordedAt = File.Exists(fullPath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture)
                : null;
            await ReplaceClipAsync(connection, tx, existing?.Id, clipKey, fullPath, result, segments, status, recordedAt, run,
                sidecarModels, knownVoices, linkThreshold, ct);
        }

        await DeleteOrphanSpeakersAsync(connection, tx, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Records an analysis that failed before it produced a result (for example, the sidecar did not answer).</summary>
    public async Task RecordFailedRunAsync(long clipId, AnalysisRunInfo run, string error, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        await InsertRunAsync(connection, tx, clipId, run, "{}", 0, ClipAnalysisStatus.Error, error, ct);
        await tx.CommitAsync(ct);
    }

    private async Task ReplaceClipAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        long? existingId,
        string clipKey,
        string fullPath,
        FileScanResult result,
        List<DetectedSegment> segments,
        ClipAnalysisStatus status,
        string? recordedAt,
        AnalysisRunInfo run,
        string sidecarModels,
        IReadOnlyList<KnownVoice> knownVoices,
        double linkThreshold,
        CancellationToken ct)
    {
        string now = Now();
        long clipId;
        List<ClipAppearance> previousAppearances = [];
        List<ClipLine> previousLines = [];
        if (existingId.HasValue)
        {
            clipId = existingId.Value;
            (previousAppearances, previousLines) = await ReadClipLinesAsync(connection, tx, clipId, ct);
            using (var clear = Command(connection, tx, "DELETE FROM appearances WHERE clip_id = @id;"))
            {
                clear.Parameters.AddWithValue("@id", clipId);
                await clear.ExecuteNonQueryAsync(ct);
            }
            // The key follows the file's current content unless another clip already has that content.
            using var update = Command(connection, tx, @"
                UPDATE clips SET
                    clip_key = CASE WHEN EXISTS (SELECT 1 FROM clips o WHERE o.clip_key = @key AND o.id <> @id) THEN clip_key ELSE @key END,
                    file_path = @path, file_name = @name, duration_seconds = @duration, scanned_at = @scanned,
                    recorded_at = @recorded, status = @status, error = @error, embedding_model = @model, sidecar_models_json = @sidecar
                WHERE id = @id;");
            update.Parameters.AddWithValue("@key", clipKey);
            AddClipParameters(update, fullPath, result, now, recordedAt, status, run.EmbeddingModelVersion, sidecarModels);
            update.Parameters.AddWithValue("@id", clipId);
            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            using var insert = Command(connection, tx, @"
                INSERT INTO clips (clip_key, file_path, file_name, duration_seconds, scanned_at, recorded_at, status, error,
                    embedding_model, sidecar_models_json)
                VALUES (@key, @path, @name, @duration, @scanned, @recorded, @status, @error, @model, @sidecar) RETURNING id;");
            insert.Parameters.AddWithValue("@key", clipKey);
            AddClipParameters(insert, fullPath, result, now, recordedAt, status, run.EmbeddingModelVersion, sidecarModels);
            clipId = (long)(await insert.ExecuteScalarAsync(ct))!;
        }

        if (clipKey != "path:" + fullPath)
        {
            // An earlier failed scan of this path was keyed by path because it had no content hash.
            using var stale = Command(connection, tx, "DELETE FROM clips WHERE clip_key = @key AND id <> @id;");
            stale.Parameters.AddWithValue("@key", "path:" + fullPath);
            stale.Parameters.AddWithValue("@id", clipId);
            await stale.ExecuteNonQueryAsync(ct);
        }

        long runId = await InsertRunAsync(connection, tx, clipId, run, sidecarModels, segments.Count, status, result.Error, ct);
        if (segments.Count == 0) return;

        var words = await ReadWordListAsync(connection, tx, ct);
        var speakers = await ReadSpeakerVoicesAsync(connection, tx, run.EmbeddingModelVersion, ct);
        var groups = GroupByLabel(segments);
        var previousSpans = previousAppearances
            .Select(a => new PreviousSpan(a, previousLines.Where(l => l.AppearanceId == a.Id).Select(l => (l.Start, l.End)).ToList()))
            .ToList();
        var carried = MatchByOverlap(groups, previousSpans);
        var usedInClip = carried.Values.Select(a => a.SpeakerId).ToHashSet();
        var reviews = CarryReviews(segments, previousLines);

        foreach (var group in groups)
        {
            long speakerId;
            double? matchScore = null;
            bool manual = false;
            bool carriedOver = false;
            if (carried.TryGetValue(group, out var previous))
            {
                speakerId = previous.SpeakerId;
                manual = previous.Manual;
                carriedOver = !previous.Manual;
            }
            else
            {
                (speakerId, matchScore) = await LinkOrCreateSpeakerAsync(
                    connection, tx, group, speakers, usedInClip, run.EmbeddingModelVersion, linkThreshold, ct);
                usedInClip.Add(speakerId);
            }

            if (!manual) await NameAfterKnownVoiceAsync(connection, tx, speakerId, group, speakers, knownVoices, linkThreshold, ct);
            long appearanceId = await InsertAppearanceAsync(connection, tx, clipId, speakerId, group.Label, matchScore, manual, carriedOver, ct);
            foreach (var seg in group.Segments)
            {
                await InsertUtteranceAsync(connection, tx, clipId, appearanceId, runId, seg, words, reviews.GetValueOrDefault(seg), ct);
            }
        }

        await RecomputeAppearancesAsync(connection, tx, clipId, ct);
    }

    private async Task MergeSectionAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        long clipId,
        List<DetectedSegment> segments,
        ClipAnalysisStatus status,
        AnalysisRunInfo run,
        string sidecarModels,
        IReadOnlyList<KnownVoice> knownVoices,
        double linkThreshold,
        CancellationToken ct)
    {
        double from = run.RangeStart ?? double.NegativeInfinity;
        double to = run.RangeEnd ?? double.PositiveInfinity;
        var (appearances, lines) = await ReadClipLinesAsync(connection, tx, clipId, ct);
        var replaced = lines.Where(l => InRange(l.Start, l.End, from, to)).ToList();

        using (var delete = Command(connection, tx, "DELETE FROM utterances WHERE id = @id;"))
        {
            var idParam = delete.Parameters.Add("@id", SqliteType.Integer);
            foreach (var line in replaced)
            {
                idParam.Value = line.Id;
                await delete.ExecuteNonQueryAsync(ct);
            }
        }

        long runId = await InsertRunAsync(connection, tx, clipId, run, sidecarModels, segments.Count, status, null, ct);
        if (segments.Count > 0)
        {
            var words = await ReadWordListAsync(connection, tx, ct);
            var groups = GroupByLabel(segments);
            var previousSpans = appearances
                .Select(a => new PreviousSpan(a, replaced.Where(l => l.AppearanceId == a.Id).Select(l => (l.Start, l.End)).ToList()))
                .Where(p => p.Intervals.Count > 0)
                .ToList();
            var mapped = MatchByOverlap(groups, previousSpans);
            var usedAppearances = mapped.Values.Select(a => a.Id).ToHashSet();
            var clipSpeakers = appearances.Select(a => a.SpeakerId).ToHashSet();
            var reviews = CarryReviews(segments, replaced);
            Dictionary<long, SpeakerVoice>? speakers = null;

            foreach (var group in groups)
            {
                long appearanceId;
                if (mapped.TryGetValue(group, out var previous))
                {
                    appearanceId = previous.Id;
                }
                else if (BestAppearance(group, appearances.Where(a => !usedAppearances.Contains(a.Id)), linkThreshold) is { } own)
                {
                    // Labels in a section are local to it, so the clip's own speakers are the likeliest match.
                    appearanceId = own.Id;
                }
                else
                {
                    speakers ??= await ReadSpeakerVoicesAsync(connection, tx, run.EmbeddingModelVersion, ct);
                    var (speakerId, matchScore) = await LinkOrCreateSpeakerAsync(
                        connection, tx, group, speakers, clipSpeakers, run.EmbeddingModelVersion, linkThreshold, ct);
                    clipSpeakers.Add(speakerId);
                    await NameAfterKnownVoiceAsync(connection, tx, speakerId, group, speakers, knownVoices, linkThreshold, ct);
                    string label = string.Create(CultureInfo.InvariantCulture, $"{group.Label} (run {runId})");
                    appearanceId = await InsertAppearanceAsync(connection, tx, clipId, speakerId, label, matchScore, false, false, ct);
                }

                usedAppearances.Add(appearanceId);
                foreach (var seg in group.Segments)
                {
                    await InsertUtteranceAsync(connection, tx, clipId, appearanceId, runId, seg, words, reviews.GetValueOrDefault(seg), ct);
                }
            }
        }

        await RecomputeAppearancesAsync(connection, tx, clipId, ct);
        using var update = Command(connection, tx, @"
            UPDATE clips SET
                status = CASE WHEN EXISTS (SELECT 1 FROM utterances u WHERE u.clip_id = @id) THEN @analysed ELSE @noSpeech END,
                embedding_model = COALESCE(embedding_model, @model)
            WHERE id = @id;");
        update.Parameters.AddWithValue("@id", clipId);
        update.Parameters.AddWithValue("@analysed", (int)ClipAnalysisStatus.Analysed);
        update.Parameters.AddWithValue("@noSpeech", (int)ClipAnalysisStatus.NoSpeech);
        update.Parameters.AddWithValue("@model", run.EmbeddingModelVersion);
        await update.ExecuteNonQueryAsync(ct);
    }

    private static bool InRange(double start, double end, double from, double to)
    {
        double mid = (start + end) / 2.0;
        return mid >= from && mid <= to;
    }

    private static void AddClipParameters(
        SqliteCommand cmd, string fullPath, FileScanResult result, string scannedAt, string? recordedAt, ClipAnalysisStatus status,
        string embeddingModel, string sidecarModels)
    {
        cmd.Parameters.AddWithValue("@path", fullPath);
        cmd.Parameters.AddWithValue("@name", Path.GetFileName(fullPath));
        cmd.Parameters.AddWithValue("@duration", result.DurationSeconds);
        cmd.Parameters.AddWithValue("@scanned", scannedAt);
        cmd.Parameters.AddWithValue("@recorded", (object?)recordedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@error", (object?)result.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@model", embeddingModel);
        cmd.Parameters.AddWithValue("@sidecar", sidecarModels);
    }

    private static async Task<ClipRow?> FindClipAsync(SqliteConnection connection, SqliteTransaction tx, string column, object value, CancellationToken ct)
    {
        using var find = Command(connection, tx, $"SELECT id, status, embedding_model FROM clips WHERE {column} = @value;");
        find.Parameters.AddWithValue("@value", value);
        using var reader = await find.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ClipRow(reader.GetInt64(0), (ClipAnalysisStatus)reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    private static async Task<(List<ClipAppearance> Appearances, List<ClipLine> Lines)> ReadClipLinesAsync(
        SqliteConnection connection, SqliteTransaction tx, long clipId, CancellationToken ct)
    {
        var appearances = new List<ClipAppearance>();
        using (var cmd = Command(connection, tx,
            "SELECT id, speaker_id, assigned_manually, vector_sum FROM appearances WHERE clip_id = @clip;"))
        {
            cmd.Parameters.AddWithValue("@clip", clipId);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                appearances.Add(new ClipAppearance(
                    r.GetInt64(0), r.GetInt64(1), r.GetInt32(2) == 1, r.IsDBNull(3) ? null : FromBytes((byte[])r.GetValue(3))));
            }
        }

        var lines = new List<ClipLine>();
        using (var cmd = Command(connection, tx,
            "SELECT id, appearance_id, start_seconds, end_seconds, status, note FROM utterances WHERE clip_id = @clip ORDER BY start_seconds;"))
        {
            cmd.Parameters.AddWithValue("@clip", clipId);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new ClipLine(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetDouble(3), r.GetInt32(4), r.GetString(5)));
            }
        }
        return (appearances, lines);
    }

    private static async Task<long> InsertRunAsync(
        SqliteConnection connection, SqliteTransaction tx, long clipId, AnalysisRunInfo run, string sidecarModels,
        int utteranceCount, ClipAnalysisStatus status, string? error, CancellationToken ct)
    {
        using var cmd = Command(connection, tx, @"
            INSERT INTO analysis_runs (clip_id, created_at, trigger_name, preset, range_start, range_end, embedding_model,
                sidecar_models_json, utterance_count, status, error)
            VALUES (@clip, @created, @trigger, @preset, @from, @to, @model, @sidecar, @count, @status, @error) RETURNING id;");
        cmd.Parameters.AddWithValue("@clip", clipId);
        cmd.Parameters.AddWithValue("@created", Now());
        cmd.Parameters.AddWithValue("@trigger", run.Trigger);
        cmd.Parameters.AddWithValue("@preset", run.Preset.ToString());
        cmd.Parameters.AddWithValue("@from", (object?)run.RangeStart ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@to", (object?)run.RangeEnd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@model", run.EmbeddingModelVersion);
        cmd.Parameters.AddWithValue("@sidecar", sidecarModels);
        cmd.Parameters.AddWithValue("@count", utteranceCount);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> InsertAppearanceAsync(
        SqliteConnection connection, SqliteTransaction tx, long clipId, long speakerId, string label, double? matchScore,
        bool manual, bool carriedOver, CancellationToken ct)
    {
        // Totals and the voice vector are filled in from the lines by RecomputeAppearancesAsync.
        using var cmd = Command(connection, tx, @"
            INSERT INTO appearances (clip_id, speaker_id, local_label, vector_sum, vector_count, talk_time_seconds,
                utterance_count, first_start, last_end, match_score, assigned_manually, carried_over)
            VALUES (@clip, @speaker, @label, NULL, 0, 0, 0, 0, 0, @match, @manual, @carried) RETURNING id;");
        cmd.Parameters.AddWithValue("@clip", clipId);
        cmd.Parameters.AddWithValue("@speaker", speakerId);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@match", (object?)matchScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@manual", manual ? 1 : 0);
        cmd.Parameters.AddWithValue("@carried", carriedOver ? 1 : 0);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task InsertUtteranceAsync(
        SqliteConnection connection, SqliteTransaction tx, long clipId, long appearanceId, long runId, DetectedSegment seg,
        IReadOnlyList<WordListEntry> words, (int Status, string Note) review, CancellationToken ct)
    {
        var scores = ClassifierScores(seg);
        var hits = WordListMatcher.FindHits(seg.Transcript, words);
        float[]? unit = seg.Embedding is { Length: > 0 } emb ? Normalize(emb) : null;

        using var cmd = Command(connection, tx, @"
            INSERT INTO utterances (clip_id, appearance_id, start_seconds, end_seconds, transcript, scores_json,
                max_score, word_hits_json, word_hit_count, status, note, embedding, run_id)
            VALUES (@clip, @appearance, @start, @end, @text, @scores, @max, @hits, @hitCount, @status, @note, @embedding, @run);");
        cmd.Parameters.AddWithValue("@clip", clipId);
        cmd.Parameters.AddWithValue("@appearance", appearanceId);
        cmd.Parameters.AddWithValue("@start", seg.StartTimeSeconds);
        cmd.Parameters.AddWithValue("@end", seg.EndTimeSeconds);
        cmd.Parameters.AddWithValue("@text", seg.Transcript?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("@scores", JsonSerializer.Serialize(scores));
        cmd.Parameters.AddWithValue("@max", scores.Count > 0 ? scores.Values.Max() : 0.0);
        cmd.Parameters.AddWithValue("@hits", JsonSerializer.Serialize(hits));
        cmd.Parameters.AddWithValue("@hitCount", hits.Count);
        cmd.Parameters.AddWithValue("@status", review.Status);
        cmd.Parameters.AddWithValue("@note", review.Note ?? string.Empty);
        cmd.Parameters.AddWithValue("@embedding", unit != null ? ToBytes(unit) : DBNull.Value);
        cmd.Parameters.AddWithValue("@run", runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Rebuilds each appearance's totals and voice vector (sum of its lines' unit embeddings) from its lines, and removes
    /// appearances left without lines. Lines stored before embeddings were kept leave an appearance's vector unchanged.
    /// </summary>
    private static async Task RecomputeAppearancesAsync(SqliteConnection connection, SqliteTransaction tx, long clipId, CancellationToken ct)
    {
        var lines = new Dictionary<long, List<(double Start, double End, float[]? Embedding)>>();
        using (var cmd = Command(connection, tx, @"
            SELECT a.id, u.start_seconds, u.end_seconds, u.embedding
            FROM appearances a LEFT JOIN utterances u ON u.appearance_id = a.id
            WHERE a.clip_id = @clip;"))
        {
            cmd.Parameters.AddWithValue("@clip", clipId);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long id = r.GetInt64(0);
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                if (r.IsDBNull(1)) continue;
                list.Add((r.GetDouble(1), r.GetDouble(2), r.IsDBNull(3) ? null : FromBytes((byte[])r.GetValue(3))));
            }
        }

        foreach (var (appearanceId, list) in lines)
        {
            if (list.Count == 0)
            {
                using var delete = Command(connection, tx, "DELETE FROM appearances WHERE id = @id;");
                delete.Parameters.AddWithValue("@id", appearanceId);
                await delete.ExecuteNonQueryAsync(ct);
                continue;
            }

            float[]? sum = null;
            int vectors = 0;
            foreach (var (_, _, embedding) in list)
            {
                if (embedding == null || (sum != null && embedding.Length != sum.Length)) continue;
                sum = Add(sum, embedding);
                vectors++;
            }

            using var update = Command(connection, tx, @"
                UPDATE appearances SET talk_time_seconds = @talk, utterance_count = @count, first_start = @first, last_end = @last,
                    vector_sum = CASE WHEN @vectors > 0 THEN @sum ELSE vector_sum END,
                    vector_count = CASE WHEN @vectors > 0 THEN @vectors ELSE vector_count END
                WHERE id = @id;");
            update.Parameters.AddWithValue("@talk", Math.Round(list.Sum(l => Math.Max(0.0, l.End - l.Start)), 3));
            update.Parameters.AddWithValue("@count", list.Count);
            update.Parameters.AddWithValue("@first", list.Min(l => l.Start));
            update.Parameters.AddWithValue("@last", list.Max(l => l.End));
            update.Parameters.AddWithValue("@vectors", vectors);
            update.Parameters.AddWithValue("@sum", sum != null ? ToBytes(sum) : DBNull.Value);
            update.Parameters.AddWithValue("@id", appearanceId);
            await update.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Links the group to the most similar speaker not in <paramref name="exclude"/>, or creates a new speaker.</summary>
    private static async Task<(long SpeakerId, double? MatchScore)> LinkOrCreateSpeakerAsync(
        SqliteConnection connection, SqliteTransaction tx, LabelGroup group, Dictionary<long, SpeakerVoice> speakers,
        IReadOnlySet<long> exclude, string modelVersion, double linkThreshold, CancellationToken ct)
    {
        float[]? voice = group.NormalizedVector;
        long? best = null;
        double bestScore = double.NegativeInfinity;
        if (voice != null)
        {
            foreach (var (id, known) in speakers)
            {
                if (exclude.Contains(id) || known.Sum == null || known.Sum.Length != voice.Length) continue;
                double score = Cosine(voice, known.Sum);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = id;
                }
            }
        }

        if (best.HasValue && bestScore >= linkThreshold)
        {
            return (best.Value, Math.Round(bestScore, 4));
        }

        long created = await InsertSpeakerAsync(connection, tx, modelVersion, ct);
        speakers[created] = new SpeakerVoice(null, null, null);
        return (created, null);
    }

    private static async Task NameAfterKnownVoiceAsync(
        SqliteConnection connection, SqliteTransaction tx, long speakerId, LabelGroup group, Dictionary<long, SpeakerVoice> speakers,
        IReadOnlyList<KnownVoice> knownVoices, double linkThreshold, CancellationToken ct)
    {
        if (group.NormalizedVector is not { } voice || !speakers.TryGetValue(speakerId, out var current)
            || current.CustomName != null || current.LinkedProfile != null)
        {
            return;
        }

        string? profile = MatchKnownVoice(voice, knownVoices, linkThreshold);
        if (profile == null) return;

        using var name = Command(connection, tx, "UPDATE speakers SET linked_profile_name = @name WHERE id = @id;");
        name.Parameters.AddWithValue("@name", profile);
        name.Parameters.AddWithValue("@id", speakerId);
        await name.ExecuteNonQueryAsync(ct);
        speakers[speakerId] = current with { LinkedProfile = profile };
    }

    private static ClipAppearance? BestAppearance(LabelGroup group, IEnumerable<ClipAppearance> candidates, double linkThreshold)
    {
        if (group.NormalizedVector is not { } voice) return null;
        return candidates
            .Where(a => a.VectorSum is { } sum && sum.Length == voice.Length)
            .Select(a => (Appearance: a, Score: Cosine(voice, a.VectorSum!)))
            .Where(x => x.Score >= linkThreshold)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Appearance)
            .FirstOrDefault();
    }

    /// <summary>
    /// Pairs each new label with the previous appearance it overlaps most in time, greedily from the largest overlap.
    /// A pair needs an overlap of at least half the talk time of the smaller side; each speaker is used once.
    /// </summary>
    private static Dictionary<LabelGroup, ClipAppearance> MatchByOverlap(List<LabelGroup> groups, List<PreviousSpan> previous)
    {
        var pairs = new List<(LabelGroup Group, ClipAppearance Appearance, double Overlap)>();
        foreach (var group in groups)
        {
            foreach (var span in previous)
            {
                double overlap = Overlap(group.Intervals, span.Intervals);
                if (overlap > 0 && overlap >= MinOverlapFraction * Math.Min(group.TalkTime, span.TalkTime))
                {
                    pairs.Add((group, span.Appearance, overlap));
                }
            }
        }

        var matched = new Dictionary<LabelGroup, ClipAppearance>();
        var usedSpeakers = new HashSet<long>();
        foreach (var (group, appearance, _) in pairs.OrderByDescending(p => p.Overlap))
        {
            if (matched.ContainsKey(group) || !usedSpeakers.Add(appearance.SpeakerId)) continue;
            matched[group] = appearance;
        }
        return matched;
    }

    /// <summary>
    /// Passes each previously reviewed or annotated line's status and note to the new line it overlaps most (at least half
    /// the shorter of the two), one new line per old line.
    /// </summary>
    private static Dictionary<DetectedSegment, (int Status, string Note)> CarryReviews(
        List<DetectedSegment> segments, IEnumerable<ClipLine> previousLines)
    {
        var pairs = new List<(DetectedSegment Segment, ClipLine Line, double Overlap)>();
        foreach (var line in previousLines.Where(l => l.Status != 0 || l.Note.Length > 0))
        {
            foreach (var seg in segments)
            {
                double overlap = Math.Min(seg.EndTimeSeconds, line.End) - Math.Max(seg.StartTimeSeconds, line.Start);
                double shorter = Math.Min(seg.EndTimeSeconds - seg.StartTimeSeconds, line.End - line.Start);
                if (overlap > 0 && overlap >= MinOverlapFraction * shorter) pairs.Add((seg, line, overlap));
            }
        }

        var carried = new Dictionary<DetectedSegment, (int Status, string Note)>(ReferenceEqualityComparer.Instance);
        var usedLines = new HashSet<long>();
        foreach (var (seg, line, _) in pairs.OrderByDescending(p => p.Overlap))
        {
            if (carried.ContainsKey(seg) || !usedLines.Add(line.Id)) continue;
            carried[seg] = (line.Status, line.Note);
        }
        return carried;
    }

    private static double Overlap(IReadOnlyList<(double Start, double End)> a, IReadOnlyList<(double Start, double End)> b)
    {
        double total = 0.0;
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                total += Math.Max(0.0, Math.Min(x.End, y.End) - Math.Max(x.Start, y.Start));
            }
        }
        return total;
    }

    private static List<LabelGroup> GroupByLabel(List<DetectedSegment> segments) => segments
        .GroupBy(s => string.IsNullOrWhiteSpace(s.SpeakerLabel) ? "UNKNOWN" : s.SpeakerLabel!.Trim())
        .Select(g => new LabelGroup(g.Key, g.OrderBy(s => s.StartTimeSeconds).ToList()))
        .OrderByDescending(g => g.VectorCount)
        .ThenByDescending(g => g.TalkTime)
        .ToList();

    /// <summary>Sidecar scores per category; labels it reported without a score (its regex fallback) count as 1.0.</summary>
    private static Dictionary<string, double> ClassifierScores(DetectedSegment seg)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (seg.ModerationScores != null)
        {
            foreach (var (label, score) in seg.ModerationScores) scores[label] = score;
        }
        foreach (var label in seg.ModerationViolations)
        {
            if (!scores.ContainsKey(label)) scores[label] = 1.0;
        }
        return scores;
    }

    private static string? MatchKnownVoice(float[] voice, IReadOnlyList<KnownVoice> knownVoices, double threshold)
    {
        string? bestName = null;
        double bestScore = threshold;
        foreach (var known in knownVoices)
        {
            if (known.Centroid.Length != voice.Length) continue;
            double score = Cosine(voice, known.Centroid);
            if (score >= bestScore)
            {
                bestScore = score;
                bestName = known.Name;
            }
        }
        return bestName;
    }

    private sealed record ClipRow(long Id, ClipAnalysisStatus Status, string? EmbeddingModel);

    private sealed record ClipAppearance(long Id, long SpeakerId, bool Manual, float[]? VectorSum);

    private sealed record ClipLine(long Id, long AppearanceId, double Start, double End, int Status, string Note);

    private sealed record PreviousSpan(ClipAppearance Appearance, List<(double Start, double End)> Intervals)
    {
        public double TalkTime { get; } = Intervals.Sum(i => Math.Max(0.0, i.End - i.Start));
    }

    private sealed record SpeakerVoice(float[]? Sum, string? CustomName, string? LinkedProfile);

    private sealed class LabelGroup
    {
        public LabelGroup(string label, List<DetectedSegment> segments)
        {
            Label = label;
            Segments = segments;
            Intervals = segments.Select(s => (s.StartTimeSeconds, s.EndTimeSeconds)).ToList();
            TalkTime = segments.Sum(s => Math.Max(0.0, s.EndTimeSeconds - s.StartTimeSeconds));
            float[]? sum = null;
            foreach (var seg in segments)
            {
                if (seg.Embedding is not { Length: > 0 } emb || (sum != null && emb.Length != sum.Length)) continue;
                float[]? unit = Normalize(emb);
                if (unit == null) continue;
                sum = Add(sum, unit);
                VectorCount++;
            }
            NormalizedVector = sum != null ? Normalize(sum) : null;
        }

        public string Label { get; }
        public List<DetectedSegment> Segments { get; }
        public List<(double Start, double End)> Intervals { get; }
        public double TalkTime { get; }
        public int VectorCount { get; }
        public float[]? NormalizedVector { get; }
    }

    #endregion

    #region Queries

    public async Task<IReadOnlyList<ClipSummary>> GetClipsAsync(double sensitivity, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = Command(connection, null, $@"
            SELECT c.id, c.file_path, c.file_name, c.duration_seconds, c.scanned_at, c.recorded_at, c.status, c.error,
                (SELECT COUNT(DISTINCT a.speaker_id) FROM appearances a WHERE a.clip_id = c.id),
                (SELECT COUNT(*) FROM utterances u WHERE u.clip_id = c.id),
                (SELECT COUNT(*) FROM utterances u WHERE u.clip_id = c.id AND {IncidentSql}),
                c.embedding_model, c.sidecar_models_json
            FROM clips c
            ORDER BY COALESCE(c.recorded_at, c.scanned_at) DESC, c.file_name;");
        cmd.Parameters.AddWithValue("@sensitivity", sensitivity);

        var clips = new List<ClipSummary>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            clips.Add(new ClipSummary(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3), ParseTime(r.GetString(4)),
                r.IsDBNull(5) ? null : ParseTime(r.GetString(5)), (ClipAnalysisStatus)r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
                r.IsDBNull(11) ? null : r.GetString(11), ParseModels(r.IsDBNull(12) ? null : r.GetString(12))));
        }
        return clips;
    }

    /// <summary>The clip's analysis history, newest first.</summary>
    public async Task<IReadOnlyList<AnalysisRun>> GetAnalysisRunsAsync(long clipId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = Command(connection, null, @"
            SELECT id, clip_id, created_at, trigger_name, preset, range_start, range_end, embedding_model, sidecar_models_json,
                utterance_count, status, error
            FROM analysis_runs WHERE clip_id = @clip ORDER BY id DESC;");
        cmd.Parameters.AddWithValue("@clip", clipId);

        var runs = new List<AnalysisRun>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            runs.Add(new AnalysisRun(
                r.GetInt64(0), r.GetInt64(1), ParseTime(r.GetString(2)), r.GetString(3),
                Enum.TryParse<AnalysisPreset>(r.GetString(4), ignoreCase: true, out var preset) ? preset : AnalysisPreset.Standard,
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.GetString(7),
                ParseModels(r.GetString(8)), r.GetInt32(9), (ClipAnalysisStatus)r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11)));
        }
        return runs;
    }

    public Task<IReadOnlyList<SpeakerSummary>> GetSpeakersAsync(double sensitivity, CancellationToken ct = default) =>
        QuerySpeakersAsync(null, sensitivity, ct);

    public async Task<SpeakerSummary?> GetSpeakerAsync(long speakerId, double sensitivity, CancellationToken ct = default) =>
        (await QuerySpeakersAsync(speakerId, sensitivity, ct)).FirstOrDefault();

    private async Task<IReadOnlyList<SpeakerSummary>> QuerySpeakersAsync(long? speakerId, double sensitivity, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = Command(connection, null, $@"
            SELECT s.id, {SpeakerNameSql}, s.custom_name, s.linked_profile_name, s.notes,
                COUNT(DISTINCT a.clip_id), COALESCE(SUM(a.talk_time_seconds), 0), COALESCE(SUM(a.utterance_count), 0),
                (SELECT COUNT(*) FROM utterances u JOIN appearances a2 ON a2.id = u.appearance_id
                    WHERE a2.speaker_id = s.id AND {IncidentSql}) AS incidents,
                MIN(COALESCE(c.recorded_at, c.scanned_at)), MAX(COALESCE(c.recorded_at, c.scanned_at))
            FROM speakers s
            LEFT JOIN appearances a ON a.speaker_id = s.id
            LEFT JOIN clips c ON c.id = a.clip_id
            WHERE (@speaker IS NULL OR s.id = @speaker)
            GROUP BY s.id
            ORDER BY incidents DESC, 7 DESC, s.id;");
        cmd.Parameters.AddWithValue("@sensitivity", sensitivity);
        cmd.Parameters.AddWithValue("@speaker", (object?)speakerId ?? DBNull.Value);

        var speakers = new List<SpeakerSummary>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            speakers.Add(new SpeakerSummary(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.GetInt32(5), r.GetDouble(6), r.GetInt32(7), r.GetInt32(8),
                r.IsDBNull(9) ? null : ParseTime(r.GetString(9)), r.IsDBNull(10) ? null : ParseTime(r.GetString(10))));
        }
        return speakers;
    }

    public async Task<IReadOnlyList<SpeakerAppearance>> GetAppearancesAsync(
        long? clipId, long? speakerId, double sensitivity, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = Command(connection, null, $@"
            SELECT a.id, a.clip_id, c.file_path, c.file_name, c.duration_seconds, a.speaker_id, {SpeakerNameSql}, a.local_label,
                a.talk_time_seconds, a.utterance_count,
                (SELECT COUNT(*) FROM utterances u WHERE u.appearance_id = a.id AND {IncidentSql}),
                a.first_start, a.last_end, a.match_score, a.assigned_manually, a.carried_over, c.recorded_at
            FROM appearances a
            JOIN clips c ON c.id = a.clip_id
            JOIN speakers s ON s.id = a.speaker_id
            WHERE (@clip IS NULL OR a.clip_id = @clip) AND (@speaker IS NULL OR a.speaker_id = @speaker)
            ORDER BY COALESCE(c.recorded_at, c.scanned_at) DESC, a.talk_time_seconds DESC;");
        cmd.Parameters.AddWithValue("@sensitivity", sensitivity);
        cmd.Parameters.AddWithValue("@clip", (object?)clipId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@speaker", (object?)speakerId ?? DBNull.Value);

        var list = new List<SpeakerAppearance>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SpeakerAppearance(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetInt64(5), r.GetString(6),
                r.GetString(7), r.GetDouble(8), r.GetInt32(9), r.GetInt32(10), r.GetDouble(11), r.GetDouble(12),
                r.IsDBNull(13) ? null : r.GetDouble(13), r.GetInt32(14) == 1, r.GetInt32(15) == 1,
                r.IsDBNull(16) ? null : ParseTime(r.GetString(16))));
        }
        return list;
    }

    /// <param name="incidentSensitivity">When set, only incidents at this sensitivity are returned.</param>
    public async Task<IReadOnlyList<UtteranceRecord>> GetUtterancesAsync(
        long? clipId = null, long? speakerId = null, double? incidentSensitivity = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = Command(connection, null, $@"
            SELECT u.id, u.clip_id, c.file_path, c.file_name, c.duration_seconds, a.speaker_id, {SpeakerNameSql},
                u.start_seconds, u.end_seconds, u.transcript, u.scores_json, u.word_hits_json, u.max_score, u.status, u.note,
                c.recorded_at
            FROM utterances u
            JOIN appearances a ON a.id = u.appearance_id
            JOIN speakers s ON s.id = a.speaker_id
            JOIN clips c ON c.id = u.clip_id
            WHERE (@clip IS NULL OR u.clip_id = @clip) AND (@speaker IS NULL OR a.speaker_id = @speaker)
                AND (@sensitivity IS NULL OR {IncidentSql})
            ORDER BY COALESCE(c.recorded_at, c.scanned_at) DESC, c.file_name, u.start_seconds;");
        cmd.Parameters.AddWithValue("@clip", (object?)clipId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@speaker", (object?)speakerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sensitivity", (object?)incidentSensitivity ?? DBNull.Value);

        var list = new List<UtteranceRecord>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new UtteranceRecord(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetInt64(5), r.GetString(6),
                r.GetDouble(7), r.GetDouble(8), r.GetString(9),
                JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(10)) ?? new Dictionary<string, double>(),
                JsonSerializer.Deserialize<List<WordListEntry>>(r.GetString(11)) ?? new List<WordListEntry>(),
                r.GetDouble(12), (IncidentStatus)r.GetInt32(13), r.GetString(14),
                r.IsDBNull(15) ? null : ParseTime(r.GetString(15))));
        }
        return list;
    }

    #endregion

    #region Edits

    public async Task SetIncidentReviewAsync(long utteranceId, IncidentStatus status, string note, CancellationToken ct = default)
    {
        await ExecuteAsync("UPDATE utterances SET status = @status, note = @note WHERE id = @id;", ct,
            ("@status", (int)status), ("@note", note), ("@id", utteranceId));
    }

    /// <summary>Sets the speaker's name; null or blank falls back to the matched profile name or "Speaker N".</summary>
    public async Task RenameSpeakerAsync(long speakerId, string? name, CancellationToken ct = default)
    {
        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await ExecuteAsync("UPDATE speakers SET custom_name = @name WHERE id = @id;", ct,
            ("@name", (object?)trimmed ?? DBNull.Value), ("@id", speakerId));
    }

    public async Task SetSpeakerNotesAsync(long speakerId, string notes, CancellationToken ct = default)
    {
        await ExecuteAsync("UPDATE speakers SET notes = @notes WHERE id = @id;", ct, ("@notes", notes), ("@id", speakerId));
    }

    /// <summary>
    /// Moves an appearance (and its utterances) to another speaker, or to a new speaker when <paramref name="targetSpeakerId"/>
    /// is null. Returns the speaker it now belongs to. The assignment survives rescans of the clip.
    /// </summary>
    public async Task<long> ReassignAppearanceAsync(long appearanceId, long? targetSpeakerId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        string modelVersion;
        using (var find = Command(connection, tx,
            "SELECT s.model_version FROM appearances a JOIN speakers s ON s.id = a.speaker_id WHERE a.id = @id;"))
        {
            find.Parameters.AddWithValue("@id", appearanceId);
            modelVersion = (string?)await find.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException($"Appearance {appearanceId} does not exist.");
        }

        long target = targetSpeakerId ?? await InsertSpeakerAsync(connection, tx, modelVersion, ct);
        using (var move = Command(connection, tx,
            "UPDATE appearances SET speaker_id = @speaker, assigned_manually = 1, match_score = NULL WHERE id = @id;"))
        {
            move.Parameters.AddWithValue("@speaker", target);
            move.Parameters.AddWithValue("@id", appearanceId);
            await move.ExecuteNonQueryAsync(ct);
        }

        await DeleteOrphanSpeakersAsync(connection, tx, ct);
        await tx.CommitAsync(ct);
        return target;
    }

    #endregion

    #region Word list

    public async Task<IReadOnlyList<WordListEntry>> GetWordListAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        return await ReadWordListAsync(connection, null, ct);
    }

    /// <summary>Adds a phrase (or updates its category) and re-checks every stored transcript.</summary>
    public async Task AddWordAsync(string phrase, string category, CancellationToken ct = default)
    {
        string trimmed = phrase.Trim();
        if (WordListMatcher.Tokenize(trimmed.TrimEnd('*')).Length == 0)
        {
            throw new ArgumentException("The phrase must contain at least one letter or digit.", nameof(phrase));
        }
        string cat = string.IsNullOrWhiteSpace(category) ? "word_list" : category.Trim();

        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        using (var cmd = Command(connection, tx, @"
            INSERT INTO word_list (phrase, category, created_at) VALUES (@phrase, @category, @created)
            ON CONFLICT (phrase) DO UPDATE SET category = excluded.category;"))
        {
            cmd.Parameters.AddWithValue("@phrase", trimmed);
            cmd.Parameters.AddWithValue("@category", cat);
            cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await RecheckWordHitsAsync(connection, tx, ct);
        await tx.CommitAsync(ct);
    }

    public async Task RemoveWordAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        using (var cmd = Command(connection, tx, "DELETE FROM word_list WHERE id = @id;"))
        {
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await RecheckWordHitsAsync(connection, tx, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task RecheckWordHitsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        var words = await ReadWordListAsync(connection, tx, ct);
        var rows = new List<(long Id, string Text)>();
        using (var read = Command(connection, tx, "SELECT id, transcript FROM utterances;"))
        using (var r = await read.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) rows.Add((r.GetInt64(0), r.GetString(1)));
        }

        using var update = Command(connection, tx, "UPDATE utterances SET word_hits_json = @hits, word_hit_count = @count WHERE id = @id;");
        var hitsParam = update.Parameters.Add("@hits", SqliteType.Text);
        var countParam = update.Parameters.Add("@count", SqliteType.Integer);
        var idParam = update.Parameters.Add("@id", SqliteType.Integer);
        foreach (var (id, text) in rows)
        {
            var hits = WordListMatcher.FindHits(text, words);
            hitsParam.Value = JsonSerializer.Serialize(hits);
            countParam.Value = hits.Count;
            idParam.Value = id;
            await update.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<WordListEntry>> ReadWordListAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken ct)
    {
        var list = new List<WordListEntry>();
        using var cmd = Command(connection, tx, "SELECT id, phrase, category FROM word_list ORDER BY phrase COLLATE NOCASE;");
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new WordListEntry(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    #endregion

    #region Helpers

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await EnsureInitializedAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = Command(connection, null, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> InsertSpeakerAsync(SqliteConnection connection, SqliteTransaction tx, string modelVersion, CancellationToken ct)
    {
        using var cmd = Command(connection, tx,
            "INSERT INTO speakers (model_version, created_at) VALUES (@model, @created) RETURNING id;");
        cmd.Parameters.AddWithValue("@model", modelVersion);
        cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Every speaker, with its voice as the sum of its appearance vectors from clips analysed with
    /// <paramref name="modelVersion"/>; vectors of different models are never mixed or compared.
    /// </summary>
    private static async Task<Dictionary<long, SpeakerVoice>> ReadSpeakerVoicesAsync(
        SqliteConnection connection, SqliteTransaction tx, string modelVersion, CancellationToken ct)
    {
        var voices = new Dictionary<long, SpeakerVoice>();
        using (var cmd = Command(connection, tx, "SELECT id, custom_name, linked_profile_name FROM speakers;"))
        {
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                voices[r.GetInt64(0)] = new SpeakerVoice(null, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
        }

        using (var cmd = Command(connection, tx, @"
            SELECT a.speaker_id, a.vector_sum FROM appearances a JOIN clips c ON c.id = a.clip_id
            WHERE c.embedding_model = @model AND a.vector_sum IS NOT NULL;"))
        {
            cmd.Parameters.AddWithValue("@model", modelVersion);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long id = r.GetInt64(0);
                float[] sum = FromBytes((byte[])r.GetValue(1));
                var voice = voices[id];
                if (voice.Sum == null || voice.Sum.Length == sum.Length) voices[id] = voice with { Sum = Add(voice.Sum, sum) };
            }
        }
        return voices;
    }

    /// <summary>Removes speakers left with no appearances, unless the user has named them or written notes.</summary>
    private static async Task DeleteOrphanSpeakersAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = Command(connection, tx, @"
            DELETE FROM speakers WHERE custom_name IS NULL AND notes = ''
                AND NOT EXISTS (SELECT 1 FROM appearances a WHERE a.speaker_id = speakers.id);");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, string> ParseModels(string? json) =>
        string.IsNullOrEmpty(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static float[] Add(float[]? a, float[] b)
    {
        var sum = new float[b.Length];
        for (int i = 0; i < b.Length; i++) sum[i] = (a?[i] ?? 0f) + b[i];
        return sum;
    }

    private static float[]? Normalize(float[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm < 1e-12) return null;
        return v.Select(x => (float)(x / norm)).ToArray();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na < 1e-12 || nb < 1e-12 ? 0.0 : dot / Math.Sqrt(na * nb);
    }

    private static byte[] ToBytes(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    private static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    #endregion

    public void Dispose() => _initLock.Dispose();
}
