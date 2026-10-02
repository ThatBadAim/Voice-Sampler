#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VoiceScan.App.Core.Models;
using Windows.UI;

namespace VoiceScan.App.Controls;

public sealed partial class WaveformTimelineControl : UserControl
{
    private WaveformEnvelope? _envelope;
    private IReadOnlyList<HitSegmentResult>? _segments;
    private double _durationSeconds;
    private double _currentPositionSeconds;

    public event EventHandler<double>? SeekRequested;

    public WaveformTimelineControl()
    {
        this.InitializeComponent();
        this.SizeChanged += (s, e) => RenderWaveform();
    }

    public void SetData(WaveformEnvelope? envelope, IReadOnlyList<HitSegmentResult>? segments, double durationSeconds)
    {
        _envelope = envelope;
        _segments = segments;
        _durationSeconds = Math.Max(0.1, durationSeconds);

        TotalTimeText.Text = TimeSpan.FromSeconds(_durationSeconds).ToString(@"mm\:ss");
        RenderWaveform();
    }

    public void UpdatePlaybackCursor(double currentPositionSeconds)
    {
        _currentPositionSeconds = Math.Clamp(currentPositionSeconds, 0.0, _durationSeconds);
        CurrentTimeText.Text = TimeSpan.FromSeconds(_currentPositionSeconds).ToString(@"mm\:ss");

        double width = ActualWidth > 0 ? ActualWidth : 600.0;
        double ratio = _durationSeconds > 0 ? _currentPositionSeconds / _durationSeconds : 0.0;
        double x = ratio * width;

        PlaybackCursorLine.X1 = x;
        PlaybackCursorLine.X2 = x;
    }

    private void RenderWaveform()
    {
        WaveformCanvas.Children.Clear();
        double width = ActualWidth > 0 ? ActualWidth : 600.0;
        double height = ActualHeight > 0 ? ActualHeight : 140.0;
        double midY = height / 2.0;

        // 1. Draw Hit Segment Highlights
        if (_segments != null && _durationSeconds > 0)
        {
            foreach (var seg in _segments)
            {
                double startRatio = seg.StartTimeSeconds / _durationSeconds;
                double endRatio = seg.EndTimeSeconds / _durationSeconds;
                double segX = startRatio * width;
                double segWidth = Math.Max(4.0, (endRatio - startRatio) * width);

                Color hitColor = seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(100, 16, 124, 65) // translucent green
                    : Color.FromArgb(100, 216, 59, 1); // translucent orange

                var rect = new Rectangle
                {
                    Width = segWidth,
                    Height = height,
                    Fill = new SolidColorBrush(hitColor)
                };
                Canvas.SetLeft(rect, segX);
                Canvas.SetTop(rect, 0);
                WaveformCanvas.Children.Add(rect);
            }
        }

        // 2. Draw Waveform Peaks
        if (_envelope != null && _envelope.BucketCount > 0)
        {
            var brush = new SolidColorBrush(Color.FromArgb(200, 120, 120, 120));
            int count = _envelope.BucketCount;
            double step = width / count;

            for (int i = 0; i < count; i++)
            {
                float min = _envelope.MinPeaks[i];
                float max = _envelope.MaxPeaks[i];
                double y1 = midY - (max * midY * 0.9);
                double y2 = midY - (min * midY * 0.9);

                var line = new Line
                {
                    X1 = i * step,
                    X2 = i * step,
                    Y1 = y1,
                    Y2 = y2,
                    Stroke = brush,
                    StrokeThickness = Math.Max(1.0, step - 0.5)
                };
                WaveformCanvas.Children.Add(line);
            }
        }
    }

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        double width = ActualWidth > 0 ? ActualWidth : 600.0;
        double ratio = Math.Clamp(point.Position.X / width, 0.0, 1.0);
        double targetSec = ratio * _durationSeconds;

        UpdatePlaybackCursor(targetSec);
        SeekRequested?.Invoke(this, targetSec);
    }
}
#endif
