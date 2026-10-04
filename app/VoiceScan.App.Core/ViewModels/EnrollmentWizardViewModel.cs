using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;

namespace VoiceScan.App.Core.ViewModels;

public sealed class EnrollmentWizardViewModel : INotifyPropertyChanged
{
    private readonly IAudioQualityAnalyzer _qualityAnalyzer;
    private readonly ProfileEnrollmentService _enrollmentService;
    private readonly IWaveformService _waveformService;

    private EnrollmentStep _currentStep = EnrollmentStep.AudioSelection;
    private string _profileName = string.Empty;
    private bool _hasConsent;
    private bool _isAnalyzing;
    private bool _isEnrolling;
    private string? _statusMessage;
    private VoiceProfileSummary? _createdProfile;
    private VoiceProfileSummary? _selectedProfile;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a profile has been written to disk.</summary>
    public event Action<VoiceProfileSummary>? ProfileCreated;

    /// <summary>Raised after a saved voice was deleted.</summary>
    public event Action? ProfilesChanged;

    public ObservableCollection<VoiceProfileSummary> Profiles { get; } = [];
    public ObservableCollection<EnrollmentSampleItem> SampleFiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public VoiceProfileSummary? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(CanDeleteProfile));
            }
        }
    }

    public bool CanDeleteProfile => _selectedProfile is not null;

    public EnrollmentStep CurrentStep
    {
        get => _currentStep;
        private set => SetField(ref _currentStep, value);
    }

    /// <summary>Single-sample convenience: the first sample's path; setting it replaces all samples (no quality check is run).</summary>
    public string? SelectedAudioPath
    {
        get => SampleFiles.FirstOrDefault()?.Path;
        set
        {
            SampleFiles.Clear();
            if (!string.IsNullOrWhiteSpace(value)) SampleFiles.Add(new EnrollmentSampleItem(value));
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetField(ref _profileName, value))
            {
                OnPropertyChanged(nameof(CanCreateProfile));
            }
        }
    }

    public bool HasConsent
    {
        get => _hasConsent;
        set
        {
            if (SetField(ref _hasConsent, value))
            {
                OnPropertyChanged(nameof(CanProceedFromConsent));
                OnPropertyChanged(nameof(CanCreateProfile));
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set => SetField(ref _isAnalyzing, value);
    }

    public bool IsEnrolling
    {
        get => _isEnrolling;
        private set
        {
            if (SetField(ref _isEnrolling, value)) OnPropertyChanged(nameof(CanCreateProfile));
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public VoiceProfileSummary? CreatedProfile
    {
        get => _createdProfile;
        private set => SetField(ref _createdProfile, value);
    }

    public int AcceptedSampleCount => SampleFiles.Count(f => f.IsAccepted);
    public bool HasSamples => SampleFiles.Count > 0;

    public bool CanProceedFromAudioSelection => SampleFiles.Any(f => File.Exists(f.Path));
    public bool CanProceedFromConsent => _hasConsent;
    public bool CanProceedFromQuality => AcceptedSampleCount > 0;
    public bool CanCreateProfile => !string.IsNullOrWhiteSpace(_profileName) && CanProceedFromConsent && CanProceedFromQuality && !_isEnrolling;

    public EnrollmentWizardViewModel(IAudioQualityAnalyzer qualityAnalyzer, ProfileEnrollmentService enrollmentService, IWaveformService? waveformService = null)
    {
        _qualityAnalyzer = qualityAnalyzer;
        _enrollmentService = enrollmentService;
        _waveformService = waveformService ?? new WaveformService();
        SampleFiles.CollectionChanged += OnSamplesChanged;
        RefreshProfiles();
    }

    public void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in ProfileLibrary.List())
        {
            Profiles.Add(profile);
        }
        OnPropertyChanged(nameof(HasProfiles));
        SelectedProfile = null;
    }

    /// <summary>Deletes the selected voice from disk.</summary>
    public void DeleteSelectedProfile()
    {
        if (_selectedProfile is not { } profile) return;

        try
        {
            ProfileLibrary.Delete(profile.Path);
            StatusMessage = $"Deleted voice '{profile.Name}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not delete '{profile.Name}': {ex.Message}";
            return;
        }

        RefreshProfiles();
        ProfilesChanged?.Invoke();
    }

    public void MoveToStep(EnrollmentStep step)
    {
        // Enforce guard conditions
        if (step > EnrollmentStep.AudioSelection && !CanProceedFromAudioSelection)
        {
            StatusMessage = "Please select or record reference audio first.";
            return;
        }

        if (step > EnrollmentStep.ConsentVerification && !CanProceedFromConsent)
        {
            StatusMessage = "Mandatory consent is required before proceeding.";
            return;
        }

        CurrentStep = step;
        StatusMessage = null;
    }

    private void OnSamplesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.NewItems?.OfType<EnrollmentSampleItem>() ?? [])
        {
            item.PropertyChanged += OnSampleChanged;
        }

        foreach (var item in e.OldItems?.OfType<EnrollmentSampleItem>() ?? [])
        {
            item.PropertyChanged -= OnSampleChanged;
        }

        if (string.IsNullOrWhiteSpace(_profileName) && SampleFiles.Count > 0)
        {
            ProfileName = Path.GetFileNameWithoutExtension(SampleFiles[0].Path);
        }

        NotifySamplesChanged();
    }

    private void OnSampleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnrollmentSampleItem.IsAccepted)) NotifySamplesChanged();
    }

    private void NotifySamplesChanged()
    {
        OnPropertyChanged(nameof(SelectedAudioPath));
        OnPropertyChanged(nameof(HasSamples));
        OnPropertyChanged(nameof(AcceptedSampleCount));
        OnPropertyChanged(nameof(CanProceedFromAudioSelection));
        OnPropertyChanged(nameof(CanProceedFromQuality));
        OnPropertyChanged(nameof(CanCreateProfile));
    }

    /// <summary>Adds files and folders as voice samples (bulk) and quality-checks each new one.</summary>
    public async Task AddSamplesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var existing = new HashSet<string>(SampleFiles.Select(f => f.Path));
        var added = new List<EnrollmentSampleItem>();
        foreach (var file in MediaFileCollector.Collect(paths))
        {
            if (!existing.Add(file)) continue;
            var item = new EnrollmentSampleItem(file);
            SampleFiles.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            StatusMessage = "No new audio or video files found in that selection.";
            return;
        }

        await RunQualityCheckAsync(added, cancellationToken);
    }

    public void RemoveSample(EnrollmentSampleItem sample)
    {
        if (!_isEnrolling) SampleFiles.Remove(sample);
    }

    public void ClearSamples()
    {
        if (!_isEnrolling) SampleFiles.Clear();
    }

    public Task ToggleWaveformAsync(MediaFileItem sample) => WaveformPreview.ToggleAsync(sample, _waveformService);

    /// <summary>Checks every sample that has not been checked yet.</summary>
    public Task RunQualityCheckAsync(CancellationToken cancellationToken = default) =>
        RunQualityCheckAsync(SampleFiles.Where(f => f.Report is null).ToList(), cancellationToken);

    private async Task RunQualityCheckAsync(IReadOnlyList<EnrollmentSampleItem> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            StatusMessage = "No valid audio clip found for analysis.";
            return;
        }

        IsAnalyzing = true;
        try
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                StatusMessage = $"Checking sample {i + 1} of {samples.Count}: {sample.FileName}";
                sample.IsAnalyzing = true;
                sample.AnalysisError = null;
                try
                {
                    // Decoding and analysis are CPU-bound; keep them off the UI thread.
                    sample.Report = await Task.Run(() => _qualityAnalyzer.AnalyzeAudioAsync(sample.Path, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sample.AnalysisError = ex.Message;
                }
                finally
                {
                    sample.IsAnalyzing = false;
                }
            }

            StatusMessage = AcceptedSampleCount > 0
                ? $"{AcceptedSampleCount} of {SampleFiles.Count} samples passed acoustic verification."
                : "No sample passed minimum quality standards. See feedback details.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Quality check cancelled.";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    public async Task<VoiceProfileSummary?> CreateProfileAsync(string? outputDir = null, CancellationToken cancellationToken = default)
    {
        var acceptedPaths = SampleFiles.Where(f => f.IsAccepted).Select(f => f.Path).ToList();
        if (!CanCreateProfile || acceptedPaths.Count == 0)
        {
            StatusMessage = "Cannot create profile: prerequisites not satisfied.";
            return null;
        }

        string name = _profileName.Trim();
        outputDir ??= AppPaths.ProfilesDirectory;
        string safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string outputPath = Path.Combine(outputDir, $"{safeName}.json");
        bool nameTaken = ProfileLibrary.List(outputDir).Any(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p.Path).Equals(Path.GetFileName(outputPath), StringComparison.OrdinalIgnoreCase));
        if (nameTaken || File.Exists(outputPath))
        {
            StatusMessage = $"A voice named '{name}' already exists. Choose another name, or delete the existing voice first.";
            return null;
        }

        IsEnrolling = true;
        StatusMessage = "Extracting speaker embeddings and building profile centroid...";

        try
        {
            Directory.CreateDirectory(outputDir);

            // Decoding, voice detection and embedding inference are CPU/GPU-bound; keep them off the UI thread.
            var profile = await Task.Run(() => _enrollmentService.EnrollProfileAsync(
                audioFilePaths: acceptedPaths,
                profileName: name,
                multiCondition: false,
                cancellationToken: cancellationToken), cancellationToken);

            profile.SaveToFile(outputPath);

            CreatedProfile = new VoiceProfileSummary(
                Name: profile.ProfileName,
                Path: outputPath,
                Dimension: profile.Centroid.Length,
                WindowCount: profile.EnrollmentEmbeddings.Count,
                CreatedAtUtc: DateTimeOffset.Parse(profile.CreatedAt));

            RefreshProfiles();
            CurrentStep = EnrollmentStep.Complete;
            StatusMessage = $"Profile '{_profileName}' is ready ({profile.EnrollmentEmbeddings.Count} voice samples).";
            ProfileCreated?.Invoke(CreatedProfile);
            return CreatedProfile;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Enrollment failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsEnrolling = false;
        }
    }

    public void Reset()
    {
        CurrentStep = EnrollmentStep.AudioSelection;
        SampleFiles.Clear();
        ProfileName = string.Empty;
        HasConsent = false;
        CreatedProfile = null;
        StatusMessage = null;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private bool SetField<T>(ref T field, T value, Action? onChanged, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            onChanged?.Invoke();
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
