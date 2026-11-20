using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Flashback.Core;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Flashback;

public partial class MainWindow : Window
{
    private const int PageSize = 60;
    /// <summary>Height of the header, which is also the part of the window that drags it.</summary>
    private const double HeaderHeight = 64;
    /// <summary>Width of the window's edge that resizes it.</summary>
    private const double GripSize = 6;
    /// <summary>Width of each of the minimize, maximize and close buttons.</summary>
    private const double CaptionWidth = 46;
    /// <summary>Narrowest the Latest panel's details get, beside the clip itself.</summary>
    private const double HeroPanelMin = 344;
    /// <summary>Roughly how tall the Latest panel's details are at their normal size, for fitting the stills under them.</summary>
    private const double HeroDetailsHeight = 230;
    /// <summary>Width of the Beige label left of each band of tiles.</summary>
    private const double DayLabelWidth = 156;
    /// <summary>Tiles at least this tall leave room in a band's label for three days; shorter ones for two.</summary>
    private const double ThreeDayTileHeight = 150;

    public static readonly DependencyProperty TileColumnsProperty =
        DependencyProperty.Register(nameof(TileColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(4));

    private readonly Engine _engine;
    private readonly App _app;
    private readonly ObservableCollection<ClipItem> _clips = new();
    /// <summary>Every clip but the newest, by day, newest first; regrouped into <see cref="_bands"/> when the width changes.</summary>
    private List<ClipDay> _days = [];
    private readonly ObservableCollection<ClipBand> _bands = new();
    private int _bandDays = 2;
    private readonly Dictionary<string, QualityTier> _tiers = new();
    private readonly SemaphoreSlim _thumbGate = new(3, 3);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Brush _white, _cream, _beige, _bean, _coffee, _alert;
    private FileSystemWatcher? _watcher;
    private string _watchedFolder = "";
    private int _shown = PageSize;
    private int _reloadVersion;
    private bool _pruned;
    private string? _heroKey;
    private int _heroVersion;
    private double _heroAspect = 16.0 / 9;
    private double _frameAspect = 16.0 / 9;
    private bool _wideSettings;

    private bool _loading = true;
    private bool _capturingHotkey;
    private bool _shuttingDown;
    private string _hotkeyMessage = "";
    private double _desktopLevel, _micLevel;

    public MainWindow(Engine engine, App app)
    {
        _engine = engine;
        _app = app;
        InitializeComponent();
        _white = (Brush)FindResource("WhiteBrush");
        _cream = (Brush)FindResource("CreamBrush");
        _beige = (Brush)FindResource("BeigeBrush");
        _bean = (Brush)FindResource("BeanBrush");
        _coffee = (Brush)FindResource("CoffeeLightBrush");
        _alert = (Brush)FindResource("AlertBrush");
        ApplyChrome(fullscreen: false);
        DayList.ItemsSource = _bands;
        VersionText.Text = "VERSION " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

        LoadSettingsIntoUi();
        _loading = false;
        InitPlayer();

        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            ReloadClips();
        };
        _engine.Settings.Changed += (old, now) =>
        {
            if (old.SaveFolder != now.SaveFolder) Dispatcher.BeginInvoke(() => { _shown = PageSize; WatchFolder(); ReloadClips(); });
        };
        WatchFolder();
        ReloadClips();
        RefreshStatus();
    }

    /// <summary>How many tiles fit across a band; set from the window width.</summary>
    public int TileColumns { get => (int)GetValue(TileColumnsProperty); set => SetValue(TileColumnsProperty, value); }

    // ================================================================ window frame

    /// <summary>
    /// The header takes the place of the Windows title bar: it drags the window, double-clicking it maximizes, and the
    /// minimize, maximize and close buttons are part of it. In full screen nothing drags and nothing resizes.
    /// </summary>
    private void ApplyChrome(bool fullscreen) => WindowChrome.SetWindowChrome(this, new WindowChrome
    {
        CaptionHeight = fullscreen ? 0 : CaptionHeightNow(),
        ResizeBorderThickness = fullscreen ? new Thickness(0) : new Thickness(GripSize),
        GlassFrameThickness = new Thickness(0, 0, 0, 1),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    });

    /// <summary>
    /// Windows counts the dragging part from the window's top edge, past the resize edge, so it ends at the header's
    /// bottom only with the resize edge taken off, and the part of a maximized window that is past the screen edge added.
    /// </summary>
    private double CaptionHeightNow() =>
        HeaderHeight - GripSize + (WindowState == WindowState.Maximized && !_fullscreen ? MaximizedInset().Top : 0);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            // Windows 11: round corners and a Beige outline, like other windows. Ignored on Windows 10.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int round = 2, border = 0x00AAC7DC; // DWMWCP_ROUND; COLORREF of #DCC7AA
            Win32.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            Win32.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch { }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateFrame();

    private void UpdateFrame()
    {
        if (!_fullscreen && WindowChrome.GetWindowChrome(this) is { } chrome && Math.Abs(chrome.CaptionHeight - CaptionHeightNow()) > 0.1)
        {
            if (chrome.IsFrozen) ApplyChrome(fullscreen: false);
            else chrome.CaptionHeight = CaptionHeightNow();
        }
        LayoutFrame(WindowState == WindowState.Maximized);
    }

