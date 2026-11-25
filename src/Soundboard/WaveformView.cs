using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Soundboard;

/// <summary>Draws a waveform. Dragging across it selects a time range, in seconds.</summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1B)));
    private static readonly Pen WavePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xE0))), 1));
    private static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xF2, 0xB8, 0x4B)));
    private static readonly Pen SelectionEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B))), 1.5));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7C, 0x7E, 0x8A)));

    private float[] _samples = [];
    private int _channels = 2;
    private int _sampleRate = 48000;
    private float[]? _peaks;
    private double _dragAnchor;

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
        CaptureMouse();
        _dragAnchor = TimeAt(e.GetPosition(this).X);
        SetSelection(_dragAnchor, _dragAnchor);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SetSelection(_dragAnchor, TimeAt(e.GetPosition(this).X));
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
