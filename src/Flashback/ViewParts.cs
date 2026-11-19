using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Flashback.Core;

namespace Flashback;

/// <summary>
/// Text set top to bottom, one character under the other, as Japanese is written vertically. The comma and full stop
/// move to the upper right of their square, where vertical writing puts them.
/// </summary>
public sealed class VerticalText : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(VerticalText),
        new FrameworkPropertyMetadata("", (d, _) => ((VerticalText)d).Rebuild()));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(VerticalText),
        new FrameworkPropertyMetadata(4.0, (d, _) => ((VerticalText)d).Rebuild()));

    public VerticalText()
    {
        Loaded += (_, _) => Rebuild(); // the inherited font size is known by now
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private void Rebuild()
    {
        Children.Clear();
        double size = TextElement.GetFontSize(this);
        foreach (char c in Text ?? "")
        {
            var glyph = new TextBlock { Text = c.ToString(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, Spacing) };
            if (c is '、' or '。') glyph.RenderTransform = new TranslateTransform(size * 0.55, -size * 0.55);
            Children.Add(glyph);
        }
    }
}

/// <summary>
/// The tiles of one band on the Clips page: 16:9 cells of one size, a set number across, row after row, 2 pixels apart.
/// A band holding a few days shows their clips the same way, as if they were one day's.
/// </summary>
public sealed class BandTilesPanel : Panel
{
    public const double Gap = 2;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(BandTilesPanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    private (double Width, double Height, int Columns) Cell(double width)
    {
        int columns = Math.Max(1, Columns);
        double w = Math.Max(0, (width - (columns - 1) * Gap) / columns);
        return (w, Math.Floor(w * 9 / 16), columns);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 0 : available.Width;
        var (w, h, columns) = Cell(width);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(w, h));
        int rows = (InternalChildren.Count + columns - 1) / columns;
        return new Size(width, rows == 0 ? 0 : rows * h + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (w, h, columns) = Cell(finalSize.Width);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / columns, column = i % columns;
            // Whole pixels, so the 2-pixel seams between tiles stay sharp.
            double left = Math.Round(column * (w + Gap)), right = Math.Round(column * (w + Gap) + w);
            InternalChildren[i].Arrange(new Rect(left, row * (h + Gap), right - left, h));
        }
        return finalSize;
    }
}

/// <summary>
/// A band on the Clips page: one day, or a few consecutive days that each have only a few clips, so they share a row
/// instead of each leaving most of one empty. Its label lists the days, newest first, in the same order as their tiles.
/// </summary>
public sealed class ClipBand
{
    public ClipBand(IReadOnlyList<ClipDay> days)
    {
        Days = days;
        Clips = days.SelectMany(d => d.Clips).ToList();
    }

    public IReadOnlyList<ClipDay> Days { get; }
    public ClipDay First => Days[0];
    public bool IsCombined => Days.Count > 1;
    public IReadOnlyList<ClipItem> Clips { get; }

    public bool SameAs(ClipBand other) => Days.Count == other.Days.Count && Days.Zip(other.Days).All(p => p.First.SameAs(p.Second));

    /// <summary>
    /// Bands for <paramref name="days"/> (newest first) with <paramref name="columns"/> tiles across. Days join the band
    /// before them while they are in the same week and their clips all fit in one row together, up to
    /// <paramref name="maxDays"/> days a band. A day that fills a row has a band of its own, and so does a day alone in
    /// its week, however few its clips.
    /// </summary>
    public static List<ClipBand> Group(IReadOnlyList<ClipDay> days, int columns, int maxDays)
    {
        var bands = new List<ClipBand>();
        var current = new List<ClipDay>();
        int tiles = 0;
        foreach (ClipDay day in days)
        {
            bool fits = current.Count > 0 && current.Count < maxDays && WeekOf(day.Day) == WeekOf(current[0].Day)
                        && tiles + day.Clips.Count <= columns;
            if (!fits && current.Count > 0)
            {
                bands.Add(new ClipBand(current));
                current = [];
                tiles = 0;
            }
            current.Add(day);
            tiles += day.Clips.Count;
        }
        if (current.Count > 0) bands.Add(new ClipBand(current));
        return bands;
    }

    /// <summary>The first day of the week <paramref name="date"/> is in, by the user's Windows settings (Sunday in the US).</summary>
    public static DateTime WeekOf(DateTime date)
    {
        DayOfWeek first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return date.Date.AddDays(-(((int)date.DayOfWeek - (int)first + 7) % 7));
    }
}

/// <summary>A day on the Clips page: its name, date, how many clips were saved that day, and the ones shown.</summary>
public sealed class ClipDay
{
    public ClipDay(DateTime date, int count, IReadOnlyList<ClipItem> clips)
    {
        Day = date.Date;
        Clips = clips;
        Label = ClipLibrary.DayName(Day).ToUpperInvariant();
        Date = Day.Year == DateTime.Today.Year ? Day.ToString("MM.dd") : Day.ToString("MM.dd.yy");
        CountText = count == 1 ? "1 clip" : $"{count} clips";
    }

    public DateTime Day { get; }
    public string Label { get; }
    public string Date { get; }
    public string CountText { get; }
    public IReadOnlyList<ClipItem> Clips { get; }

    /// <summary>Same day, same clips: the band can stay as it is.</summary>
    public bool SameAs(ClipDay other) => Day == other.Day && CountText == other.CountText && Clips.SequenceEqual(other.Clips);
}

/// <summary>A quality choice in Settings: its name and how much a minute of clip can take at most.</summary>
public sealed class QualityTier : INotifyPropertyChanged
{
    private string _rate = "";

    public QualityTier(string name) => Name = name;

    public string Name { get; }

    public string Rate
    {
        get => _rate;
        set
        {
            if (_rate == value) return;
            _rate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rate)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
