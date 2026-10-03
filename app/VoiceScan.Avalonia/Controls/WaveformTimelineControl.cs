using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VoiceScan.App.Core.Models;

namespace VoiceScan.App.Controls;

public sealed class WaveformTimelineControl : Control
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(70, 34, 160, 107));
    private static readonly IBrush PossibleBrush = new SolidColorBrush(Color.FromArgb(70, 224, 138, 30));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(139, 151, 166));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.FromArgb(210, 107, 122, 142));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(16, 20, 26));

    private WaveformEnvelope? _envelope;
    private IReadOnlyList<HitSegmentResult>? _segments;
    private double _durationSeconds = 0.1;
    private double _currentPositionSeconds;
    private bool _hasData;

    public event EventHandler<double>? SeekRequested;

    public WaveformTimelineControl()
    {
        Height = 140;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SetData(WaveformEnvelope? envelope, IReadOnlyList<HitSegmentResult>? segments, double durationSeconds)
    {
        _envelope = envelope;
        _segments = segments;
        _durationSeconds = Math.Max(0.1, durationSeconds);
        _hasData = true;
        InvalidateVisual();
    }

    public void UpdatePlaybackCursor(double currentPositionSeconds)
    {
        _currentPositionSeconds = Math.Clamp(currentPositionSeconds, 0.0, _durationSeconds);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        context.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height), 10, 10);

        if (_segments != null)
        {
            foreach (var seg in _segments)
            {
                double x = seg.StartTimeSeconds / _durationSeconds * width;
                double w = Math.Max(4.0, (seg.EndTimeSeconds - seg.StartTimeSeconds) / _durationSeconds * width);
                var brush = seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase) ? MatchBrush : PossibleBrush;
                context.DrawRectangle(brush, null, new Rect(x, 0, w, height));
            }
        }

        if (_envelope is { BucketCount: > 0 })
        {
            double midY = height / 2.0;
            double step = width / _envelope.BucketCount;
            double barWidth = Math.Max(1.0, step - 0.5);
            for (int i = 0; i < _envelope.BucketCount; i++)
            {
                double top = midY - _envelope.MaxPeaks[i] * midY * 0.9;
                double bottom = midY - _envelope.MinPeaks[i] * midY * 0.9;
                context.DrawRectangle(BarBrush, null,
                    new Rect(i * step, top, barWidth, Math.Max(1.0, bottom - top)));
            }
        }

        double cursorX = _currentPositionSeconds / _durationSeconds * width;
        context.DrawLine(new Pen(CursorBrush, 2), new Point(cursorX, 0), new Point(cursorX, height));

        if (_hasData)
        {
            DrawLabel(context, "00:00", new Point(8, height - 18));
            DrawLabel(context, Format(_currentPositionSeconds), new Point(width / 2 - 14, height - 18), CursorBrush, FontWeight.Bold);
            var total = Format(_durationSeconds);
            DrawLabel(context, total, new Point(width - 8 - total.Length * 7, height - 18));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bounds.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        double ratio = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0.0, 1.0);
        double target = ratio * _durationSeconds;
        UpdatePlaybackCursor(target);
        SeekRequested?.Invoke(this, target);
    }

    private static string Format(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

    private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush? brush = null, FontWeight weight = FontWeight.Normal)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), 11, brush ?? LabelBrush);
        context.DrawText(formatted, origin);
    }
}
