using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Flashback.Core;

namespace Flashback;

/// <summary>One audio track drawn on the timeline.</summary>
public sealed record TimelineTrack(string Name, Color Color, float[] Peaks);

/// <summary>
/// The editing strip under the video: a time ruler, the video bar (a strip of frames from the clip) with the trim
/// handles, and a waveform per audio track. Clicking or dragging on the ruler or a waveform moves the playhead; dragging a handle on the video bar
/// changes the trimmed range. The ruler and the waveforms are drawn once per size and reused, so moving the playhead
/// (many times a second while scrubbing) only redraws the cheap parts.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public const double RulerHeight = 14;
    public const double VideoHeight = 30;
    public const double TrackHeight = 34;
    /// <summary>Row height once there are more tracks than fit comfortably at <see cref="TrackHeight"/>.</summary>
    public const double CompactTrackHeight = 28;
    public const double RowGap = 5;
    private const double HandleWidth = 8;
    private const double MinTrimSeconds = 0.5;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrimStartProperty = DependencyProperty.Register(nameof(TrimStart), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrimEndProperty = DependencyProperty.Register(nameof(TrimEnd), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Espresso shades, a Beige trim range and a White playhead.
    private static readonly Brush RowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x2B, 0x26)));
    private static readonly Brush RulerText = Frozen(new SolidColorBrush(Color.FromRgb(0xDC, 0xC7, 0xAA)));
    private static readonly Pen TickPen = FrozenPen(Color.FromArgb(0x70, 0xF5, 0xE6, 0xCA), 1);
    private static readonly Pen MinorTickPen = FrozenPen(Color.FromArgb(0x38, 0xF5, 0xE6, 0xCA), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xFF, 0xFC, 0xF7), 1.5);
    private static readonly Brush PlayheadBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFC, 0xF7)));
    private static readonly Brush HandleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDC, 0xC7, 0xAA)));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFC, 0xF7)));
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0xDC, 0xC7, 0xAA), 1);
    private static readonly Pen GripPen = FrozenPen(Color.FromArgb(0xA0, 0x4B, 0x38, 0x32), 1);
    private static readonly Brush VideoBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x2B, 0x26)));
    /// <summary>Laid over the parts of the video bar outside the kept range.</summary>
    private static readonly Brush OutsideBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB8, 0x3A, 0x2B, 0x26)));
    private static readonly Typeface Font = new((FontFamily)Application.Current.FindResource("MonoFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly List<TimelineTrack> _tracks = new();
    private readonly Dictionary<TimelineTrack, (Brush Bright, Brush Dim)> _brushes = new();
    private readonly List<StreamGeometry> _waves = new();
    private double _wavesWidth = -1;
    private DrawingGroup? _ruler;
    private (double Width, double Duration) _rulerKey;
    private enum Drag { None, Playhead, Start, End }
    private Drag _drag;
    private ImageSource? _filmstrip;
    private Brush? _filmstripBrush;

    public TimelineControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double TrimStart { get => (double)GetValue(TrimStartProperty); set => SetValue(TrimStartProperty, value); }
    public double TrimEnd { get => (double)GetValue(TrimEndProperty); set => SetValue(TrimEndProperty, value); }

    /// <summary>The user moved the playhead (while dragging this fires for every movement).</summary>
    public event Action<double>? SeekRequested;
    public event Action? ScrubStarted;
    public event Action? ScrubEnded;
    public event Action? TrimChanged;

    public bool IsTrimmed => Duration > 0 && (TrimStart > 0.01 || TrimEnd < Duration - 0.01);

    /// <summary>Frames from across the clip, drawn in the video bar (cropped to its height, not squashed).</summary>
    public ImageSource? Filmstrip
    {
        get => _filmstrip;
        set
        {
            _filmstrip = value;
            _filmstripBrush = null;
            if (value != null)
            {
                var brush = new ImageBrush(value) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center, Opacity = 0.9 };
                brush.Freeze();
                _filmstripBrush = brush;
            }
            InvalidateVisual();
        }
    }

    /// <summary>Height of each audio row for this many tracks; the volume rows beside the timeline use the same.</summary>
    public static double TrackHeightFor(int tracks) => tracks > 3 ? CompactTrackHeight : TrackHeight;

    public double RowHeight => TrackHeightFor(_tracks.Count);

    public void SetTracks(IEnumerable<TimelineTrack> tracks)
    {
        _tracks.Clear();
        _brushes.Clear();
        _waves.Clear();
        _wavesWidth = -1;
        _tracks.AddRange(tracks);
        foreach (TimelineTrack t in _tracks)
        {
            _brushes[t] = (Frozen(new SolidColorBrush(t.Color)), Frozen(new SolidColorBrush(Color.FromArgb(0x60, t.Color.R, t.Color.G, t.Color.B))));
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Rows that are drawn: the waveform rows, or one empty row while audio is still loading.</summary>
    private int TrackRows => Math.Max(1, _tracks.Count);

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = RulerHeight + RowGap + VideoHeight + TrackRows * (RowGap + RowHeight);
        return new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, height);
    }

    // ---------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double videoTop = RulerHeight + RowGap, rowHeight = RowHeight;
        dc.DrawRectangle(VideoBrush, null, new Rect(0, videoTop, w, VideoHeight));
        for (int i = 0; i < TrackRows; i++)
            dc.DrawRectangle(RowBrush, null, new Rect(0, TrackTop(i), w, rowHeight));
        if (Duration <= 0 || w < 10) return;

        double px = w / Duration;
        double start = Math.Clamp(TrimStart, 0, Duration), end = Math.Clamp(TrimEnd, 0, Duration);
        double xs = start * px, xe = end * px;

        if (_ruler == null || _rulerKey != (w, Duration))
        {
            _ruler = new DrawingGroup();
            using (DrawingContext rc = _ruler.Open()) DrawRuler(rc, w, px);
            _ruler.Freeze();
            _rulerKey = (w, Duration);
        }
        dc.DrawDrawing(_ruler);

        // Video bar: the clip's frames, darkened outside the kept range, handles at its ends.
        if (_filmstripBrush != null) dc.DrawRectangle(_filmstripBrush, null, new Rect(0, videoTop, w, VideoHeight));
        if (xs > 0) dc.DrawRectangle(OutsideBrush, null, new Rect(0, videoTop, xs, VideoHeight));
        if (xe < w) dc.DrawRectangle(OutsideBrush, null, new Rect(xe, videoTop, w - xe, VideoHeight));
        dc.DrawRectangle(null, SelectionPen, new Rect(xs + 0.75, videoTop + 0.75, Math.Max(0, xe - xs - 1.5), VideoHeight - 1.5));
        DrawHandle(dc, xs, videoTop, true);
        DrawHandle(dc, xe, videoTop, false);

        // Waveforms, dimmed outside the kept range. A track without peaks yet is still being decoded.
        if (_wavesWidth != w)
        {
            _waves.Clear();
            for (int i = 0; i < _tracks.Count; i++) _waves.Add(WaveShape(_tracks[i].Peaks, w, TrackTop(i), rowHeight));
            _wavesWidth = w;
        }
        bool loading = _tracks.Count == 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            TimelineTrack t = _tracks[i];
            if (t.Peaks.Length == 0)
            {
                loading = true;
                continue;
            }
            StreamGeometry shape = _waves[i];
            var (bright, dim) = _brushes[t];
            dc.DrawGeometry(dim, null, shape);
            dc.PushClip(new RectangleGeometry(new Rect(xs, TrackTop(i), Math.Max(0, xe - xs), rowHeight)));
            dc.DrawGeometry(bright, null, shape);
            dc.Pop();
        }
        if (loading)
        {
            var text = Text("LOADING AUDIO", 10, RulerText);
            dc.DrawText(text, new Point(10, TrackTop(0) + (rowHeight - text.Height) / 2));
        }

        // Kept range shading across the audio rows, then the playhead over everything.
        dc.DrawRectangle(SelectionBrush, null, new Rect(xs, TrackTop(0), Math.Max(0, xe - xs), h - TrackTop(0)));
        double xp = Math.Round(Math.Clamp(Position, 0, Duration) * px) + 0.5;
        dc.DrawLine(PlayheadPen, new Point(xp, 2), new Point(xp, h));
        var head = new StreamGeometry();
        using (StreamGeometryContext g = head.Open())
        {
            g.BeginFigure(new Point(xp - 5, 0), true, true);
            g.LineTo(new Point(xp + 5, 0), false, false);
            g.LineTo(new Point(xp, 7), false, false);
        }
        dc.DrawGeometry(PlayheadBrush, null, head);
    }

    private void DrawRuler(DrawingContext dc, double w, double px)
    {
        double step = 1;
        foreach (double candidate in new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
        {
            step = candidate;
            if (candidate * px >= 64) break;
        }
        double minor = step / (step >= 10 ? 5 : 2);
        for (double t = 0; t <= Duration + 1e-6; t += minor)
        {
            double x = Math.Round(t * px) + 0.5;
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            dc.DrawLine(major ? TickPen : MinorTickPen, new Point(x, major ? RulerHeight - 7 : RulerHeight - 3), new Point(x, RulerHeight));
        }
    }

    private static void DrawHandle(DrawingContext dc, double x, double top, bool left)
    {
        double hx = left ? x : x - HandleWidth;
        dc.DrawRectangle(HandleBrush, null, new Rect(hx, top, HandleWidth, VideoHeight));
        double gx = Math.Round(hx + HandleWidth / 2) + 0.5;
        dc.DrawLine(GripPen, new Point(gx, top + 9), new Point(gx, top + VideoHeight - 9));
    }

    private static StreamGeometry WaveShape(float[] peaks, double w, double top, double height)
    {
        if (peaks.Length == 0) return new StreamGeometry();
        int columns = Math.Max(1, (int)w);
        var upper = new double[columns];
        double mid = top + height / 2, half = height / 2 - 2;
        double perColumn = peaks.Length / (double)columns;
        for (int x = 0; x < columns; x++)
        {
            int from = (int)(x * perColumn), to = Math.Max(from + 1, (int)((x + 1) * perColumn));
            float peak = 0;
            for (int i = from; i < to && i < peaks.Length; i++)
            {
                if (peaks[i] > peak) peak = peaks[i];
            }
            upper[x] = Math.Max(1, Math.Sqrt(peak) * half); // square root lifts quiet passages so speech stays visible
        }
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(0, mid - upper[0]), true, true);
            for (int x = 1; x < columns; x++) g.LineTo(new Point(x, mid - upper[x]), false, false);
            g.LineTo(new Point(columns, mid - upper[columns - 1]), false, false);
            g.LineTo(new Point(columns, mid + upper[columns - 1]), false, false);
            for (int x = columns - 1; x >= 0; x--) g.LineTo(new Point(x, mid + upper[x]), false, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private double TrackTop(int index) => RulerHeight + RowGap + VideoHeight + RowGap + index * (RowHeight + RowGap);

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---------------------------------------------------------------- mouse

    private Drag HitTest(Point p)
    {
        if (Duration <= 0) return Drag.None;
        double px = ActualWidth / Duration;
        double videoTop = RulerHeight + RowGap;
        if (p.Y >= videoTop && p.Y < videoTop + VideoHeight)
        {
            double xs = TrimStart * px, xe = TrimEnd * px;
            bool nearStart = p.X >= xs - 4 && p.X <= xs + HandleWidth + 4;
            bool nearEnd = p.X >= xe - HandleWidth - 4 && p.X <= xe + 4;
            if (nearStart && nearEnd) return p.X - xs < xe - p.X ? Drag.Start : Drag.End;
            if (nearStart) return Drag.Start;
            if (nearEnd) return Drag.End;
            return Drag.None;
        }
        return Drag.Playhead;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Drag hit = HitTest(p);
            Cursor = hit is Drag.Start or Drag.End ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        Apply(p);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point p = e.GetPosition(this);
        _drag = HitTest(p);
        if (_drag == Drag.None) return;
        CaptureMouse();
        Focus();
        if (_drag == Drag.Playhead) ScrubStarted?.Invoke();
        Apply(p);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        Drag was = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (was == Drag.Playhead) ScrubEnded?.Invoke();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == Drag.Playhead) ScrubEnded?.Invoke();
        _drag = Drag.None;
    }

    private void Apply(Point p)
    {
        double t = Math.Clamp(p.X / ActualWidth * Duration, 0, Duration);
        switch (_drag)
        {
            case Drag.Playhead:
                Position = t;
                SeekRequested?.Invoke(t);
                break;
            case Drag.Start:
                TrimStart = Math.Min(t, Math.Max(0, TrimEnd - MinTrimSeconds));
                TrimChanged?.Invoke();
                break;
            case Drag.End:
                TrimEnd = Math.Max(t, Math.Min(Duration, TrimStart + MinTrimSeconds));
                TrimChanged?.Invoke();
                break;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }
}
