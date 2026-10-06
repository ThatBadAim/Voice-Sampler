using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VoiceScan.App.Core.Models;

namespace VoiceScan.App.Controls;

public sealed class WaveformTimelineControl : Control
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(70, 78, 159, 125));
    private static readonly IBrush PossibleBrush = new SolidColorBrush(Color.FromArgb(70, 201, 146, 58));
    private static readonly IBrush OffensiveBrush = new SolidColorBrush(Color.FromArgb(90, 220, 60, 60));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(143, 137, 126));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromRgb(210, 112, 79));
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.FromArgb(220, 176, 168, 154));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(31, 29, 26));

    public static readonly StyledProperty<WaveformEnvelope?> EnvelopeProperty =
        AvaloniaProperty.Register<WaveformTimelineControl, WaveformEnvelope?>(nameof(Envelope));

    static WaveformTimelineControl()
    {
        EnvelopeProperty.Changed.AddClassHandler<WaveformTimelineControl>((c, e) =>
        {
            var envelope = e.NewValue as WaveformEnvelope;
            c.SetData(envelope, null, envelope?.DurationSeconds ?? 0.1);
        });
    }

    /// <summary>Bindable alternative to <see cref="SetData"/> for a plain waveform without hit segments.</summary>
    public WaveformEnvelope? Envelope
    {
        get => GetValue(EnvelopeProperty);
        set => SetValue(EnvelopeProperty, value);
    }

    private WaveformEnvelope? _envelope;
    private IReadOnlyList<HitSegmentResult>? _segments;
    private double _durationSeconds = 0.1;
    private double _currentPositionSeconds;
    private bool _hasData;
    private HitSegmentResult? _hoveredSegment;

    public event EventHandler<double>? SeekRequested;
    public event EventHandler<HitSegmentResult>? SegmentSelected;

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
        _hoveredSegment = null;
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
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

        context.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height), 8, 8);

        if (_segments != null)
        {
            foreach (var seg in _segments)
            {
                double x = seg.StartTimeSeconds / _durationSeconds * width;
                double w = Math.Max(4.0, (seg.EndTimeSeconds - seg.StartTimeSeconds) / _durationSeconds * width);
                var brush = (seg.IsOffensive || seg.IsFlagged)
                    ? OffensiveBrush
                    : (seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase) ? MatchBrush : PossibleBrush);
                context.DrawRectangle(brush, null, new Rect(x, 0, w, height));

                if (ReferenceEquals(seg, _hoveredSegment))
                {
                    var highlightPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5);
                    context.DrawRectangle(null, highlightPen, new Rect(x, 0, w, height));
                }
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
            DrawLabel(context, Format(0), new Point(8, height - 18));
            DrawLabel(context, Format(_currentPositionSeconds), new Point(width / 2 - 14, height - 18), CursorBrush, FontWeight.Bold);
            var total = Format(_durationSeconds);
            DrawLabel(context, total, new Point(width - 8 - total.Length * 7, height - 18));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Bounds.Width <= 0 || _durationSeconds <= 0) return;

        double x = e.GetPosition(this).X;
        double ratio = Math.Clamp(x / Bounds.Width, 0.0, 1.0);
        double time = ratio * _durationSeconds;
        var hit = FindSegmentAtTime(time);

        if (!ReferenceEquals(hit, _hoveredSegment))
        {
            _hoveredSegment = hit;
            if (hit != null)
            {
                ToolTip.SetTip(this, CreateHoverCard(hit));
                ToolTip.SetIsOpen(this, true);
            }
            else
            {
                ToolTip.SetIsOpen(this, false);
                ToolTip.SetTip(this, null);
            }
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredSegment != null)
        {
            _hoveredSegment = null;
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bounds.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        double ratio = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0.0, 1.0);
        double target = ratio * _durationSeconds;
        UpdatePlaybackCursor(target);

        var hit = FindSegmentAtTime(target);
        if (hit != null)
        {
            SegmentSelected?.Invoke(this, hit);
            SeekRequested?.Invoke(this, hit.Start);
        }
        else
        {
            SeekRequested?.Invoke(this, target);
        }
    }

    private HitSegmentResult? FindSegmentAtTime(double seconds)
    {
        if (_segments == null || _segments.Count == 0) return null;

        for (int i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            if (seconds >= s.StartTimeSeconds && seconds <= s.EndTimeSeconds)
            {
                return s;
            }
        }

        double bestDist = 0.2;
        HitSegmentResult? nearest = null;
        for (int i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            double dist = Math.Min(Math.Abs(seconds - s.StartTimeSeconds), Math.Abs(seconds - s.EndTimeSeconds));
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = s;
            }
        }
        return nearest;
    }

    private static Control CreateHoverCard(HitSegmentResult seg)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 28, 26, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 80, 75, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            MaxWidth = 360
        };

        var rootStack = new StackPanel { Spacing = 8 };

        // Header: Timing + Verdict + Speaker Pill + Offensive Badge
        var header = new WrapPanel { Orientation = Orientation.Horizontal };

        var timeText = new TextBlock
        {
            Text = $"{seg.StartTimeSeconds:F1}s – {seg.EndTimeSeconds:F1}s ({seg.DurationSeconds:F1}s)",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(240, 235, 225))
        };
        header.Children.Add(timeText);

        var verdictPill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 0, 8, 4),
            Background = seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromArgb(50, 78, 159, 125))
                : new SolidColorBrush(Color.FromArgb(50, 201, 146, 58)),
            Child = new TextBlock
            {
                Text = seg.Verdict,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = seg.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.FromRgb(120, 210, 160))
                    : new SolidColorBrush(Color.FromRgb(240, 180, 90))
            }
        };
        header.Children.Add(verdictPill);

        // Styled speaker pill tag: [SPEAKER_00]
        if (!string.IsNullOrWhiteSpace(seg.DisplaySpeakerLabel))
        {
            var speakerPill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 8, 4),
                Background = new SolidColorBrush(Color.FromArgb(255, 45, 42, 38)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 75, 70, 64)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = seg.DisplaySpeakerLabel,
                    FontSize = 11,
                    FontWeight = FontWeight.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(210, 205, 195))
                }
            };
            header.Children.Add(speakerPill);
        }

        // When IsOffensive == true or IsFlagged == true, display a warning badge listing the violated safety categories
        if (seg.IsOffensive || seg.IsFlagged)
        {
            string violationSummary = !string.IsNullOrWhiteSpace(seg.ViolationsSummary)
                ? seg.ViolationsSummary
                : "Safety Policy Violation";

            var warningBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 8, 4),
                Background = new SolidColorBrush(Color.FromArgb(60, 220, 60, 60)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 220, 60, 60)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = $"⚠ {violationSummary}",
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120))
                }
            };
            header.Children.Add(warningBadge);
        }

        rootStack.Children.Add(header);

        // Render the transcribed text in an accessible, selectable text container
        if (!string.IsNullOrWhiteSpace(seg.Transcript))
        {
            var transcriptSection = new StackPanel { Spacing = 2 };
            transcriptSection.Children.Add(new TextBlock
            {
                Text = "Transcript",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 135, 125))
            });
            transcriptSection.Children.Add(new SelectableTextBlock
            {
                Text = seg.Transcript,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 240, 230))
            });
            rootStack.Children.Add(transcriptSection);
        }

        rootStack.Children.Add(new TextBlock
        {
            Text = "Click to seek and play from segment start",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 125, 115)),
            FontStyle = FontStyle.Italic
        });

        card.Child = rootStack;
        return card;
    }

    private static string Format(double seconds) => TimeFormat.Clock(seconds);

    private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush? brush = null, FontWeight weight = FontWeight.Normal)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), 11, brush ?? LabelBrush);
        context.DrawText(formatted, origin);
    }
}
