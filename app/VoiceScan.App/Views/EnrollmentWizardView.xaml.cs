#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App.Views;

public sealed partial class EnrollmentWizardView : Page
{
    public EnrollmentWizardViewModel? ViewModel { get; private set; }

    public EnrollmentWizardView()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is EnrollmentWizardViewModel vm)
        {
            ViewModel = vm;
        }
        base.OnNavigatedTo(e);
    }

    private void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        // Windows file picker or file input dialog
        if (ViewModel != null)
        {
            // Set test file or open picker
            ViewModel.SelectedAudioPath = "eval/data_config/speech/speaker_alpha/ref_01.wav";
            SelectedFilePathText.Text = ViewModel.SelectedAudioPath;
            WizardProgressBar.Value = 2;
            UpdateCreateButtonState();
        }
    }

    private void RecordMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            SelectedFilePathText.Text = "🎙️ Recorded audio stream (16kHz mono float PCM).";
            WizardProgressBar.Value = 2;
            UpdateCreateButtonState();
        }
    }

    private async void RunQualityCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        RunQualityCheckButton.IsEnabled = false;
        StatusMessageText.Text = "Analyzing audio acoustic metrics...";

        await ViewModel.RunQualityCheckAsync();

        if (ViewModel.QualityReport != null)
        {
            var rep = ViewModel.QualityReport;
            SpeechDurationText.Text = $"{rep.SpeechDurationSeconds:F1} s";
            SnrText.Text = $"{rep.SnrEstimateDb:F1} dB";
            NoiseFloorText.Text = $"{rep.NoiseFloorRms:F4} RMS";

            FeedbackBox.Visibility = Visibility.Visible;
            FeedbackMessageText.Text = string.Join("\n• ", rep.FeedbackMessages);

            WizardProgressBar.Value = 3;
        }

        RunQualityCheckButton.IsEnabled = true;
        StatusMessageText.Text = ViewModel.StatusMessage ?? "";
        UpdateCreateButtonState();
    }

    private void ConsentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.HasConsent = ConsentCheckBox.IsChecked == true;
            if (ViewModel.HasConsent)
            {
                WizardProgressBar.Value = 4;
            }
            UpdateCreateButtonState();
        }
    }

    private void UpdateCreateButtonState()
    {
        if (ViewModel != null)
        {
            // STRICT MANDATORY CONSENT: button remains disabled until consent check is checked!
            CreateProfileButton.IsEnabled = ViewModel.CanProceedFromConsent && ViewModel.CanProceedFromAudioSelection;
        }
    }

    private async void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        ViewModel.ProfileName = ProfileNameInput.Text.Trim();
        ViewModel.MultiCondition = MultiConditionCheckBox.IsChecked == true;

        CreateProfileButton.IsEnabled = false;
        var result = await ViewModel.CreateProfileAsync();

        if (result != null)
        {
            StatusMessageText.Text = $"✅ Profile '{result.Name}' created successfully with {result.Dimension} embedding dimensions.";
        }
        else
        {
            StatusMessageText.Text = ViewModel.StatusMessage ?? "Enrollment failed.";
        }

        CreateProfileButton.IsEnabled = true;
    }
}
#endif
