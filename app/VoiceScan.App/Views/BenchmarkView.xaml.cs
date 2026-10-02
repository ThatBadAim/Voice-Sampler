#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App.Views;

public sealed partial class BenchmarkView : Page
{
    public BenchmarkViewModel? ViewModel { get; private set; }

    public BenchmarkView()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is BenchmarkViewModel vm)
        {
            ViewModel = vm;
            SnrTiersListView.ItemsSource = ViewModel.SnrTiers;
            await ViewModel.InitializeAsync();

            ReportComboBox.ItemsSource = ViewModel.AvailableReports;
            ReportComboBox.DisplayMemberPath = nameof(BenchmarkReportSummary.ReportTitle);
            ReportComboBox.SelectedItem = ViewModel.SelectedReport;

            UpdateReportUI();
        }
        base.OnNavigatedTo(e);
    }

    private void ReportComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && ReportComboBox.SelectedItem is BenchmarkReportSummary report)
        {
            ViewModel.SelectedReport = report;
            UpdateReportUI();
        }
    }

    private void UpdateReportUI()
    {
        if (ViewModel?.SelectedReport == null) return;
        var r = ViewModel.SelectedReport;

        RecallValueText.Text = $"{r.OverallRecall * 100.0:F1} %";
        RecallCiText.Text = $"95% CI: [{r.OverallRecallLowCi * 100.0:F1}%, {r.OverallRecallHighCi * 100.0:F1}%]";

        PrecisionValueText.Text = $"{r.OverallPrecision * 100.0:F1} %";
        PrecisionCiText.Text = $"95% CI: [{r.OverallPrecisionLowCi * 100.0:F1}%, {r.OverallPrecisionHighCi * 100.0:F1}%]";

        FaValueText.Text = $"{r.OverallFaPerHour:F2}";
        FaCiText.Text = $"95% CI: [{r.OverallFaLowCi:F2}, {r.OverallFaHighCi:F2}]";

        SpeedValueText.Text = $"{r.SpeedMultiple:F1}x";
        MetadataSummaryText.Text = $"Target: {r.TargetProfile}  |  Split: {r.Split}  |  Commit: {r.GitCommit}";
        StatusMessageText.Text = $"Verified empirical run: {r.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC";
    }
}
#endif
