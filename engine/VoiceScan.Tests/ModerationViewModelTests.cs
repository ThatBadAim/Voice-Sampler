namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.Core;
using VoiceScan.Core.Storage;
using Xunit;

public sealed class ModerationViewModelTests : IDisposable
{
    private const string Model = "test-model@abc";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_moderation_vm_" + Guid.NewGuid().ToString("N"));
    private readonly ModerationStore _store;

    public ModerationViewModelTests()
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

    private sealed class RecordingOutput : IAudioOutput
    {
        public List<(string File, double Start, double? Duration)> Starts { get; } = [];
        public bool IsAvailable => true;

        public IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds)
        {
            Starts.Add((filePath, startSeconds, durationSeconds));
            return new Session();
        }

        private sealed class Session : IAudioOutputSession
        {
            public bool HasExited => false;
            public void Dispose() { }
        }
    }

    private static float[] Voice(int axis, int variant = 0)
    {
        var v = new float[8];
        v[axis] = 1f;
        v[(axis + 1 + variant) % 8] += 0.05f;
        return v;
    }

    private static DetectedSegment Line(double start, double length, string label, float[] voice, string text, double insult = 0.0) => new()
    {
        StartTimeSeconds = start,
        EndTimeSeconds = start + length,
        SpeakerLabel = label,
        Transcript = text,
        Embedding = voice,
        ModerationScores = new Dictionary<string, double> { ["insult"] = insult, ["threat"] = 0.01 }
    };

    /// <summary>Creates a real (empty) recording file so pages that check the file exists can play it.</summary>
    private async Task<string> IngestClipAsync(string name, params DetectedSegment[] lines)
    {
        string path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, [0]);
        await _store.IngestAsync(new FileScanResult
        {
            FilePath = path,
            FileHash = "sha256-" + name,
            DurationSeconds = 600,
            AnalyzerUsed = true,
            Segments = lines.ToList()
        }, new AnalysisRunInfo(Model), [], 0.48);
        return path;
    }

    private static ClipPlayer Player(RecordingOutput output) => new(new AudioPlaybackController(output));

    [Fact]
    public void ClipPlayer_PlaysLineWithContextAndStopsAtItsEnd()
    {
        var output = new RecordingOutput();
        using var player = Player(output);

        player.Play(7, "/rec/a.mkv", 600, 10, 12);
        player.Play(8, "/rec/a.mkv", 600, 598, 599.5);

        Assert.Equal(("/rec/a.mkv", 9.0, (double?)4.0), output.Starts[0]);
        Assert.Equal(597.0, output.Starts[1].Start);
        Assert.Equal(3.0, output.Starts[1].Duration!.Value, 6); // clamped to the end of the file
        Assert.Equal(8, player.PlayingId);
    }

    [Fact]
    public async Task Incidents_SortFilterAndReviewAdvancesToTheNextLine()
    {
        await IngestClipAsync("a.mkv",
            Line(10, 2, "SPEAKER_00", Voice(0), "you are garbage", insult: 0.95),
            Line(20, 2, "SPEAKER_01", Voice(3), "that was dumb", insult: 0.70),
            Line(30, 2, "SPEAKER_01", Voice(3), "worst player ever", insult: 0.60),
            Line(40, 2, "SPEAKER_00", Voice(0), "nice shot", insult: 0.01));

        var vm = new IncidentsViewModel(_store, new ModerationSettings(), Player(new RecordingOutput()));
        await vm.RefreshAsync();

        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(["you are garbage", "that was dumb", "worst player ever"], vm.Incidents.Select(i => i.Transcript));
        Assert.Contains("insult", vm.Categories);

        vm.SortDescending = false;
        Assert.Equal("worst player ever", vm.Incidents[0].Transcript);
        vm.SortDescending = true;

        string other = vm.Speakers.Single(s => s != IncidentsViewModel.AllSpeakers && s != vm.Incidents[0].SpeakerName);
        vm.SpeakerFilter = other;
        Assert.Equal(2, vm.ShownCount);
        vm.SpeakerFilter = IncidentsViewModel.AllSpeakers;

        vm.SearchText = "DUMB";
        Assert.Equal("that was dumb", Assert.Single(vm.Incidents).Transcript);
        vm.SearchText = string.Empty;

        vm.StatusFilter = nameof(IncidentStatus.Unreviewed);
        vm.SelectedIncident = vm.Incidents[0];
        vm.ReviewNote = "muted for a week";
        await vm.ReviewSelectedAsync(IncidentStatus.Confirmed);

        Assert.Equal(2, vm.ShownCount);
        Assert.Equal(2, vm.UnreviewedCount);
        Assert.Equal("that was dumb", vm.SelectedIncident?.Transcript);

        vm.StatusFilter = nameof(IncidentStatus.Confirmed);
        var confirmed = Assert.Single(vm.Incidents);
        Assert.Equal("muted for a week", confirmed.Note);
    }

    [Fact]
    public async Task Incidents_WordAddedFromThePageListsMatchingLines()
    {
        await IngestClipAsync("a.mkv", Line(0, 2, "SPEAKER_00", Voice(0), "get good, noob"));
        var vm = new IncidentsViewModel(_store, new ModerationSettings(), Player(new RecordingOutput()));
        await vm.RefreshAsync();
        Assert.True(vm.IsEmpty);

        vm.NewPhrase = "noob";
        vm.NewCategory = "harassment";
        await vm.AddWordAsync();

        var line = Assert.Single(vm.Incidents);
        Assert.Equal("Word list", line.Source);
        Assert.Equal("harassment", line.CategoriesText);
        Assert.Equal(string.Empty, vm.NewPhrase);

        await vm.RemoveWordAsync(Assert.Single(vm.WordList));
        Assert.Empty(vm.Incidents);
    }

    [Fact]
    public async Task Incidents_PlayUsesThePagesOwnPlayerAndOpensTheSpeaker()
    {
        string path = await IngestClipAsync("a.mkv", Line(100, 3, "SPEAKER_00", Voice(0), "you are garbage", insult: 0.9));
        var output = new RecordingOutput();
        var vm = new IncidentsViewModel(_store, new ModerationSettings(), Player(output));
        await vm.RefreshAsync();

        long? requested = null;
        vm.SpeakerRequested += id => requested = id;
        vm.SelectedIncident = vm.Incidents[0];
        vm.PlayCommand.Execute(null);
        vm.OpenSpeakerCommand.Execute(null);

        Assert.Equal((path, 99.0, (double?)5.0), Assert.Single(output.Starts));
        Assert.Equal(vm.SelectedIncident!.Record.SpeakerId, requested);
    }

    [Fact]
    public async Task Clips_SelectingAClipListsItsSpeakersAndTranscript()
    {
        await IngestClipAsync("a.mkv",
            Line(5, 2, "SPEAKER_00", Voice(0), "hello"),
            Line(1, 2, "SPEAKER_01", Voice(3), "you are garbage", insult: 0.9));
        await IngestClipAsync("b.mkv", Line(0, 2, "SPEAKER_00", Voice(5), "quiet one"));

        var model = new SwappableEmbeddingModel(new FakeEmbeddingModel("test-model", Model));
        var sidecar = new FakeSidecar();
        var models = new ModelManager(new EmbeddingModelCatalog(Path.Combine(_dir, "models"), () => []), model,
            e => new FakeEmbeddingModel(e.ModelId), sidecar);
        using var reanalysis = new ReanalysisService(new ClipAnalyzer(model, sidecar), _store, Path.Combine(_dir, "profiles"));
        var vm = new ClipsViewModel(_store, new ModerationSettings(), Player(new RecordingOutput()), reanalysis, models);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Clips.Count);

        vm.OnlyWithIncidents = true;
        var clip = Assert.Single(vm.Clips);
        Assert.Equal("a.mkv", clip.FileName);

        vm.SelectedClip = clip;
        await vm.LoadSelectedClipAsync();
        Assert.Equal(2, vm.ClipSpeakers.Count);
        Assert.Equal(["you are garbage", "hello"], vm.ClipTranscript.Select(t => t.Transcript));
        Assert.True(vm.ClipTranscript[0].IsIncident);

        long? requested = null;
        vm.SpeakerRequested += id => requested = id;
        vm.OpenSpeakerCommand.Execute(vm.ClipSpeakers[0]);
        Assert.Equal(vm.ClipSpeakers[0].SpeakerId, requested);
    }

    [Fact]
    public async Task Speakers_PageShowsEverythingKnownAndSavesEdits()
    {
        await IngestClipAsync("a.mkv",
            Line(0, 1.5, "SPEAKER_00", Voice(0), "short"),
            Line(10, 4.0, "SPEAKER_00", Voice(0), "this is my longest line", insult: 0.0),
            Line(20, 2.0, "SPEAKER_00", Voice(0), "you are garbage", insult: 0.9));
        await IngestClipAsync("b.mkv", Line(0, 2, "SPEAKER_00", Voice(0, 1), "back again"));

        var vm = new SpeakersViewModel(_store, new ModerationSettings(), Player(new RecordingOutput()));
        await vm.RefreshAsync();
        long id = Assert.Single(vm.Speakers).Id;

        await vm.ShowSpeakerAsync(id);
        Assert.True(vm.HasSelection);
        Assert.Equal(2, vm.Appearances.Count);
        Assert.Equal("you are garbage", Assert.Single(vm.Incidents).Transcript);
        Assert.Equal(4, vm.Transcript.Count);
        Assert.Equal("this is my longest line", vm.VoiceSample?.Transcript);
        Assert.Equal(2, vm.Detail!.ClipCount);

        vm.EditName = "Sam";
        await vm.SaveNameAsync();
        vm.Notes = "Plays support";
        await vm.SaveNotesAsync();

        Assert.Equal("Sam", vm.Detail!.DisplayName);
        Assert.Equal("Sam", Assert.Single(vm.Speakers).DisplayName);
        Assert.Equal("Plays support", (await _store.GetSpeakerAsync(id, 0.5))!.Notes);
    }

    [Fact]
    public async Task Speakers_MovingAWronglyLinkedClipCreatesANewSpeaker()
    {
        await IngestClipAsync("a.mkv", Line(0, 2, "SPEAKER_00", Voice(0), "first"));
        await IngestClipAsync("b.mkv", Line(0, 2, "SPEAKER_00", Voice(0, 1), "second"));

        var vm = new SpeakersViewModel(_store, new ModerationSettings(), Player(new RecordingOutput()));
        await vm.RefreshAsync();
        await vm.ShowSpeakerAsync(Assert.Single(vm.Speakers).Id);
        long originalId = vm.Detail!.Id;

        vm.SelectedAppearance = vm.Appearances.Single(a => a.ClipName == "b.mkv");
        vm.MoveTarget = vm.MoveTargets.Single(t => t.SpeakerId == null);
        await vm.MoveSelectedAppearanceAsync();

        Assert.Equal(2, vm.Speakers.Count);
        Assert.Equal(originalId, vm.Detail!.Id);
        Assert.Equal("a.mkv", Assert.Single(vm.Appearances).ClipName);
        Assert.Contains(vm.MoveTargets, t => t.SpeakerId != null && t.SpeakerId != originalId);
    }

    [Fact]
    public void ModerationSettings_ClampsAndPersistsSensitivity()
    {
        string path = Path.Combine(_dir, "settings.json");
        var settings = new ModerationSettings(new UserSettingsStore(path)) { Sensitivity = 0.3 };
        Assert.Equal(0.3, new ModerationSettings(new UserSettingsStore(path)).Sensitivity);

        settings.Sensitivity = 7;
        Assert.Equal(ModerationSettings.MaxSensitivity, settings.Sensitivity);
        settings.Sensitivity = -1;
        Assert.Equal(ModerationSettings.MinSensitivity, settings.Sensitivity);
    }
}
