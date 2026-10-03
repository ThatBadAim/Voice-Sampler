using System.Collections.ObjectModel;
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

    private EnrollmentStep _currentStep = EnrollmentStep.AudioSelection;
    private string? _selectedAudioPath;
    private string _profileName = string.Empty;
    private bool _hasConsent;
    private bool _isAnalyzing;
    private bool _isEnrolling;
    private AudioQualityReport? _qualityReport;
    private string? _statusMessage;
    private VoiceProfileSummary? _createdProfile;
    private VoiceProfileSummary? _selectedProfile;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a profile has been written to disk.</summary>
    public event Action<VoiceProfileSummary>? ProfileCreated;

    /// <summary>Raised after a saved voice was deleted.</summary>
    public event Action? ProfilesChanged;

    public ObservableCollection<VoiceProfileSummary> Profiles { get; } = [];

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

    public string? SelectedAudioPath
    {
        get => _selectedAudioPath;
        set
        {
            if (SetField(ref _selectedAudioPath, value))
            {
                QualityReport = null;
                OnPropertyChanged(nameof(CanProceedFromAudioSelection));
                OnPropertyChanged(nameof(CanProceedFromQuality));
                OnPropertyChanged(nameof(CanCreateProfile));
                if (string.IsNullOrWhiteSpace(_profileName) && !string.IsNullOrWhiteSpace(value))
                {
                    ProfileName = Path.GetFileNameWithoutExtension(value);
                }
            }
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
        private set => SetField(ref _isEnrolling, value);
    }

    public AudioQualityReport? QualityReport
    {
        get => _qualityReport;
        private set => SetField(ref _qualityReport, value);
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

    public bool CanProceedFromAudioSelection => !string.IsNullOrWhiteSpace(_selectedAudioPath) && File.Exists(_selectedAudioPath);
    public bool CanProceedFromConsent => _hasConsent;
    public bool CanProceedFromQuality => _qualityReport?.IsAcceptableForEnrollment == true;
    public bool CanCreateProfile => !string.IsNullOrWhiteSpace(_profileName) && CanProceedFromConsent && CanProceedFromQuality && !_isEnrolling;

    public EnrollmentWizardViewModel(IAudioQualityAnalyzer qualityAnalyzer, ProfileEnrollmentService enrollmentService)
    {
        _qualityAnalyzer = qualityAnalyzer;
        _enrollmentService = enrollmentService;
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
        catch (IOException ex)
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

    public async Task RunQualityCheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_selectedAudioPath) || !File.Exists(_selectedAudioPath))
        {
            StatusMessage = "No valid audio clip found for analysis.";
            return;
        }

        IsAnalyzing = true;
        StatusMessage = "Analyzing speech duration, SNR, and acoustic background noise...";

        try
        {
            QualityReport = await _qualityAnalyzer.AnalyzeAudioAsync(_selectedAudioPath, cancellationToken);
            OnPropertyChanged(nameof(CanProceedFromQuality));
            OnPropertyChanged(nameof(CanCreateProfile));

            if (QualityReport.IsAcceptableForEnrollment)
            {
                StatusMessage = "Audio passed acoustic verification.";
            }
            else
            {
                StatusMessage = "Audio failed minimum quality standards. See feedback details.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Quality check error: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    public async Task<VoiceProfileSummary?> CreateProfileAsync(string? outputDir = null, CancellationToken cancellationToken = default)
    {
        if (!CanCreateProfile || string.IsNullOrWhiteSpace(_selectedAudioPath))
        {
            StatusMessage = "Cannot create profile: prerequisites not satisfied.";
            return null;
        }

        IsEnrolling = true;
        StatusMessage = "Extracting speaker embeddings and building profile centroid...";

        try
        {
            outputDir ??= AppPaths.ProfilesDirectory;
            Directory.CreateDirectory(outputDir);
            string safeName = string.Concat(_profileName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string outputPath = Path.Combine(outputDir, $"{safeName}.json");

            var profile = await _enrollmentService.EnrollProfileAsync(
                audioFilePaths: [_selectedAudioPath],
                profileName: _profileName,
                multiCondition: false,
                cancellationToken: cancellationToken);

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
        SelectedAudioPath = null;
        ProfileName = string.Empty;
        HasConsent = false;
        QualityReport = null;
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
