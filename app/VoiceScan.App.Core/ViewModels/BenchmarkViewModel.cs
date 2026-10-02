using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;

namespace VoiceScan.App.Core.ViewModels;

public sealed class BenchmarkViewModel : INotifyPropertyChanged
{
    private readonly IBenchmarkService _benchmarkService;
    private BenchmarkReportSummary? _selectedReport;
    private bool _isLoading;
    private string? _statusMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BenchmarkReportSummary> AvailableReports { get; } = [];
    public ObservableCollection<SnrTierBenchmarkItem> SnrTiers { get; } = [];

    public BenchmarkReportSummary? SelectedReport
    {
        get => _selectedReport;
        set
        {
            if (SetField(ref _selectedReport, value))
            {
                SnrTiers.Clear();
                if (value != null)
                {
                    foreach (var tier in value.SnrTiers)
                    {
                        SnrTiers.Add(tier);
                    }
                }
                OnPropertyChanged(nameof(HasSelectedReport));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool HasSelectedReport => _selectedReport != null;

    public BenchmarkViewModel(IBenchmarkService benchmarkService)
    {
        _benchmarkService = benchmarkService;
    }

    public async Task InitializeAsync(string reportsRoot = "eval/reports", CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "Loading evaluation reports and bootstrap confidence intervals...";

        try
        {
            var reports = await _benchmarkService.LoadAvailableReportsAsync(reportsRoot, cancellationToken);
            AvailableReports.Clear();
            foreach (var r in reports)
            {
                AvailableReports.Add(r);
            }

            // Prefer step1_clustering, step4, or latest
            SelectedReport = AvailableReports.FirstOrDefault(r => r.ReportId == "step1_clustering")
                          ?? AvailableReports.FirstOrDefault(r => r.ReportId == "latest")
                          ?? AvailableReports.FirstOrDefault();

            StatusMessage = AvailableReports.Count > 0
                ? $"Loaded {AvailableReports.Count} verified evaluation runs from {reportsRoot}."
                : $"No evaluation runs found in {reportsRoot}. Run evaluation harness first.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load benchmarks: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
