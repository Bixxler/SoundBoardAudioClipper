using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soundboard.Audio;

namespace Soundboard;

/// <summary>
/// Draws a waveform. Dragging across it selects a time range, in seconds. A green line along the top marks
/// where there's voice; clicking it selects that phrase. A white line shows where a preview is playing.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1B)));
    private static readonly Pen WavePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xE0))), 1));
    private static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xF2, 0xB8, 0x4B)));
    private static readonly Pen SelectionEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B))), 1.5));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7C, 0x7E, 0x8A)));
    private static readonly Brush VoiceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0xD0, 0x8A)));
    private static readonly Brush VoiceTint = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0x5B, 0xD0, 0x8A)));
    private static readonly Brush PlayheadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x3B)));
    private static readonly Pen PlayheadPen = Freeze(new Pen(PlayheadBrush, 2));

    /// <summary>Clicks this close to the top hit the voice line instead of starting a selection.</summary>
    private const double VoiceLaneHeight = 14;

    private float[] _samples = [];
    private int _channels = 2;
    private int _sampleRate = 48000;
    private float[]? _peaks;
    private double _dragAnchor;
    private IReadOnlyList<TimeRange> _voice = [];
    private double? _playhead;

    public double Duration => _samples.Length / (double)_channels / _sampleRate;
    public double SelectionStart { get; private set; }
    public double SelectionEnd { get; private set; }
    public event EventHandler? SelectionChanged;

    public WaveformView()
    {
        Cursor = Cursors.IBeam;
        Focusable = false;
    }

    public void SetAudio(float[] samples, int sampleRate, int channels)
    {
        _samples = samples;
        _sampleRate = sampleRate;
        _channels = channels;
        _peaks = null;
        InvalidateVisual();
    }

    /// <summary>Where there's voice, shown as the green line. Empty hides it.</summary>
    public IReadOnlyList<TimeRange> Voice
    {
        get => _voice;
        set { _voice = value; InvalidateVisual(); }
    }

    /// <summary>The preview's position in seconds, or null when nothing is playing.</summary>
    public double? Playhead
    {
        get => _playhead;
        set { if (_playhead == value) return; _playhead = value; InvalidateVisual(); }
    }

    public void SetSelection(double start, double end)
    {
        if (end < start) (start, end) = (end, start);
        SelectionStart = Math.Clamp(start, 0, Duration);
        SelectionEnd = Math.Clamp(end, 0, Duration);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        if (_samples.Length == 0 || w < 2)
        {
            DrawLabel(dc, "No audio captured yet", new Point(10, h / 2 - 8));
            return;
        }

        var columns = (int)w;
        if (_peaks is null || _peaks.Length != columns) _peaks = ComputePeaks(columns);

        foreach (var range in _voice)
        {
            var (vx1, vx2) = (range.Start / Duration * w, range.End / Duration * w);
            var width = Math.Max(2, vx2 - vx1);
            dc.DrawRectangle(VoiceTint, null, new Rect(vx1, 0, width, h));
            dc.DrawRoundedRectangle(VoiceBrush, null, new Rect(vx1, 4, width, 4), 2, 2);
        }

        var mid = h / 2;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var x = 0; x < columns; x++)
            {
                var half = Math.Max(0.5, _peaks[x] * (mid - 4));
                ctx.BeginFigure(new Point(x + 0.5, mid - half), false, false);
                ctx.LineTo(new Point(x + 0.5, mid + half), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, WavePen, geometry);

        if (SelectionEnd > SelectionStart)
        {
            var x1 = SelectionStart / Duration * w;
            var x2 = SelectionEnd / Duration * w;
            dc.DrawRectangle(SelectionFill, null, new Rect(x1, 0, x2 - x1, h));
            dc.DrawLine(SelectionEdge, new Point(x1, 0), new Point(x1, h));
            dc.DrawLine(SelectionEdge, new Point(x2, 0), new Point(x2, h));
        }

        if (_playhead is { } at && at >= 0 && at <= Duration)
        {
            var px = at / Duration * w;
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
            // A small handle at the top so the line stands out against a busy waveform.
            var handle = new StreamGeometry();
            using (var ctx = handle.Open())
            {
                ctx.BeginFigure(new Point(px - 6, 0), true, true);
                ctx.LineTo(new Point(px + 6, 0), true, false);
                ctx.LineTo(new Point(px, 8), true, false);
            }
            handle.Freeze();
            dc.DrawGeometry(PlayheadBrush, null, handle);
        }

        DrawLabel(dc, $"−{Duration:0} s", new Point(6, h - 18));
        DrawLabel(dc, "now", new Point(w - 30, h - 18));
    }

    private float[] ComputePeaks(int columns)
    {
        var peaks = new float[columns];
        var frames = _samples.Length / _channels;
        for (var x = 0; x < columns; x++)
        {
            var from = (int)((long)frames * x / columns) * _channels;
            var to = (int)((long)frames * (x + 1) / columns) * _channels;
            float peak = 0;
            for (var i = from; i < to; i++)
            {
                var v = Math.Abs(_samples[i]);
                if (v > peak) peak = v;
            }
            peaks[x] = Math.Min(1f, peak);
        }
        return peaks;
    }

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private double TimeAt(double x) => ActualWidth <= 0 ? 0 : Math.Clamp(x / ActualWidth, 0, 1) * Duration;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_samples.Length == 0) return;
        var point = e.GetPosition(this);
        if (point.Y <= VoiceLaneHeight && VoiceAt(TimeAt(point.X)) is { } phrase)
        {
            SetSelection(phrase.Start, phrase.End);
            e.Handled = true;
            return;
        }
        CaptureMouse();
        _dragAnchor = TimeAt(e.GetPosition(this).X);
        SetSelection(_dragAnchor, _dragAnchor);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (IsMouseCaptured) SetSelection(_dragAnchor, TimeAt(point.X));
        else Cursor = point.Y <= VoiceLaneHeight && VoiceAt(TimeAt(point.X)) is not null ? Cursors.Hand : Cursors.IBeam;
    }

    /// <summary>The voice phrase at a time, allowing a few pixels either side so short phrases are easy to hit.</summary>
    private TimeRange? VoiceAt(double seconds)
    {
        var slop = ActualWidth > 0 ? 4 / ActualWidth * Duration : 0;
        foreach (var range in _voice)
            if (seconds >= range.Start - slop && seconds <= range.End + slop) return range;
        return null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _peaks = null;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
