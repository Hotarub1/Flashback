using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Flashback.Core;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Flashback;

public partial class MainWindow : Window
{
    private const int PageSize = 60;

    private readonly Engine _engine;
    private readonly App _app;
    private readonly ObservableCollection<ClipItem> _clips = new();
    private readonly SemaphoreSlim _thumbGate = new(3, 3);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private FileSystemWatcher? _watcher;
    private string _watchedFolder = "";
    private int _shown = PageSize;
    private int _reloadVersion;
    private bool _pruned;

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
        ClipList.ItemsSource = _clips;
        VersionText.Text = "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

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

        ClipCountText.Text = files.Count switch
        {
            0 => "",
            1 => "1 clip  ·  " + ClipLibrary.FormatSize(files[0].Length),
            _ => $"{files.Count} clips  ·  {ClipLibrary.FormatSize(files.Sum(f => f.Length))}",
        };
        EmptyState.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreButton.Visibility = files.Count > _shown ? Visibility.Visible : Visibility.Collapsed;

        foreach (ClipItem c in next) RequestThumb(c);

        if (!_pruned)
        {
            _pruned = true;
            var keys = files.Select(ClipItem.MakeKey).ToHashSet();
            _ = Task.Run(() => ClipLibrary.PruneThumbs(keys));
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

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) OpenPlayer(item);
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
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 16) };
        var ok = new Button { Content = "Rename", IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        ok.Style = (Style)FindResource("PrimaryButton");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var stack = new StackPanel();
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(20), Background = (Brush)FindResource("AppBackground"), Child = stack },
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

    private static ComboBoxItem Item(string text, object? tag) => new() { Content = text, Tag = tag };

    private static void Select(ComboBox box, object? tag)
    {
        foreach (ComboBoxItem i in box.Items)
        {
            if (Equals(i.Tag, tag))
            {
                box.SelectedItem = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string LengthLabel(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        60 => "1 minute",
        _ when seconds % 60 == 0 => $"{seconds / 60} minutes",
        _ => $"{seconds / 60.0:0.#} minutes",
    };

    private void LoadSettingsIntoUi()
    {
        AppSettings s = _engine.Settings.Current;
        var display = DisplayInfo.GetPrimary();

        SaveFolderBox.Text = s.SaveFolder;
        HotkeyButton.Content = s.HotkeyText;

        LengthBox.Items.Clear();
        foreach (int n in AppSettings.ClipLengths) LengthBox.Items.Add(Item(LengthLabel(n), n));
        if (Array.IndexOf(AppSettings.ClipLengths, s.ClipSeconds) < 0) LengthBox.Items.Add(Item(LengthLabel(s.ClipSeconds), s.ClipSeconds));
        Select(LengthBox, s.ClipSeconds);

        QualityBox.Items.Clear();
        foreach (string q in AppSettings.Qualities) QualityBox.Items.Add(Item(q, q));
        Select(QualityBox, s.Quality);

        ResolutionBox.Items.Clear();
        ResolutionBox.Items.Add(Item($"Native ({display.Width} × {display.Height})", 0));
        foreach (int h in new[] { 1440, 1080, 720 })
        {
            if (display.Height > h) ResolutionBox.Items.Add(Item($"{h}p", h));
        }
        Select(ResolutionBox, s.ClipHeight);

        FpsBox.Items.Clear();
        FpsBox.Items.Add(Item("60 fps", 60));
        FpsBox.Items.Add(Item("30 fps", 30));
        Select(FpsBox, s.Fps);

        CursorCheck.IsChecked = s.CaptureCursor;
        DesktopCheck.IsChecked = s.DesktopAudio;
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
        FillMicDevices((MicDeviceBox.SelectedItem as ComboBoxItem)?.Tag as string);
        _loading = was;
    }

    private void UpdateHints(AppSettings s)
    {
        var display = DisplayInfo.GetPrimary();
        (_, int kbps) = AppSettings.VideoQuality(s.Quality, display.Width, display.Height, s.Fps);
        QualityHint.Text = $"Up to about {kbps * 60 / 8 / 1000} MB per minute of clip. Calm scenes use far less.";
        BufferHint.Text = $"The last {s.ClipSeconds + 15} seconds are kept in a temporary buffer on disk (at most about {(s.ClipSeconds + 15) * (long)kbps / 8 / 1000} MB). " +
                          "It is erased when the app closes.";
        EmptyHint.Text = $"Press {s.HotkeyText} to save the last {LengthLabel(s.ClipSeconds)}.";
        SaveClipText.Text = $"Save clip  ({s.HotkeyText})";
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
        if ((LengthBox.SelectedItem as ComboBoxItem)?.Tag is int len) s.ClipSeconds = len;
        if ((QualityBox.SelectedItem as ComboBoxItem)?.Tag is string q) s.Quality = q;
        if ((ResolutionBox.SelectedItem as ComboBoxItem)?.Tag is int h) s.ClipHeight = h;
        if ((FpsBox.SelectedItem as ComboBoxItem)?.Tag is int fps) s.Fps = fps;
        if (MicDeviceBox.SelectedItem is ComboBoxItem mic) s.MicDeviceId = mic.Tag as string;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.DesktopAudio = DesktopCheck.IsChecked == true;
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
        _engine.Settings.Save(s);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogDir);

    // ---------------------------------------------------------------- hotkey capture

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) return;
        _capturingHotkey = true;
        _hotkeyMessage = "";
        _app.SuspendHotkey();
        HotkeyButton.Content = "Press the keys…  (Esc cancels)";
        HotkeyButton.Focus();
    }

    private void EndHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyButton.Content = _engine.Settings.Current.HotkeyText;
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

        (string text, string brush) = st.State switch
        {
            RecorderState.Recording => ("Recording", "Good"),
            RecorderState.Starting => ("Starting…", "Warn"),
            RecorderState.Retrying => ("Reconnecting to the screen…", "Warn"),
            RecorderState.Paused => ("Paused", "Warn"),
            RecorderState.Error => ("Not recording", "Bad"),
            _ => ("Stopped", "TextMuted"),
        };
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(brush);
        if (st.State == RecorderState.Recording && st.Config is { } c)
        {
            string encoder = c.Encoder == "h264_nvenc" ? "GPU encoder" : "CPU encoder (GPU encoder unavailable, uses more CPU)";
            string ready = st.BufferedSeconds >= s.ClipSeconds
                ? $"last {LengthLabel(s.ClipSeconds)} ready"
                : $"buffering, {st.BufferedSeconds} of {s.ClipSeconds} seconds so far";
            StatusDetail.Text = $"Main monitor  ·  {c.Width} × {c.Height}  ·  {c.Fps} fps  ·  {encoder}  ·  {ready}";
        }
        else StatusDetail.Text = st.Detail;

        string Describe(string name, bool enabled, string status, string device) =>
            !enabled ? $"{name}: off" : status == "OK" ? $"{name}: on" : $"{name}: {status}";
        AudioStatusText.Text = Describe("Desktop audio", st.DesktopEnabled, st.DesktopStatus, st.DesktopDevice) + "     "
                               + Describe("Microphone", st.MicEnabled, st.MicStatus, st.MicDevice);
        AudioStatusText.Foreground = (Brush)FindResource(
            (st.DesktopEnabled && st.DesktopStatus is not ("OK" or "Starting")) || (st.MicEnabled && st.MicStatus is not ("OK" or "Starting")) ? "Warn" : "TextMuted");

        if (IsVisible)
        {
            _desktopLevel = MeterLevel(_engine.Desktop.TakePeak(), _desktopLevel);
            _micLevel = MeterLevel(_engine.Mic.TakePeak(), _micLevel);
            DesktopMeter.Value = st.DesktopEnabled ? _desktopLevel : 0;
            MicMeter.Value = st.MicEnabled ? _micLevel : 0;
            DesktopInfo.Text = !st.DesktopEnabled ? "" : st.DesktopStatus == "OK" ? st.DesktopDevice : st.DesktopStatus;
            MicInfo.Text = !st.MicEnabled ? "" : st.MicStatus == "OK" ? st.MicDevice : st.MicStatus;

            if (!_capturingHotkey)
            {
                bool ok = _app.HotkeyOk;
                HotkeyStatus.Text = _hotkeyMessage.Length > 0 ? _hotkeyMessage
                    : ok ? "Works everywhere, including in games."
                    : "Another program is already using this shortcut. Pick a different one.";
                HotkeyStatus.Foreground = (Brush)FindResource(ok && _hotkeyMessage.Length == 0 ? "TextMuted" : "Warn");
            }
            else
            {
                HotkeyStatus.Text = _hotkeyMessage;
                HotkeyStatus.Foreground = (Brush)FindResource("Warn");
            }
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

    public void ShowSettingsTab() => Tabs.SelectedItem = SettingsTab;

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
}
