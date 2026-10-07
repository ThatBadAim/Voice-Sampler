namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;
using Xunit;

/// <summary>Re-analysed clips in the moderation store: replace, carry over by overlap, keep history (docs/SPEC-model-swap.md).</summary>
public sealed class ReanalysisStoreTests : IDisposable
{
    private const string ModelA = "model-a@v1";
    private const string ModelB = "model-b@v1";
    private const double LinkThreshold = 0.48;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_reanalysis_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly ModerationStore _store;

    public ReanalysisStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "moderation.db");
        _store = new ModerationStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static float[] Voice(int axis, int variant = 0)
    {
        var v = new float[8];
        v[axis] = 1f;
        v[(axis + 1 + variant) % 8] += 0.05f;
        return v;
    }

    private static DetectedSegment Line(double start, double end, string label, float[] voice, string text, double insult = 0.01) => new()
    {
        StartTimeSeconds = start,
        EndTimeSeconds = end,
        SpeakerLabel = label,
        Transcript = text,
        Embedding = voice,
        ModerationScores = new Dictionary<string, double> { ["insult"] = insult }
    };

    private static FileScanResult Result(string name, params DetectedSegment[] lines) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), name),
        FileHash = "sha256-" + name,
        DurationSeconds = 600,
        AnalyzerUsed = true,
        AnalyzerModels = new() { ["asr"] = "large-v3-turbo" },
        Segments = lines.ToList()
    };

    private static AnalysisRunInfo Reanalysis(string model, double? from = null, double? to = null) =>
        new(model, AnalysisRunInfo.ReanalysisTrigger, AnalysisPreset.Detailed, from, to);

    private async Task<long> ClipIdAsync() => Assert.Single(await _store.GetClipsAsync(0.5)).Id;

    [Fact]
    public async Task WholeClip_ReanalysisWithNewLabelsKeepsSpeakersManualAssignmentsAndReviews()
    {
        await _store.IngestAsync(Result("a.mkv",
            Line(0, 4, "SPEAKER_00", Voice(0), "hello there"),
            Line(10, 14, "SPEAKER_00", Voice(0), "nice shot"),
            Line(5, 9, "SPEAKER_01", Voice(3), "you are garbage", insult: 0.92),
            Line(15, 19, "SPEAKER_01", Voice(3), "uninstall")), new AnalysisRunInfo(ModelA), [], LinkThreshold);

        var before = await _store.GetAppearancesAsync(null, null, 0.5);
        long sam = before.Single(a => a.LocalLabel == "SPEAKER_00").SpeakerId;
        await _store.RenameSpeakerAsync(sam, "Sam");
        long jordan = await _store.ReassignAppearanceAsync(before.Single(a => a.LocalLabel == "SPEAKER_01").Id, null);
        await _store.RenameSpeakerAsync(jordan, "Jordan");
        var insult = Assert.Single(await _store.GetUtterancesAsync(incidentSensitivity: 0.5));
        await _store.SetIncidentReviewAsync(insult.Id, IncidentStatus.Confirmed, "muted");

        // A different model swaps the labels, shifts the timings and splits the insult in two.
        await _store.IngestAsync(Result("a.mkv",
            Line(0.3, 4.2, "SPEAKER_01", Voice(1), "hello there"),
            Line(10.2, 14.1, "SPEAKER_01", Voice(1), "nice shot"),
            Line(5.2, 6.0, "SPEAKER_00", Voice(4), "you are"),
            Line(6.0, 9.1, "SPEAKER_00", Voice(4), "garbage", insult: 0.95),
            Line(15.1, 19.0, "SPEAKER_00", Voice(4), "uninstall")), Reanalysis(ModelB), [], LinkThreshold);

        var after = await _store.GetAppearancesAsync(null, null, 0.5);
        var samNow = after.Single(a => a.LocalLabel == "SPEAKER_01");
        Assert.Equal(sam, samNow.SpeakerId);
        Assert.Equal("Sam", samNow.SpeakerName);
        Assert.True(samNow.CarriedOver);
        Assert.False(samNow.AssignedManually);
        var jordanNow = after.Single(a => a.LocalLabel == "SPEAKER_00");
        Assert.Equal(jordan, jordanNow.SpeakerId);
        Assert.True(jordanNow.AssignedManually);
        Assert.Equal(3, jordanNow.UtteranceCount);
        Assert.Equal(7.8, jordanNow.TalkTimeSeconds, 3);

        var lines = await _store.GetUtterancesAsync();
        Assert.Equal(5, lines.Count);
        var garbage = lines.Single(l => l.Transcript == "garbage");
        Assert.Equal(IncidentStatus.Confirmed, garbage.Status); // the larger overlap of the old line
        Assert.Equal("muted", garbage.Note);
        Assert.Equal(IncidentStatus.Unreviewed, lines.Single(l => l.Transcript == "you are").Status);

        var clip = Assert.Single(await _store.GetClipsAsync(0.5));
        Assert.Equal(ModelB, clip.EmbeddingModel);
        Assert.Equal("large-v3-turbo", clip.SidecarModels["asr"]);
        var runs = await _store.GetAnalysisRunsAsync(clip.Id);
        Assert.Equal([AnalysisRunInfo.ReanalysisTrigger, AnalysisRunInfo.ScanTrigger], runs.Select(r => r.Trigger));
        Assert.Equal(AnalysisPreset.Detailed, runs[0].Preset);
        Assert.Equal(5, runs[0].UtteranceCount);
        Assert.Equal(ModelB, runs[0].EmbeddingModel);
    }

    [Fact]
    public async Task Section_ReplacesOnlyLinesInRangeAndMergesIntoTheClipsSpeakers()
    {
        await _store.IngestAsync(Result("a.mkv",
            Line(0, 4, "SPEAKER_00", Voice(0), "first"),
            Line(10, 12, "SPEAKER_00", Voice(0), "mumble"),
            Line(20, 22, "SPEAKER_01", Voice(3), "something"),
            Line(30, 34, "SPEAKER_01", Voice(3), "last")), new AnalysisRunInfo(ModelA), [], LinkThreshold);
        long clipId = await ClipIdAsync();
        var before = await _store.GetAppearancesAsync(clipId, null, 0.5);
        var mumble = (await _store.GetUtterancesAsync()).Single(l => l.Transcript == "mumble");
        await _store.SetIncidentReviewAsync(mumble.Id, IncidentStatus.Dismissed, "just noise");

        // Labels in a section are local to it: SPEAKER_00 here is the clip's SPEAKER_01, and a new voice appears.
        await _store.IngestAsync(Result("a.mkv",
            Line(10.1, 12.2, "SPEAKER_01", Voice(0, 1), "you are a muppet", insult: 0.7),
            Line(16, 17, "SPEAKER_02", Voice(6), "who said that"),
            Line(20, 22.5, "SPEAKER_00", Voice(3, 2), "something stupid", insult: 0.6)), Reanalysis(ModelA, 8, 23), [], LinkThreshold, clipId);

        var lines = await _store.GetUtterancesAsync(clipId: clipId);
        Assert.Equal(["first", "you are a muppet", "who said that", "something stupid", "last"],
            lines.OrderBy(l => l.StartSeconds).Select(l => l.Transcript));
        Assert.Equal(IncidentStatus.Dismissed, lines.Single(l => l.Transcript == "you are a muppet").Status);

        var after = await _store.GetAppearancesAsync(clipId, null, 0.5);
        Assert.Equal(3, after.Count);
        var first = after.Single(a => a.LocalLabel == "SPEAKER_00");
        Assert.Equal(before.Single(a => a.LocalLabel == "SPEAKER_00").SpeakerId, first.SpeakerId);
        Assert.Equal(2, first.UtteranceCount);
        Assert.Equal(6.1, first.TalkTimeSeconds, 3);
        Assert.Equal(2, after.Single(a => a.LocalLabel == "SPEAKER_01").UtteranceCount);
        var newcomer = after.Single(a => a.LocalLabel.StartsWith("SPEAKER_02 (run "));
        Assert.Null(newcomer.MatchScore);

        var runs = await _store.GetAnalysisRunsAsync(clipId);
        Assert.Equal((8.0, 23.0), (runs[0].RangeStart!.Value, runs[0].RangeEnd!.Value));
        Assert.Equal(3, runs[0].UtteranceCount);
    }

    [Fact]
    public async Task Section_IsRefusedForAClipAnalysedWithAnotherModel()
    {
        await _store.IngestAsync(Result("a.mkv", Line(0, 4, "SPEAKER_00", Voice(0), "first")), new AnalysisRunInfo(ModelA), [], LinkThreshold);
        long clipId = await ClipIdAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _store.IngestAsync(
            Result("a.mkv", Line(1, 2, "SPEAKER_00", Voice(0), "replaced")), Reanalysis(ModelB, 0, 5), [], LinkThreshold, clipId));

        Assert.Contains("Re-analyse the whole clip", ex.Message);
        Assert.Equal("first", Assert.Single(await _store.GetUtterancesAsync()).Transcript);
        Assert.Single(await _store.GetAnalysisRunsAsync(clipId));
    }

    [Fact]
    public async Task FailedRuns_AreRecordedAndLeaveTheDataUnchanged()
    {
        await _store.IngestAsync(Result("a.mkv", Line(0, 4, "SPEAKER_00", Voice(0), "first")), new AnalysisRunInfo(ModelA), [], LinkThreshold);
        long clipId = await ClipIdAsync();

        await _store.RecordFailedRunAsync(clipId, Reanalysis(ModelA), "Connection refused (localhost:54321)");
        var notAnalysed = Result("a.mkv");
        notAnalysed.AnalyzerUsed = false;
        await _store.IngestAsync(notAnalysed, Reanalysis(ModelA, 0, 10), [], LinkThreshold, clipId);

        var runs = await _store.GetAnalysisRunsAsync(clipId);
        Assert.Equal([ClipAnalysisStatus.NotAnalysed, ClipAnalysisStatus.Error, ClipAnalysisStatus.Analysed], runs.Select(r => r.Status));
        Assert.Equal("Connection refused (localhost:54321)", runs[1].Error);
        Assert.Equal("first", Assert.Single(await _store.GetUtterancesAsync()).Transcript);
        Assert.Equal(ClipAnalysisStatus.Analysed, Assert.Single(await _store.GetClipsAsync(0.5)).Status);
    }

    [Fact]
    public async Task Linking_NeverComparesVoiceVectorsOfDifferentModels()
    {
        await _store.IngestAsync(Result("a.mkv", Line(0, 4, "SPEAKER_00", Voice(0), "model A")), new AnalysisRunInfo(ModelA), [], LinkThreshold);
        await _store.IngestAsync(Result("b.mkv", Line(0, 4, "SPEAKER_00", Voice(0), "model B")), new AnalysisRunInfo(ModelB), [], LinkThreshold);
        await _store.IngestAsync(Result("c.mkv", Line(0, 4, "SPEAKER_00", Voice(0, 1), "model A again")), new AnalysisRunInfo(ModelA), [], LinkThreshold);

        var appearances = await _store.GetAppearancesAsync(null, null, 0.5);
        long a = appearances.Single(x => x.FileName == "a.mkv").SpeakerId;
        Assert.NotEqual(a, appearances.Single(x => x.FileName == "b.mkv").SpeakerId);
        var c = appearances.Single(x => x.FileName == "c.mkv");
        Assert.Equal(a, c.SpeakerId);
        Assert.NotNull(c.MatchScore);
    }

    [Fact]
    public async Task Migration_AddsTheNewColumnsToADatabaseFromBeforeReanalysis()
    {
        string oldDb = Path.Combine(_dir, "old.db");
        using (var connection = new SqliteConnection($"Data Source={oldDb}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE clips (id INTEGER PRIMARY KEY AUTOINCREMENT, clip_key TEXT UNIQUE NOT NULL, file_path TEXT NOT NULL,
                    file_name TEXT NOT NULL, duration_seconds REAL NOT NULL, scanned_at TEXT NOT NULL, recorded_at TEXT,
                    status INTEGER NOT NULL, error TEXT);
                CREATE TABLE speakers (id INTEGER PRIMARY KEY AUTOINCREMENT, model_version TEXT NOT NULL, custom_name TEXT,
                    linked_profile_name TEXT, notes TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL);
                CREATE TABLE appearances (id INTEGER PRIMARY KEY AUTOINCREMENT, clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    speaker_id INTEGER NOT NULL REFERENCES speakers(id), local_label TEXT NOT NULL, vector_sum BLOB,
                    vector_count INTEGER NOT NULL, talk_time_seconds REAL NOT NULL, utterance_count INTEGER NOT NULL,
                    first_start REAL NOT NULL, last_end REAL NOT NULL, match_score REAL, assigned_manually INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (clip_id, local_label));
                CREATE TABLE utterances (id INTEGER PRIMARY KEY AUTOINCREMENT, clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    appearance_id INTEGER NOT NULL REFERENCES appearances(id) ON DELETE CASCADE, start_seconds REAL NOT NULL,
                    end_seconds REAL NOT NULL, transcript TEXT NOT NULL, scores_json TEXT NOT NULL, max_score REAL NOT NULL,
                    word_hits_json TEXT NOT NULL, word_hit_count INTEGER NOT NULL, status INTEGER NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT '');
                INSERT INTO clips VALUES (1, 'sha256-old', '/rec/old.mkv', 'old.mkv', 60, '2026-10-01T10:00:00Z', NULL, 0, NULL);
                INSERT INTO speakers VALUES (1, 'model-a@v1', 'Sam', NULL, '', '2026-10-01T10:00:00Z');
                INSERT INTO appearances VALUES (1, 1, 1, 'SPEAKER_00', NULL, 0, 2, 1, 0, 2, NULL, 0);
                INSERT INTO utterances VALUES (1, 1, 1, 0, 2, 'old line', '{}', 0, '[]', 0, 0, '');";
            cmd.ExecuteNonQuery();
        }

        using var store = new ModerationStore(oldDb);
        var clip = Assert.Single(await store.GetClipsAsync(0.5));
        Assert.Equal("model-a@v1", clip.EmbeddingModel);
        Assert.Empty(await store.GetAnalysisRunsAsync(clip.Id));
        Assert.False(Assert.Single(await store.GetAppearancesAsync(clip.Id, null, 0.5)).CarriedOver);

        await store.IngestAsync(Result("old.mkv", Line(0, 2.2, "SPEAKER_00", Voice(0), "new line")),
            Reanalysis("model-a@v1"), [], LinkThreshold, clip.Id);
        Assert.Equal("Sam", Assert.Single(await store.GetUtterancesAsync()).SpeakerName);
    }
}
