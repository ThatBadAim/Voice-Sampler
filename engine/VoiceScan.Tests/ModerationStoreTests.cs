namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VoiceScan.Core;
using VoiceScan.Core.Storage;
using Xunit;

public sealed class ModerationStoreTests : IDisposable
{
    private const string Model = "test-model@abc";
    private const double LinkThreshold = 0.48;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_moderation_" + Guid.NewGuid().ToString("N"));
    private readonly ModerationStore _store;

    public ModerationStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ModerationStore(Path.Combine(_dir, "moderation.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // Orthogonal "voices" with a little per-utterance variation; cosine between voices is ~0, within a voice ~1.
    private static float[] Voice(int axis, int variant = 0)
    {
        var v = new float[8];
        v[axis] = 1f;
        v[(axis + 1 + variant) % 8] += 0.05f;
        return v;
    }

    private static DetectedSegment Utterance(
        double start, string label, float[] voice, string text,
        Dictionary<string, double>? scores = null, params string[] violations) =>
        new()
        {
            StartTimeSeconds = start,
            EndTimeSeconds = start + 2.0,
            SpeakerLabel = label,
            Transcript = text,
            Embedding = voice,
            ModerationScores = scores,
            ModerationViolations = violations,
            IsOffensive = violations.Length > 0
        };

    private static FileScanResult Clip(string name, params DetectedSegment[] segments) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), name),
        FileHash = "sha256-" + name,
        DurationSeconds = 600,
        AnalyzerUsed = true,
        Segments = segments.ToList()
    };

    private Task Ingest(FileScanResult result, params KnownVoice[] known) =>
        _store.IngestAsync(result, new AnalysisRunInfo(Model), known, LinkThreshold);

    [Theory]
    [InlineData("You absolute IDIOT!", "idiot", true)]
    [InlineData("what a classic move", "ass", false)]
    [InlineData("go uninstall the game", "uninstall the game", true)]
    [InlineData("stop being such idiots", "idiot*", true)]
    [InlineData("stop being such idiots", "idiot", false)]
    [InlineData("you're trash", "you're trash", true)]
    public void WordListMatcher_MatchesWholeWordsAndPhrases(string transcript, string phrase, bool expected)
    {
        var hits = WordListMatcher.FindHits(transcript, [new WordListEntry(1, phrase, "insult")]);
        Assert.Equal(expected, hits.Count == 1);
    }

    [Fact]
    public async Task Ingest_StoresClipSpeakersUtterancesAndClassifierScores()
    {
        await Ingest(Clip("match1.mkv",
            Utterance(10, "SPEAKER_00", Voice(0), "nice shot", new() { ["insult"] = 0.02 }),
            Utterance(20, "SPEAKER_01", Voice(2), "you are garbage", new() { ["insult"] = 0.91, ["toxicity"] = 0.95 }, "insult", "toxicity"),
            Utterance(30, "SPEAKER_01", Voice(2), "I will find you", null, "threat")));

        var clip = Assert.Single(await _store.GetClipsAsync(0.5));
        Assert.Equal(ClipAnalysisStatus.Analysed, clip.Status);
        Assert.Equal(2, clip.SpeakerCount);
        Assert.Equal(3, clip.UtteranceCount);
        Assert.Equal(2, clip.IncidentCount);

        var incidents = await _store.GetUtterancesAsync(incidentSensitivity: 0.5);
        Assert.Equal(2, incidents.Count);
        var threat = incidents.Single(u => u.Transcript == "I will find you");
        Assert.Equal(1.0, threat.ClassifierScores["threat"]); // sidecar regex fallback: label without a score
        Assert.Equal(["threat"], threat.Categories(0.5));
        Assert.Equal(["toxicity", "insult"], incidents.Single(u => u.Transcript == "you are garbage").Categories(0.5));
        Assert.Single(incidents.Select(u => u.SpeakerId).Distinct());
    }

    [Fact]
    public async Task Ingest_LinksSameVoiceAcrossClipsAndSeparatesDifferentVoices()
    {
        await Ingest(Clip("a.mkv",
            Utterance(0, "SPEAKER_00", Voice(0), "hello"),
            Utterance(5, "SPEAKER_01", Voice(3), "hi")));
        await Ingest(Clip("b.mkv",
            Utterance(0, "SPEAKER_00", Voice(3, 1), "again"),
            Utterance(5, "SPEAKER_01", Voice(5), "new person")));

        var speakers = await _store.GetSpeakersAsync(0.5);
        Assert.Equal(3, speakers.Count);
        var shared = Assert.Single(speakers, s => s.ClipCount == 2);

        var appearances = await _store.GetAppearancesAsync(null, shared.Id, 0.5);
        Assert.Equal(2, appearances.Count);
        var linked = appearances.Single(a => a.FileName == "b.mkv");
        Assert.NotNull(linked.MatchScore);
        Assert.True(linked.MatchScore >= LinkThreshold);
        Assert.Null(appearances.Single(a => a.FileName == "a.mkv").MatchScore);
    }

    [Fact]
    public async Task Ingest_NeverGivesTwoLabelsInOneClipTheSameSpeaker()
    {
        await Ingest(Clip("a.mkv", Utterance(0, "SPEAKER_00", Voice(0), "one")));
        // Diarization split one voice into two labels: only one of them may join the existing speaker.
        await Ingest(Clip("b.mkv",
            Utterance(0, "SPEAKER_00", Voice(0), "two"),
            Utterance(5, "SPEAKER_01", Voice(0, 2), "three")));

        var appearances = await _store.GetAppearancesAsync(null, null, 0.5);
        var inB = appearances.Where(a => a.FileName == "b.mkv").ToList();
        Assert.Equal(2, inB.Count);
        Assert.NotEqual(inB[0].SpeakerId, inB[1].SpeakerId);
    }

    [Fact]
    public async Task Ingest_NamesSpeakerAfterMatchingEnrolledVoice()
    {
        await Ingest(Clip("a.mkv", Utterance(0, "SPEAKER_00", Voice(4), "hey")), new KnownVoice("Alex", Voice(4, 3)));

        var speaker = Assert.Single(await _store.GetSpeakersAsync(0.5));
        Assert.Equal("Alex", speaker.DisplayName);
        Assert.Equal("Alex", speaker.LinkedProfileName);
    }

    [Fact]
    public async Task Reingest_ReplacesDataButKeepsReviewStatusAndManualAssignment()
    {
        var clip = Clip("a.mkv",
            Utterance(0, "SPEAKER_00", Voice(0), "you are garbage", new() { ["insult"] = 0.9 }),
            Utterance(5, "SPEAKER_01", Voice(2), "calm down"));
        await Ingest(clip);

        var incident = Assert.Single(await _store.GetUtterancesAsync(incidentSensitivity: 0.5));
        await _store.SetIncidentReviewAsync(incident.Id, IncidentStatus.Confirmed, "warned in chat");

        var calm = (await _store.GetAppearancesAsync(null, null, 0.5)).Single(a => a.LocalLabel == "SPEAKER_01");
        long movedTo = await _store.ReassignAppearanceAsync(calm.Id, null);
        await _store.RenameSpeakerAsync(movedTo, "Jordan");

        await Ingest(clip);

        var again = Assert.Single(await _store.GetUtterancesAsync(incidentSensitivity: 0.5));
        Assert.Equal(IncidentStatus.Confirmed, again.Status);
        Assert.Equal("warned in chat", again.Note);
        Assert.Equal(2, (await _store.GetUtterancesAsync()).Count);

        var calmAgain = (await _store.GetAppearancesAsync(null, null, 0.5)).Single(a => a.LocalLabel == "SPEAKER_01");
        Assert.Equal(movedTo, calmAgain.SpeakerId);
        Assert.Equal("Jordan", calmAgain.SpeakerName);
        Assert.True(calmAgain.AssignedManually);
    }

    [Fact]
    public async Task Ingest_WithoutSidecarIsNotAnalysedAndNeverOverwritesAnalysedData()
    {
        var unanalysed = Clip("b.mkv");
        unanalysed.AnalyzerUsed = false;
        await Ingest(unanalysed);
        Assert.Equal(ClipAnalysisStatus.NotAnalysed, Assert.Single(await _store.GetClipsAsync(0.5)).Status);

        var analysed = Clip("b.mkv", Utterance(0, "SPEAKER_00", Voice(0), "hello"));
        await Ingest(analysed);
        await Ingest(unanalysed);

        var clip = Assert.Single(await _store.GetClipsAsync(0.5));
        Assert.Equal(ClipAnalysisStatus.Analysed, clip.Status);
        Assert.Equal(1, clip.UtteranceCount);
    }

    [Fact]
    public async Task SensitivityAndWordList_ChangeIncidentsWithoutRescanning()
    {
        await Ingest(Clip("a.mkv",
            Utterance(0, "SPEAKER_00", Voice(0), "that was dumb", new() { ["insult"] = 0.4 }),
            Utterance(5, "SPEAKER_00", Voice(0), "go back to the lobby, noob", new() { ["insult"] = 0.1 })));

        Assert.Empty(await _store.GetUtterancesAsync(incidentSensitivity: 0.5));
        Assert.Single(await _store.GetUtterancesAsync(incidentSensitivity: 0.3));

        await _store.AddWordAsync("noob", "harassment");
        var hit = Assert.Single(await _store.GetUtterancesAsync(incidentSensitivity: 0.5));
        Assert.Equal(["harassment"], hit.Categories(0.5));

        var entry = Assert.Single(await _store.GetWordListAsync());
        await _store.RemoveWordAsync(entry.Id);
        Assert.Empty(await _store.GetUtterancesAsync(incidentSensitivity: 0.5));
    }

    [Fact]
    public async Task SpeakerEdits_PersistAcrossStoreInstances()
    {
        await Ingest(Clip("a.mkv", Utterance(0, "SPEAKER_00", Voice(0), "hello")));
        long id = Assert.Single(await _store.GetSpeakersAsync(0.5)).Id;
        await _store.RenameSpeakerAsync(id, "  Sam  ");
        await _store.SetSpeakerNotesAsync(id, "Team captain");

        using var reopened = new ModerationStore(Path.Combine(_dir, "moderation.db"));
        var speaker = await reopened.GetSpeakerAsync(id, 0.5);
        Assert.NotNull(speaker);
        Assert.Equal("Sam", speaker.DisplayName);
        Assert.Equal("Team captain", speaker.Notes);

        await reopened.RenameSpeakerAsync(id, " ");
        Assert.Equal($"Speaker {id}", (await reopened.GetSpeakerAsync(id, 0.5))!.DisplayName);
    }

    [Fact]
    public async Task AddWord_RejectsPhraseWithoutLetters()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AddWordAsync(" *!? ", "x"));
    }
}
