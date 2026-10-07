using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.Models;

public enum IncidentSortColumn
{
    Score,
    Clip,
    Time,
    Speaker,
    Category,
    Status
}

/// <summary>A transcribed line as shown in the Incidents, Clips and Speakers pages, evaluated at one sensitivity.</summary>
public sealed class UtteranceItem
{
    public UtteranceItem(UtteranceRecord record, double sensitivity)
    {
        Record = record;
        IsIncident = record.IsIncident(sensitivity);
        Categories = record.Categories(sensitivity);
        bool ai = record.MaxScore >= sensitivity;
        bool words = record.WordHits.Count > 0;
        Source = ai && words ? "AI + word list" : words ? "Word list" : ai ? "AI" : string.Empty;
    }

    public UtteranceRecord Record { get; }
    public long Id => Record.Id;
    public bool IsIncident { get; }
    public IReadOnlyList<string> Categories { get; }
    public string CategoriesText => string.Join(", ", Categories);
    public string Source { get; }
    public string ClipName => Record.FileName;
    public string SpeakerName => Record.SpeakerName;
    public string Transcript => Record.Transcript;
    public double Score => Record.MaxScore;
    /// <summary>Sort key for "most serious first": a word-list hit ranks with the strongest classifier score.</summary>
    public double Severity => Record.WordHits.Count > 0 ? Math.Max(1.0, Record.MaxScore) : Record.MaxScore;
    public string ScoreText => Record.MaxScore.ToString("0.00", CultureInfo.InvariantCulture);
    public string StartText => TimeFormat.Clock(Record.StartSeconds);
    public string RangeText => $"{TimeFormat.Clock(Record.StartSeconds)} – {TimeFormat.Clock(Record.EndSeconds)}";
    public string StatusText => Record.Status.ToString();
    public string Note => Record.Note;
    public string RecordedText => Record.RecordedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;
    public string WordHitsText => string.Join(", ", Record.WordHits.Select(h => $"\"{h.Phrase}\" ({h.Category})"));
    public string ScoresText => string.Join("   ", Record.ClassifierScores
        .OrderByDescending(kv => kv.Value)
        .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value:0.00}")));
}

public sealed class ClipItem
{
    public ClipItem(ClipSummary summary, bool isOutdated = false)
    {
        Summary = summary;
        IsOutdated = isOutdated;
    }

    /// <summary>The clip's last whole-clip analysis used other models than the active ones.</summary>
    public bool IsOutdated { get; }

    public ClipSummary Summary { get; }
    public long Id => Summary.Id;
    public string FileName => Summary.FileName;
    public string FilePath => Summary.FilePath;
    public string DurationText => TimeFormat.Clock(Summary.DurationSeconds);
    public int SpeakerCount => Summary.SpeakerCount;
    public int IncidentCount => Summary.IncidentCount;
    public bool HasIncidents => Summary.IncidentCount > 0;
    public string RecordedText => (Summary.RecordedAt ?? Summary.ScannedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string StatusText => Summary.Status switch
    {
        ClipAnalysisStatus.Analysed => "Analysed",
        ClipAnalysisStatus.NoSpeech => "No speech found",
        ClipAnalysisStatus.NotAnalysed => "Not analysed (sidecar was not running)",
        _ => "Error: " + Summary.ErrorMessage
    };
    public bool IsAnalysed => Summary.Status == ClipAnalysisStatus.Analysed;
}

public sealed class AppearanceItem
{
    public AppearanceItem(SpeakerAppearance appearance) => Appearance = appearance;

    public SpeakerAppearance Appearance { get; }
    public long Id => Appearance.Id;
    public long SpeakerId => Appearance.SpeakerId;
    public string SpeakerName => Appearance.SpeakerName;
    public string ClipName => Appearance.FileName;
    public string LocalLabel => Appearance.LocalLabel;
    public string TalkTimeText => TimeFormat.Clock(Appearance.TalkTimeSeconds);
    public int UtteranceCount => Appearance.UtteranceCount;
    public int IncidentCount => Appearance.IncidentCount;
    public bool HasIncidents => Appearance.IncidentCount > 0;
    public string SpanText => $"{TimeFormat.Clock(Appearance.FirstStartSeconds)} – {TimeFormat.Clock(Appearance.LastEndSeconds)}";
    public string RecordedText => Appearance.RecordedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>How the appearance was attached to its speaker, with the voice-match score when linked automatically.</summary>
    public string MatchText => Appearance.AssignedManually ? "Assigned by you"
        : Appearance.CarriedOver ? "Kept from previous analysis"
        : Appearance.MatchScore is double score ? string.Create(CultureInfo.InvariantCulture, $"Voice match {score:0.00}")
        : "First heard here";
}

public sealed class SpeakerItem
{
    public SpeakerItem(SpeakerSummary summary) => Summary = summary;

    public SpeakerSummary Summary { get; }
    public long Id => Summary.Id;
    public string DisplayName => Summary.DisplayName;
    public int ClipCount => Summary.ClipCount;
    public int IncidentCount => Summary.IncidentCount;
    public bool HasIncidents => Summary.IncidentCount > 0;
    public string TalkTimeText => TimeFormat.Clock(Summary.TalkTimeSeconds);
    public string LastSeenText => Summary.LastSeen?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? string.Empty;
    public string ProfileText => Summary.LinkedProfileName is { } name ? $"Matches enrolled voice \"{name}\"" : string.Empty;
}

/// <summary>One entry of a clip's analysis history.</summary>
public sealed class AnalysisRunItem
{
    public AnalysisRunItem(AnalysisRun run, string embeddingModelName)
    {
        Run = run;
        EmbeddingModelName = embeddingModelName;
    }