    private void LayoutFrame(bool maximized)
    {
        Thickness inset = maximized && !_fullscreen ? MaximizedInset() : new Thickness(0);
        RootBorder.Padding = inset;

        // The minimize, maximize and close buttons reach out past the padding to the window's own edge (past the screen
        // edge when maximized), their glyphs staying where they were, so the very corner of the screen is Close.
        foreach (Panel panel in new Panel[] { CaptionButtons, PlayerCaptionButtons })
        {
            panel.Margin = new Thickness(0, -inset.Top, -inset.Right, 0);
            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not Button button) continue;
                bool last = i == panel.Children.Count - 1;
                button.Width = CaptionWidth + (last ? inset.Right : 0);
                button.Padding = new Thickness(0, inset.Top, last ? inset.Right : 0, 0);
            }
        }
        CaptionGrips.Visibility = PlayerCaptionGrips.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;

        string glyph = maximized ? "" : "";
        MaxButton.Content = PlayerMaxButton.Content = glyph;
        MaxButton.ToolTip = PlayerMaxButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    /// <summary>A maximized window reaches past the screen edges by its (now invisible) resize frame; this keeps the content on screen.</summary>
    private Thickness MaximizedInset()
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
            int pixels = Win32.GetSystemMetricsForDpi(32, dpiValue) + Win32.GetSystemMetricsForDpi(92, dpiValue); // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
            return new Thickness(pixels / dpi.DpiScaleX);
        }
        catch
        {
            return new Thickness(8);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================================================================ pages

    private void NavClips_Checked(object sender, RoutedEventArgs e) => ShowPage(settings: false);

    private void NavSettings_Checked(object sender, RoutedEventArgs e) => ShowPage(settings: true);

    private void ShowPage(bool settings)
    {
        if (ClipsPage == null || SettingsPage == null) return; // still loading
        ClipsPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (RecordingSection == null || VideoSection == null || AudioSection == null || GeneralSection == null) return; // still loading
        if (_wideSettings) return; // all four are showing
        RecordingSection.Visibility = SecRecording.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VideoSection.Visibility = SecVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = SecAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GeneralSection.Visibility = SecGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = SettingsPage.ActualWidth >= 1700 && SettingsPage.ActualHeight >= 900;
        if (wide == _wideSettings) return;
        _wideSettings = wide;
        LayoutSettings();
    }

    /// <summary>The panel each settings section sits on when all four show at once: a shade darker than the page.</summary>
    private static readonly Brush SettingsCardBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEB, 0xDA, 0xBD)));

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>
    /// A big window shows all four settings sections at once, two by two, each on its own panel under its own heading,
    /// with no list to pick from. A smaller one shows one section at a time, picked from the list on the left.
    /// </summary>
    private void LayoutSettings()
    {
        bool wide = _wideSettings;
        SettingsNavColumn.Width = new GridLength(wide ? 0 : 286);
        SettingsNav.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        WideSettingsTitle.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        SettingsBody.Margin = wide ? new Thickness(48, 36, 48, 36) : new Thickness(30, 30, 30, 24);
        SectionGapColumn.Width = new GridLength(wide ? 24 : 0);
        SectionRightColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SectionGapRow.Height = new GridLength(wide ? 24 : 0);
        Border[] cards = [RecordingCard, VideoCard, AudioCard, GeneralCard];
        StackPanel[] sections = [RecordingSection, VideoSection, AudioSection, GeneralSection];
        FrameworkElement[] heads = [RecordingHead, VideoHead, AudioHead, GeneralHead];
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetRow(cards[i], wide ? i / 2 * 2 : 0);
            Grid.SetColumn(cards[i], wide ? i % 2 * 2 : 0);
            Grid.SetColumnSpan(cards[i], wide ? 1 : 3);
            cards[i].Background = wide ? SettingsCardBrush : null;
            cards[i].Padding = wide ? new Thickness(36, 30, 36, 36) : new Thickness(0);
            heads[i].Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            if (wide) sections[i].Visibility = Visibility.Visible;
        }
        if (!wide) Section_Checked(this, new RoutedEventArgs());
    }

    private void PlayerNavSettings_Click(object sender, RoutedEventArgs e)
    {
        ClosePlayer();
        NavSettings.IsChecked = true;
    }

    // ================================================================ clips

    public void OnClipSaved() => QueueReload();

    private void QueueReload()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void WatchFolder()
    {
        string folder = _engine.Settings.Current.SaveFolder;
        if (folder == _watchedFolder && _watcher != null) return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedFolder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder, "*.mp4") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(QueueReload);
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(QueueReload);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Cannot watch the clips folder: " + ex.Message);
        }
    }

    private async void ReloadClips()
    {
        int version = ++_reloadVersion;
        string folder = _engine.Settings.Current.SaveFolder;
        List<FileInfo> files;
        try
        {
            files = await Task.Run(() => ClipLibrary.Scan(folder));
        }
        catch (Exception ex)
        {
            Log.Error("Scanning clips failed", ex);
            return;
        }
        if (version != _reloadVersion) return;

        var existing = new Dictionary<string, ClipItem>();
        foreach (ClipItem c in _clips) existing[c.Key] = c;
        var next = new List<ClipItem>();
        foreach (FileInfo f in files.Take(_shown))
        {
            string key = ClipItem.MakeKey(f);
            next.Add(existing.TryGetValue(key, out ClipItem? old) ? old : new ClipItem(f));
        }
        if (!next.SequenceEqual(_clips))
        {
            _clips.Clear();
            foreach (ClipItem c in next) _clips.Add(c);
        }

        // The newest clip leads the page; the rest are banded by day. A day's count includes every clip of that day.
        UpdateHero(next.FirstOrDefault());
        var counts = files.GroupBy(f => f.LastWriteTime.Date).ToDictionary(g => g.Key, g => g.Count());
        _days = next.Skip(1)
            .GroupBy(c => c.Modified.Date)
            .Select(g => new ClipDay(g.Key, counts.GetValueOrDefault(g.Key, g.Count()), g.ToList()))
            .ToList();
        RegroupBands();

        bool empty = files.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClipsScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShowMoreButton.Visibility = files.Count > _shown ? Visibility.Visible : Visibility.Collapsed;

        foreach (ClipItem c in next) RequestThumb(c);

        if (!_pruned)
        {
            _pruned = true;
            var keys = files.Select(ClipItem.MakeKey).ToHashSet();
            _ = Task.Run(() => ClipLibrary.PruneThumbs(keys));
        }
    }

    /// <summary>Shows the newest clip in the "Latest" panel and reads its length and audio tracks for it.</summary>
    private void UpdateHero(ClipItem? item)
    {
        if (item == null)
        {
            _heroKey = null;
            HeroFigure.DataContext = null;
            Hero.Visibility = Visibility.Collapsed;
            return;
        }
        Hero.Visibility = Visibility.Visible;
        HeroFigure.DataContext = item;
        HeroWhen.Text = item.Title;
        if (_heroKey == item.Key) return;
        _heroKey = item.Key;
        HeroPoster.Source = null;
        HeroFrames.Children.Clear();
        HeroFrames.Visibility = Visibility.Collapsed;
        HeroLength.Text = item.DurationText.Length > 0 ? item.DurationText : "—";
        HeroLengthWords.Text = "";
        HeroTrackCount.Text = "—";
        HeroTracks.Text = "";
        _ = DescribeHeroAsync(item, ++_heroVersion);
    }

    private async Task DescribeHeroAsync(ClipItem item, int version)
    {
        if (_engine.FfmpegPath is not { } ffmpeg) return;
        ClipInfo? info;
        try
        {
            info = await Task.Run(() => ClipAudio.Probe(ffmpeg, item.Path, _engine.Job));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the newest clip: " + ex.Message);
            return;
        }
        if (version != _heroVersion || info == null) return;
        if (info.Width > 0 && info.Height > 0 && Math.Abs(_heroAspect - (double)info.Width / info.Height) > 0.01)
        {
            _heroAspect = (double)info.Width / info.Height;
            LayoutClips();
        }
        HeroLength.Text = ClipLibrary.FormatDuration(info.Seconds);
        HeroLengthWords.Text = ClipLibrary.LengthInWords(info.Seconds);
        List<string> names = info.StemStreams.Select(i => TrackLabel(info.AudioTitles[i])).ToList();
        HeroTrackCount.Text = names.Count.ToString("00");
        HeroTracks.Text = names.Count switch
        {
            0 => "No audio",
            1 when !info.HasStems => "One mixed track",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " & " + names[^1],
        };
        HeroTracks.ToolTip = string.Join("\n", names);

        ImageSource? poster = await Task.Run(() => ClipLibrary.LoadPoster(ffmpeg, item.Path, item.Key, _engine.Job));
        if (version != _heroVersion) return;
        HeroPoster.Source = poster;

        // The same strip of frames the player's timeline shows, so opening the clip afterwards costs nothing more.
        ImageSource? strip = await Task.Run(() => ClipLibrary.LoadFilmstrip(ffmpeg, item.Path, item.Key, info.Seconds, _engine.Job));
        if (version == _heroVersion) BuildHeroFrames(item, strip as BitmapSource, info.Seconds);
    }

    /// <summary>Cuts the clip's filmstrip into its stills for the Latest panel. Each one plays the clip from its moment.</summary>
    private void BuildHeroFrames(ClipItem item, BitmapSource? strip, double seconds)
    {
        HeroFrames.Children.Clear();
        const int Frames = ClipLibrary.FilmstripFrames;
        int frameWidth = strip?.PixelWidth / Frames ?? 0, frameHeight = strip?.PixelHeight ?? 0;
        if (strip != null && frameWidth > 0 && frameHeight > 0 && seconds > 0)
        {
            _frameAspect = (double)frameWidth / frameHeight;
            for (int i = 0; i < Frames; i++)
            {
                double at = i * seconds / Frames;
                var still = new Button
                {
                    Style = (Style)FindResource("FrameButton"),
                    Content = new Image
                    {
                        Source = new CroppedBitmap(strip, new Int32Rect(i * frameWidth, 0, frameWidth, frameHeight)),
                        Stretch = Stretch.UniformToFill,
                    },
                    ToolTip = TimeSpan.FromSeconds(Math.Round(at)).ToString(@"m\:ss"),
                };
                RenderOptions.SetBitmapScalingMode(still, BitmapScalingMode.HighQuality);
                still.Click += (_, _) => OpenPlayer(item, at);
                HeroFrames.Children.Add(still);
            }
        }
        LayoutClips();
    }

    private void HeroWatch_Click(object sender, RoutedEventArgs e)
    {
        if (HeroFigure.DataContext is ClipItem item) OpenPlayer(item);
    }

    private void ClipsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0 || e.ExtentWidthChange != 0) LayoutClips();
    }

    /// <summary>Puts the days into bands for the current number of tiles across, keeping the bands shown if nothing changed.</summary>
    private void RegroupBands()
    {
        List<ClipBand> bands = ClipBand.Group(_days, TileColumns, _bandDays);
        if (bands.Count == _bands.Count && bands.Zip(_bands).All(p => p.First.SameAs(p.Second))) return;
        _bands.Clear();
        foreach (ClipBand band in bands) _bands.Add(band);
    }

    /// <summary>
    /// Sizes the Clips page to the window. Bands fit as many tiles across as leave each about 210 pixels wide. The newest
    /// clip shows uncropped at about 60% of the page's height, its details panel taking the rest of the width, with type
    /// that grows with the clip and, when there is room, two rows of stills from across the clip.
    /// </summary>
    private void LayoutClips()
    {
        double width = ClipsScroll.ViewportWidth > 0 ? ClipsScroll.ViewportWidth : ClipsScroll.ActualWidth;
        double height = ClipsScroll.ViewportHeight > 0 ? ClipsScroll.ViewportHeight : ClipsScroll.ActualHeight;
        if (width <= 0 || height <= 0) return;

        double bandWidth = Math.Max(240, width - DayLabelWidth - 2);
        int columns = Math.Clamp((int)Math.Floor(bandWidth / 210), 2, 8);
        double tileHeight = Math.Floor((bandWidth - (columns - 1) * BandTilesPanel.Gap) / columns * 9 / 16);
        int bandDays = tileHeight >= ThreeDayTileHeight ? 3 : 2;
        if (columns != TileColumns || bandDays != _bandDays)
        {
            TileColumns = columns;
            _bandDays = bandDays;
            RegroupBands();
        }

        double figureHeight = Math.Max(250, Math.Round(height * 0.6));
        double figureWidth = Math.Round(figureHeight * _heroAspect);
        if (width - figureWidth < HeroPanelMin)
        {
            figureWidth = Math.Max(160, width - HeroPanelMin);
            figureHeight = Math.Round(figureWidth / _heroAspect);
        }
        Hero.Height = figureHeight;
        HeroFigureColumn.Width = new GridLength(figureWidth);

        double panelWidth = width - figureWidth;
        double scale = Math.Clamp(Math.Min(figureHeight / 420, panelWidth / HeroPanelMin), 1, 1.45);
        HeroDetails.LayoutTransform = scale > 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
        HeroDivider.Width = 1 / scale; // stays one pixel wide
        HeroGlow.Width = HeroGlow.Height = Math.Round(260 * scale);
        HeroGlow.Margin = new Thickness(0, -70 * scale, -90 * scale, 0);
        HeroPlay.Width = HeroPlay.Height = Math.Round(58 * scale);
        double padSide = Math.Round(30 * scale), padTop = Math.Round(24 * scale);
        HeroDock.Margin = new Thickness(padSide, padTop, padSide, padSide);

        // Two rows of six stills under the details, as large as the room left there allows.
        double cellWidth = Math.Floor(Math.Min(panelWidth - 2 * padSide, 1240) / 6);
        double room = figureHeight - padTop - padSide - (HeroDetailsHeight + 28) * scale;
        double stillHeight = Math.Floor(Math.Min((cellWidth - 2) / _frameAspect, room / 2 - 2));
        bool stills = HeroFrames.Children.Count > 0 && _frameAspect >= 1.2 && stillHeight >= 56;
        HeroFrames.Visibility = stills ? Visibility.Visible : Visibility.Collapsed;
        if (stills)
        {
            double stillWidth = Math.Round(stillHeight * _frameAspect);
            foreach (UIElement child in HeroFrames.Children)
            {
                if (child is not FrameworkElement still) continue;
                still.Width = stillWidth;
                still.Height = stillHeight;
            }
        }
    }

    private async void RequestThumb(ClipItem item)
    {
        if (item.ThumbRequested || _engine.FfmpegPath is not { } ffmpeg) return;
        item.ThumbRequested = true;
        await _thumbGate.WaitAsync();
        try
        {
            var (image, seconds) = await Task.Run(() => ClipLibrary.LoadThumb(ffmpeg, item.Path, item.Key, _engine.Job));
            item.Thumbnail = image;
            item.DurationText = ClipLibrary.FormatDuration(seconds);
            if (item.Key == _heroKey && HeroLength.Text == "—" && item.DurationText.Length > 0) HeroLength.Text = item.DurationText;
        }
        catch (Exception ex)
        {
            Log.Warn("Thumbnail error: " + ex.Message);
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    private void ShowMore_Click(object sender, RoutedEventArgs e)
    {
        _shown += PageSize;
        ReloadClips();
    }

    private void SaveClip_Click(object sender, RoutedEventArgs e) => _app.SaveClip();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_engine.Settings.Current.SaveFolder);

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open folder: " + ex.Message);
        }
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show file: " + ex.Message);
        }
    }

    /// <summary>
    /// The tile the left button was pressed on. A tile opens its clip only when the button is both pressed and released
    /// on it, like a button. Opening on the release alone also opened a clip after a double-click on the title bar:
    /// Windows maximizes the window on the second press, the window grows under the mouse so a tile is now under it, and
    /// the release that follows goes to the window and lands on that tile.
    /// </summary>
    private object? _pressedTile;

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _pressedTile = sender;

    private void Tile_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_pressedTile == sender) _pressedTile = null;
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool pressedHere = _pressedTile == sender;
        _pressedTile = null;
        if (pressedHere && (sender as FrameworkElement)?.DataContext is ClipItem item) OpenPlayer(item);
    }

    private static ClipItem? MenuTarget(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement fe ? fe.DataContext as ClipItem : null;

    private void MenuPlay_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } item) OpenPlayer(item);
    }

    private void MenuOpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is not { } item) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the clip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } item) ShowInFolder(item.Path);
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } item) RenameClip(item);
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } item) DeleteClip(item);
    }

    private void RenameClip(ClipItem item)
    {
        string? name = Prompt("Rename clip", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "That name contains characters Windows does not allow in file names.", "Rename clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(item.Path)!, name + ".mp4");
        if (File.Exists(target))
        {
            MessageBox.Show(this, "A clip with that name already exists.", "Rename clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_playing == item) ClosePlayer();
        try
        {
            File.Move(item.Path, target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not rename the clip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        QueueReload();
    }

    private void DeleteClip(ClipItem item)
    {
        if (MessageBox.Show(this, $"Move \"{item.Name}\" to the Recycle Bin?", "Delete clip", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (_playing == item) ClosePlayer();
        try
        {
            FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not delete the clip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        QueueReload();
    }

    private string? Prompt(string title, string initial)
    {
        var label = new TextBlock { Text = title.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 12) };
        var box = new TextBox { Text = initial, Style = (Style)FindResource("Field"), Margin = new Thickness(0, 0, 0, 20) };
        var ok = new Button { Content = "RENAME", IsDefault = true, MinWidth = 120, Style = (Style)FindResource("CtaButton"), Padding = new Thickness(18, 0, 18, 0) };
        var cancel = new Button { Content = "CANCEL", IsCancel = true, Style = (Style)FindResource("NavLinkButton"), Margin = new Thickness(18, 0, 6, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = _cream,
            FontFamily = (FontFamily)FindResource("SerifFont"),
            Content = new Border { Padding = new Thickness(24, 22, 24, 22), Background = _cream, Child = stack },
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    // ================================================================ settings

    private static ListBoxItem Option(string text, object? tag, string? tip = null) => new() { Content = text, Tag = tag, ToolTip = tip };

    private static ComboBoxItem Item(string text, object? tag) => new() { Content = text, Tag = tag };

    private static void Select(Selector box, object? tag)
    {
        foreach (ListBoxItem i in box.Items.OfType<ListBoxItem>())
        {
            if (Equals(i.Tag, tag))
            {
                box.SelectedItem = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static object? SelectedTag(Selector box) => (box.SelectedItem as ListBoxItem)?.Tag;

    private static string LengthLabel(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        60 => "1 minute",
        _ when seconds % 60 == 0 => $"{seconds / 60} minutes",
        _ => $"{seconds / 60.0:0.#} minutes",
    };

    private static string LengthChoice(int seconds) => seconds < 60 ? $"{seconds}S" : ClipLibrary.FormatDuration(seconds);

    private void LoadSettingsIntoUi()
    {
        AppSettings s = _engine.Settings.Current;
        var display = DisplayInfo.GetPrimary();

        SaveFolderBox.Text = s.SaveFolder;
        SaveFolderBox.ToolTip = s.SaveFolder;

        LengthBox.Items.Clear();
        foreach (int n in AppSettings.ClipLengths) LengthBox.Items.Add(Option(LengthChoice(n), n, LengthLabel(n)));
        if (Array.IndexOf(AppSettings.ClipLengths, s.ClipSeconds) < 0) LengthBox.Items.Add(Option(LengthChoice(s.ClipSeconds), s.ClipSeconds, LengthLabel(s.ClipSeconds)));
        Select(LengthBox, s.ClipSeconds);

        QualityBox.Items.Clear();
        _tiers.Clear();
        foreach (string q in AppSettings.Qualities)
        {
            var tier = new QualityTier(q.ToUpperInvariant());
            _tiers[q] = tier;
            QualityBox.Items.Add(new ListBoxItem { Content = tier, DataContext = tier, Tag = q });
        }
        Select(QualityBox, s.Quality);

        ResolutionBox.Items.Clear();
        ResolutionBox.Items.Add(Option("NATIVE", 0, $"The screen's own size, {display.Width} × {display.Height}"));
        foreach (int h in new[] { 1440, 1080, 720 })
        {
            if (display.Height > h) ResolutionBox.Items.Add(Option($"{h}P", h, "Converted while saving, which takes a few seconds longer"));
        }
        Select(ResolutionBox, s.ClipHeight);

        FpsBox.Items.Clear();
        FpsBox.Items.Add(Option("60 FPS", 60));
        FpsBox.Items.Add(Option("30 FPS", 30));
        Select(FpsBox, s.Fps);

        CursorCheck.IsChecked = s.CaptureCursor;
        DesktopCheck.IsChecked = s.DesktopAudio;
        AppsCheck.IsChecked = s.SeparateAppAudio;
        MicCheck.IsChecked = s.Microphone;
        FillMicDevices(s.MicDeviceId);
        MicVolumeSlider.Value = Math.Min(200, s.MicVolume);
        MicVolumeText.Text = $"{s.MicVolume}%";

        StartupCheck.IsChecked = s.StartWithWindows;
        TrayCheck.IsChecked = s.CloseToTray;
        SoundCheck.IsChecked = s.PlaySound;
        ToastCheck.IsChecked = s.ShowToast;
        UpdateSoundRow(s);
        UpdateHints(s);
    }

    private void UpdateSoundRow(AppSettings s)
    {
        SoundNameText.Text = s.HotkeySoundName ?? "Built-in chime";
        SoundNameText.ToolTip = s.HotkeySoundName;
        DefaultSoundButton.Visibility = s.HotkeySoundName != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the hotkey sound",
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.oga;*.opus;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        ChooseSoundButton.IsEnabled = PreviewSoundButton.IsEnabled = DefaultSoundButton.IsEnabled = false;
        SoundNameText.Text = "Converting…";
        try
        {
            string? error = await _engine.SetHotkeySoundAsync(dialog.FileName);
            if (error != null)
                MessageBox.Show(this, $"\"{Path.GetFileName(dialog.FileName)}\" cannot be used as the hotkey sound.\n\n{error}",
                    "Hotkey sound", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                Sounds.Hotkey(); // let the user hear what they picked
        }
        catch (Exception ex)
        {
            Log.Error("Choosing the hotkey sound failed", ex);
        }
        finally
        {
            ChooseSoundButton.IsEnabled = PreviewSoundButton.IsEnabled = DefaultSoundButton.IsEnabled = true;
            UpdateSoundRow(_engine.Settings.Current);
        }
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e) => Sounds.Hotkey();

    private void DefaultSound_Click(object sender, RoutedEventArgs e)
    {
        _engine.ResetHotkeySound();
        UpdateSoundRow(_engine.Settings.Current);
    }

    private void FillMicDevices(string? selectedId)
    {
        MicDeviceBox.Items.Clear();
        MicDeviceBox.Items.Add(Item("Windows default microphone", null));
        bool found = selectedId == null;
        foreach (var (id, name) in AudioSource.ListMicrophones())
        {
            MicDeviceBox.Items.Add(Item(name, id));
            if (id == selectedId) found = true;
        }
        if (!found) MicDeviceBox.Items.Add(Item("Saved microphone (not connected)", selectedId));
        Select(MicDeviceBox, selectedId);
    }

    private void MicDeviceBox_DropDownOpened(object? sender, EventArgs e)
    {
        bool was = _loading;
        _loading = true;
        FillMicDevices(SelectedTag(MicDeviceBox) as string);
        _loading = was;
    }

    private void UpdateHints(AppSettings s)
    {
        var display = DisplayInfo.GetPrimary();
        foreach (var (quality, tier) in _tiers)
        {
            (_, int kbps) = AppSettings.VideoQuality(quality, display.Width, display.Height, s.Fps);
            tier.Rate = $"{kbps * 60 / 8 / 1000} MB/min";
        }
        QualityBox.ToolTip = "The most a minute of clip can take. Calm scenes use far less.";
        EmptyHint.Text = $"Press {s.HotkeyText} to save the last {LengthLabel(s.ClipSeconds)}.";
        SaveClipButton.ToolTip = $"Save the last {LengthLabel(s.ClipSeconds)} ({s.HotkeyText})";
        UpdateHotkeyDisplay();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => Commit();

    private void MicVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || MicVolumeText == null) return;
        MicVolumeText.Text = $"{(int)Math.Round(e.NewValue / 5) * 5}%";
        Commit();
    }

    private void Commit()
    {
        if (_loading) return;
        AppSettings s = _engine.Settings.Current;
        if (SelectedTag(LengthBox) is int len) s.ClipSeconds = len;
        if (SelectedTag(QualityBox) is string q) s.Quality = q;
        if (SelectedTag(ResolutionBox) is int h) s.ClipHeight = h;
        if (SelectedTag(FpsBox) is int fps) s.Fps = fps;
        if (MicDeviceBox.SelectedItem is ComboBoxItem mic) s.MicDeviceId = mic.Tag as string;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.DesktopAudio = DesktopCheck.IsChecked == true;
        s.SeparateAppAudio = AppsCheck.IsChecked == true;
        s.Microphone = MicCheck.IsChecked == true;
        s.MicVolume = (int)Math.Round(MicVolumeSlider.Value / 5) * 5;
        s.StartWithWindows = StartupCheck.IsChecked == true;
        s.CloseToTray = TrayCheck.IsChecked == true;
        s.PlaySound = SoundCheck.IsChecked == true;
        s.ShowToast = ToastCheck.IsChecked == true;
        _engine.Settings.Save(s);
        UpdateHints(s);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        AppSettings s = _engine.Settings.Current;
        var dialog = new OpenFolderDialog { Title = "Choose where clips are saved" };
        try
        {
            if (Directory.Exists(s.SaveFolder)) dialog.InitialDirectory = s.SaveFolder;
        }
        catch { }
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Make sure the folder really is writable before accepting it.
            string probe = Path.Combine(dialog.FolderName, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Clips cannot be saved to that folder.\n\n" + ex.Message, "Save folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        s.SaveFolder = dialog.FolderName;
        SaveFolderBox.Text = s.SaveFolder;
        SaveFolderBox.ToolTip = s.SaveFolder;
        _engine.Settings.Save(s);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogDir);

    // ---------------------------------------------------------------- hotkey capture

    /// <summary>Shows the hotkey as keys in the Espresso block, or a prompt while new keys are being captured.</summary>
    private void UpdateHotkeyDisplay()
    {
        HotkeyKeys.Children.Clear();
        if (_capturingHotkey)
        {
            HotkeyKeys.Children.Add(KeyText("PRESS THE KEYS", 12));
            HotkeyButton.Content = "ESC CANCELS";
            return;
        }
        AppSettings s = _engine.Settings.Current;
        List<string> keys = AppSettings.HotkeyParts(s.HotkeyModifiers, s.HotkeyKey);
        if (keys.Count == 0) HotkeyKeys.Children.Add(KeyText("NONE", 12));
        for (int i = 0; i < keys.Count; i++)
        {
            // Each key keeps its "+" with it, so a hotkey that wraps never starts a line with one.
            var group = new StackPanel { Orientation = Orientation.Horizontal };
            group.Children.Add(new Border
            {
                BorderBrush = _cream, BorderThickness = new Thickness(1.3), MinWidth = 44, Height = 42, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = keys[i].ToUpperInvariant(), FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 19, Foreground = _white,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            if (i < keys.Count - 1) group.Children.Add(KeyText("+", 12));
            HotkeyKeys.Children.Add(group);
        }
        HotkeyButton.Content = "CHANGE";
    }

    private TextBlock KeyText(string text, double size) => new()
    {
        Text = text, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = size, Foreground = _cream,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 6),
    };

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) return;
        _capturingHotkey = true;
        _hotkeyMessage = "";
        _app.SuspendHotkey();
        UpdateHotkeyDisplay();
        HotkeyButton.Focus();
    }

    private void EndHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        UpdateHotkeyDisplay();
        _app.ApplyHotkey();
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndHotkeyCapture();

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.DeadCharProcessed)
            return; // wait for the real key

        ModifierKeys m = Keyboard.Modifiers;
        if (key == Key.Escape && m == ModifierKeys.None)
        {
            EndHotkeyCapture();
            return;
        }
        uint mods = 0;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (m.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= 8;

        bool standalone = key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Multiply or Key.Add or Key.Subtract or Key.Divide;
        if (mods == 0 && !standalone)
        {
            _hotkeyMessage = "Hold Ctrl, Alt or Shift together with that key.";
            return;
        }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        AppSettings s = _engine.Settings.Current;
        s.HotkeyModifiers = mods;
        s.HotkeyKey = vk;
        _engine.Settings.Save(s);
        _hotkeyMessage = "";
        EndHotkeyCapture();
        UpdateHints(s);
    }

    // ================================================================ status

    private static double MeterLevel(float peak, double previous)
    {
        double level = peak <= 0.0001f ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        return Math.Max(level, previous * 0.8);
    }

    private void RefreshStatus()
    {
        EngineStatus st = _engine.GetStatus();
        AppSettings s = _engine.Settings.Current;
        bool recording = st.State == RecorderState.Recording;

        (string text, Brush dot) = st.State switch
        {
            RecorderState.Recording => ("RECORDING", _white),
            RecorderState.Starting => ("STARTING", _beige),
            RecorderState.Retrying => ("RECONNECTING TO THE SCREEN", _beige),
            RecorderState.Paused => ("PAUSED", _beige),
            RecorderState.Error => ("NOT RECORDING", _alert),
            _ => ("STOPPED", _coffee),
        };
        StatusText.Text = text;
        StatusDot.Fill = dot;
        StatusDetail.Text = recording
            ? st.Config is { Encoder: not "h264_nvenc" } ? "CPU ENCODER: THE GPU ENCODER IS NOT AVAILABLE" : ""
            : (st.Detail ?? "").ToUpperInvariant();
        ReadyText.Text = !recording ? ""
            : st.BufferedSeconds >= s.ClipSeconds ? $"LAST {ClipLibrary.FormatDuration(s.ClipSeconds)} READY"
            : $"BUFFERING {st.BufferedSeconds} OF {s.ClipSeconds} S";

        // Audio only shows here when something is wrong with it.
        var problems = new List<string>();
        if (st.DesktopEnabled && st.DesktopStatus is not ("OK" or "Starting")) problems.Add("DESKTOP AUDIO: " + st.DesktopStatus.ToUpperInvariant());
        if (st.MicEnabled && st.MicStatus is not ("OK" or "Starting")) problems.Add("MICROPHONE: " + st.MicStatus.ToUpperInvariant());
        AudioStatusText.Text = string.Join("   ", problems);

        // The stamp in the side rail is the recording light: solid while recording, an outline otherwise.
        Hanko.Background = recording ? _bean : Brushes.Transparent;
        HankoText.Foreground = recording ? _cream : _bean;
        HankoRing.Opacity = recording ? 0.18 : 0;
        PlayerHanko.Opacity = recording ? 1 : 0.35;
        PlayerHankoRing.Opacity = recording ? 0.14 : 0;
        Hanko.ToolTip = PlayerHanko.ToolTip = recording ? "Recording" : text[0] + text[1..].ToLowerInvariant();

        if (IsVisible)
        {
            _desktopLevel = MeterLevel(_engine.Desktop.TakePeak(), _desktopLevel);
            _micLevel = MeterLevel(_engine.Mic.TakePeak(), _micLevel);
            DesktopMeter.Value = st.DesktopEnabled ? _desktopLevel : 0;
            MicMeter.Value = st.MicEnabled ? _micLevel : 0;
            DesktopInfo.Text = !st.DesktopEnabled ? "" : st.DesktopStatus == "OK" ? st.DesktopDevice : st.DesktopStatus;
            MicInfo.Text = !st.MicEnabled ? "" : st.MicStatus == "OK" ? st.MicDevice : st.MicStatus;
            AppsCheck.IsEnabled = st.DesktopEnabled && AppAudioCapture.Supported;
            if (!AppAudioCapture.Supported) AppsInfo.Text = "Needs Windows 10 version 2004 or newer.";
            else if (!st.DesktopEnabled || !s.SeparateAppAudio) AppsInfo.Text = "";
            else
            {
                List<string> heard = _engine.Apps.RecentlyHeard(30);
                AppsInfo.Text = heard.Count > 0 ? "Now: " + string.Join(", ", heard) : "";
            }

            string message = _capturingHotkey ? _hotkeyMessage
                : _hotkeyMessage.Length > 0 ? _hotkeyMessage
                : _app.HotkeyOk ? "" : "Another program is already using this shortcut. Pick a different one.";
            HotkeyStatus.Text = message;
            HotkeyStatus.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ================================================================ window lifetime

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shuttingDown && !_app.IsExiting)
        {
            e.Cancel = true;
            ClosePlayer();
            if (_engine.Settings.Current.CloseToTray)
            {
                Hide();
                _app.ShowTrayHint();
            }
            else
            {
                Dispatcher.BeginInvoke(_app.ExitApp);
            }
        }
        base.OnClosing(e);
    }

    /// <summary>Really closes the window (used when the app exits).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        _statusTimer.Stop();
        _reloadTimer.Stop();
        _watcher?.Dispose();
        ClosePlayer();
        Close();
    }

    // ================================================================ diagnostics

    public void ShowSettingsTab() => NavSettings.IsChecked = true;

    public void ScrollClips(bool toEnd)
    {
        if (toEnd) ClipsScroll.ScrollToEnd();
        else ClipsScroll.ScrollToHome();
        ClipsScroll.UpdateLayout();
    }

    public void ShowSettingsSection(int index) => (index switch { 1 => SecVideo, 2 => SecAudio, 3 => SecGeneral, _ => SecRecording }).IsChecked = true;

    /// <summary>
    /// Gives the newest clip (the "Latest" panel, right under the header) the mouse events it gets after a double-click
    /// on the header has maximized the window (a release without a press: the press went to the header), then a normal
    /// click, then a press that leaves it before the release. Reports which of them opened the clip.
    /// </summary>
    public string TitleBarCheck()
    {
        if (FindTile(ClipsPage) is not { } tile)
            return "no clip to check";

        bool Opens(params RoutedEvent[] events)
        {
            foreach (RoutedEvent routed in events)
            {
                tile.RaiseEvent(routed == MouseLeaveEvent
                    ? new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routed }
                    : new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed });
            }
            bool opened = _playing != null;
            if (opened) ClosePlayer();
            return opened;
        }

        bool release = Opens(MouseLeftButtonUpEvent);
        bool click = Opens(MouseLeftButtonDownEvent, MouseLeftButtonUpEvent);
        bool dragOff = Opens(MouseLeftButtonDownEvent, MouseLeaveEvent, MouseLeftButtonUpEvent);
        return $"release left over from a title-bar double-click opened the clip: {(release ? "YES (wrong)" : "no")}; " +
               $"a click opened it: {(click ? "yes" : "NO (wrong)")}; a press that left the tile opened it: {(dragOff ? "YES (wrong)" : "no")}";
    }

    /// <summary>
    /// Lays the window out as if maximized, then as it normally is, and reports what the mouse would be on at the top-right
    /// corner of the screen and just inside it. Windows sends a click to whatever app element is there when that element
    /// opts out of the title bar, as the buttons do; a resize grip there resizes the window instead.
    /// </summary>
    public string CaptionCheck()
    {
        string What(Point p)
        {
            if (InputHitTest(p) is not DependencyObject hit) return "nothing";
            for (DependencyObject? d = hit; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is Button b && WindowChrome.GetIsHitTestVisibleInChrome(b))
                    return b == CloseButton ? "Close" : b == MaxButton ? "Maximize" : b.ToolTip as string ?? "a button";
                if (d is FrameworkElement fe && WindowChrome.GetResizeGripDirection(fe) is var grip and not ResizeGripDirection.None)
                    return "resize " + grip;
            }
            return "the title bar";
        }

        var parts = new List<string>();
        try
        {
            foreach (bool maximized in new[] { true, false })
            {
                LayoutFrame(maximized);
                UpdateLayout();
                Thickness inset = RootBorder.Padding;
                double w = ActualWidth;
                // Maximized, the screen's corner pixel is inset in from the window's; the window's own corner is off screen.
                var corner = new Point(w - inset.Right - 0.5, inset.Top + 0.5);
                var below = new Point(w - inset.Right - 0.5, inset.Top + HeaderHeight - 2);
                var left = new Point(w - inset.Right - CaptionWidth * 3 + 2, inset.Top + 0.5);
                var x = CloseButton.Template.FindName("Glyph", CloseButton) as FrameworkElement ?? CloseButton;
                var glyph = x.TranslatePoint(new Point(x.ActualWidth / 2, x.ActualHeight / 2), this);
                parts.Add($"{(maximized ? "maximized" : "normal")} (inset {inset.Top:0}): screen corner = {What(corner)}, " +
                          $"right edge at the header's bottom = {What(below)}, top edge at Minimize = {What(left)}, " +
                          $"window corner = {What(new Point(w - 0.5, 0.5))}, close glyph centre {glyph.X - inset.Left:0},{glyph.Y - inset.Top:0}");
            }
        }
        finally
        {
            LayoutFrame(WindowState == WindowState.Maximized);
        }
        return string.Join("; ", parts);
    }

    /// <summary>Groups made-up days into bands (8 across, up to 3 days a band) and reports the bands, for the rules about weeks and room.</summary>
    public static string BandCheck()
    {
        var clip = new ClipItem(new FileInfo(Environment.ProcessPath!));
        ClipDay Day(int month, int day, int clips) => new(new DateTime(2026, month, day), clips, Enumerable.Repeat(clip, clips).ToList());
        string Bands(params ClipDay[] days) =>
            string.Join(" | ", ClipBand.Group(days, 8, 3).Select(b => string.Join(" + ", b.Days.Select(d => $"{d.Day:ddd dd}"))));
        return $"Wed+Tue+Mon one clip each: {Bands(Day(11, 18, 1), Day(11, 17, 1), Day(11, 16, 1))}; " +
               $"Mon, Sun, Sat (Sun starts a week here: {System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek}): {Bands(Day(11, 16, 1), Day(11, 15, 1), Day(11, 14, 1))}; " +
               $"Mon then the Friday before: {Bands(Day(11, 16, 1), Day(11, 13, 1))}; " +
               $"4 and 4 clips: {Bands(Day(11, 18, 4), Day(11, 17, 4))}; 5 and 4: {Bands(Day(11, 18, 5), Day(11, 17, 4))}";
    }

    /// <summary>How the Clips page is laid out at the window's size.</summary>
    public string LayoutCheck()
    {
        double shown = HeroFigure.ActualWidth / Math.Max(1, HeroFigure.ActualHeight);
        string stills = HeroFrames.Visibility == Visibility.Visible ? $"{HeroFrames.ActualWidth:0}x{HeroFrames.ActualHeight:0}" : "hidden";
        string bands = string.Join(" | ", _bands.Select(b => string.Join(" + ", b.Days.Select(d => $"{d.Date} ({d.Clips.Count})"))));
        return $"{TileColumns} across, up to {_bandDays} days a band: {bands}; latest clip {HeroFigure.ActualWidth:0}x{HeroFigure.ActualHeight:0} " +
               $"(clip shape {_heroAspect:0.###}, shown {shown:0.###}); details {HeroDetails.ActualHeight:0} px tall unscaled, " +
               $"x{(HeroDetails.LayoutTransform as ScaleTransform)?.ScaleX ?? 1:0.##}, in a {HeroDock.ActualWidth:0}x{HeroDock.ActualHeight:0} panel; " +
               $"stills {stills} ({HeroFrames.Children.Count})";
    }

    /// <summary>Clicks the seventh still in the Latest panel and reports where the clip then plays from.</summary>
    public async Task<string> StillCheckAsync()
    {
        const int Index = 6;
        if (HeroFrames.Children.Count <= Index || HeroFrames.Children[Index] is not Button still) return "no stills to click";
        still.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(2000);
        double expected = Index * VideoDuration / ClipLibrary.FilmstripFrames;
        string result = $"still {Index + 1} of {HeroFrames.Children.Count} should start the clip at {expected:0.00}s; 2 s after the click " +
                        $"the video is at {Video.Position:0.00}s (open={_playing != null}, playing={_isPlaying})";
        ClosePlayerForTest();
        return result;
    }

    /// <summary>Which settings layout shows, and where each section is.</summary>
    public string SettingsCheck()
    {
        StackPanel[] sections = [RecordingSection, VideoSection, AudioSection, GeneralSection];
        return $"page {SettingsPage.ActualWidth:0}x{SettingsPage.ActualHeight:0}, {(_wideSettings ? "all sections" : "one section")}: " +
               string.Join(", ", sections.Select(s => s.IsVisible && s.Parent is Border card
                   ? $"{card.Name} at {card.TranslatePoint(new Point(), SettingsPage).X:0},{card.TranslatePoint(new Point(), SettingsPage).Y:0} {card.ActualWidth:0}x{card.ActualHeight:0}"
                   : $"{s.Name} hidden"));
    }

    private static FrameworkElement? FindTile(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border { ContextMenu: not null, DataContext: ClipItem } tile) return tile;
            if (FindTile(child) is { } found) return found;
        }
        return null;
    }

    public void RenderTo(string file)
    {
        var root = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream fs = File.Create(file);
        encoder.Save(fs);
    }

    /// <summary>
    /// Compares the window with its client area. The header replaces the Windows title bar only if they are the same
    /// size: otherwise Windows still draws a frame or title bar of its own around the app's content.
    /// </summary>
    public string ChromeCheck()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!Win32.GetWindowRect(hwnd, out Win32.RECT window) || !Win32.GetClientRect(hwnd, out Win32.RECT client)) return "could not read the window";
        bool same = window.Width == client.Width && window.Height == client.Height;
        return $"window {window.Width}x{window.Height}, client area {client.Width}x{client.Height}: " +
               (same ? "the header is the title bar" : "WINDOWS DRAWS ITS OWN FRAME (wrong)");
    }
}
