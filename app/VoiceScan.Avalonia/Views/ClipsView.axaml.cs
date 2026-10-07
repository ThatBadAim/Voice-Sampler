using Avalonia.Controls;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ClipsView : UserControl
{
    public ClipsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveLayout.SplitPanes(Split, e.NewSize.Width < ResponsiveLayout.StackedPageWidth,
            420, ListPane, DetailPane);
    }
}