    public AnalysisRun Run { get; }
    public string EmbeddingModelName { get; }
    public string WhenText => Run.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string TitleText => $"{Run.Trigger} · {Run.Preset}" + (Run.RangeStart.HasValue || Run.RangeEnd.HasValue
        ? $" · section {TimeFormat.Clock(Run.RangeStart ?? 0)} – {(Run.RangeEnd is double end ? TimeFormat.Clock(end) : "end")}"
        : " · whole clip");
    public string ResultText => Run.Status switch
    {
        ClipAnalysisStatus.Analysed => $"{Run.UtteranceCount} line(s)",
        ClipAnalysisStatus.NoSpeech => "No speech found",
        ClipAnalysisStatus.NotAnalysed => "Not analysed (sidecar was not running)",
        _ => "Failed: " + Run.Error
    };
    public bool Failed => Run.Status is ClipAnalysisStatus.Error or ClipAnalysisStatus.NotAnalysed;
    public string ModelsText => ModelText.Describe(EmbeddingModelName, Run.SidecarModels);
}

public static class ModelText
{
    /// <summary>"Voice: X · asr: Y · moderation: Z", in pipeline order.</summary>
    public static string Describe(string embeddingModelName, IReadOnlyDictionary<string, string> sidecarModels)
    {
        var parts = new List<string> { "Voice: " + embeddingModelName };
        parts.AddRange(SidecarModelStages.All
            .Where(sidecarModels.ContainsKey)
            .Select(stage => $"{stage}: {sidecarModels[stage]}"));
        return string.Join(" · ", parts);
    }
}

public sealed class EmbeddingModelItem
{
    public EmbeddingModelItem(EmbeddingModelEntry entry, bool isActive)
    {
        Entry = entry;
        IsActive = isActive;
    }

    public EmbeddingModelEntry Entry { get; }
    public bool IsActive { get; }
    public string DisplayName => Entry.DisplayName;
    public bool IsImported => !Entry.IsBuiltIn;
    public string KindText => Entry.IsBuiltIn ? "Built-in" : "Imported";
    public string FrontEndText => FrontEndChoice.Describe(Entry.FrontEnd);
    public string EvaluationText => Entry.IsEvaluated
        ? string.Create(CultureInfo.InvariantCulture, $"Measured threshold {Entry.OperatingPoint.Threshold:0.00}")
        : string.Create(CultureInfo.InvariantCulture,
            $"Not evaluated: borrows threshold {Entry.OperatingPoint.Threshold:0.00} from the built-in model with this front-end");
    public string FilePath => Entry.FilePath;
}

/// <summary>A feature front-end an imported model can declare.</summary>
public sealed record FrontEndChoice(FeatureFrontEnd FrontEnd, string Name)
{
    public static IReadOnlyList<FrontEndChoice> All { get; } =
    [
        new(FeatureFrontEnd.SpeechBrainFbank, Describe(FeatureFrontEnd.SpeechBrainFbank)),
        new(FeatureFrontEnd.NemoMelSpectrogram, Describe(FeatureFrontEnd.NemoMelSpectrogram)),
    ];

    public static string Describe(FeatureFrontEnd frontEnd) => frontEnd switch
    {
        FeatureFrontEnd.SpeechBrainFbank => "ECAPA-style (SpeechBrain 80-band fbank)",
        _ => "TitaNet-style (NeMo 80-band mel)"
    };

    public override string ToString() => Name;
}

/// <summary>One sidecar stage on the Models page; <see cref="Value"/> is what Apply will load.</summary>
public sealed class SidecarStageItem : INotifyPropertyChanged
{
    private string _value;
    private string _active;
    private bool _loaded;
    private string? _error;

    public SidecarStageItem(string stage, string label, string hint)
    {
        Stage = stage;
        Label = label;
        Hint = hint;
        _value = string.Empty;
        _active = string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Stage { get; }
    public string Label { get; }
    public string Hint { get; }

    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value ?? string.Empty; OnPropertyChanged(); } }
    }

    public string StateText => _error != null ? "Failed: " + _error : _loaded ? "Loaded: " + _active : "Not loaded";
    public bool HasError => _error != null;

    public void Update(string active, bool loaded, string? error)
    {
        _active = active;
        _loaded = loaded;
        _error = error;
        Value = active;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasError));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A move target for an appearance: an existing speaker, or a new one when <see cref="SpeakerId"/> is null.</summary>
public sealed record SpeakerChoice(long? SpeakerId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Incident sensitivity shared by the moderation pages: a line is an incident when any classifier score reaches it
/// (or a word-list phrase matches). Persisted with the user's settings.
/// </summary>
public sealed class ModerationSettings : INotifyPropertyChanged
{
    public const double DefaultSensitivity = 0.5;
    public const double MinSensitivity = 0.05;
    public const double MaxSensitivity = 1.0;

    private readonly UserSettingsStore? _store;
    private double _sensitivity;

    public ModerationSettings(UserSettingsStore? store = null)
    {
        _store = store;
        _sensitivity = Math.Clamp(store?.Current.ModerationSensitivity ?? DefaultSensitivity, MinSensitivity, MaxSensitivity);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, MinSensitivity, MaxSensitivity), 2);
            if (clamped == _sensitivity) return;
            _sensitivity = clamped;
            if (_store != null)
            {
                _store.Current.ModerationSensitivity = clamped;
                _store.Save();
            }
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
