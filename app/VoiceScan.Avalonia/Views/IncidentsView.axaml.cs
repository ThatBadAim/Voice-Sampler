using Avalonia.Controls;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class IncidentsView : UserControl
{
    public IncidentsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveLayout.SplitPanes(Split, e.NewSize.Width < ResponsiveLayout.StackedPageWidth,
            520, ListPane, DetailPane);
    }
}
