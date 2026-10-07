using Avalonia.Controls;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class SpeakersView : UserControl
{
    public SpeakersView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveLayout.SplitPanes(Split, e.NewSize.Width < ResponsiveLayout.StackedPageWidth,
            380, ListPane, DetailPane);
    }
}
