using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Flashback.Core;

namespace Flashback;

/// <summary>
/// The clip player: video through a MediaElement, audio through <see cref="StemPlayer"/> so desktop audio and the
/// microphone can be balanced while watching, a timeline for trimming, and full-screen viewing. Trimmed copies are
/// written by <see cref="ClipEditor"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Longer clips play with their mixed audio only: ten minutes of decoded tracks already take about 230 MB.</summary>
    private const int MaxStemSeconds = 600;
    private static readonly Color DesktopColor = Color.FromRgb(0x2D, 0xD4, 0xBF);
    private static readonly Color MicColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Brush BarBackground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D));
    private static readonly Brush BarOverlay = new SolidColorBrush(Color.FromArgb(0xD0, 0x0B, 0x0B, 0x0D));

    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _hideControls = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private ClipItem? _playing;
    private bool _isPlaying, _dragging, _updatingSeek, _muted, _exporting, _fullscreen, _resettingEditor;
    private int _openVersion;
    private ClipInfo? _info;
    private StemPlayer? _stemPlayer;
    private double _lastVideoPos = -1, _prevTickPos;
    private long _lastVideoAdvance;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;

    /// <summary>Set by diagnostics (--render-ui) so the check plays nothing through the speakers.</summary>
    public bool PlayerSilent { get; set; }

    private void InitPlayer()
    {
        _playerTimer.Tick += (_, _) => RefreshPlayerPosition();
        _hideControls.Tick += (_, _) =>
        {
            _hideControls.Stop();
            if (!_fullscreen) return;
            ControlBar.Visibility = Visibility.Hidden;
            PlayerSurface.Cursor = Cursors.None;
        };
        Timeline.SeekRequested += SeekTo;
        Timeline.ScrubStarted += () => _dragging = true;
        Timeline.ScrubEnded += () => _dragging = false;
        Timeline.TrimChanged += () =>
        {
            UpdateTrimInfo();
            UpdateEditorButtons();
        };
    }

    // ================================================================ open and close

    private void OpenPlayer(ClipItem item)
    {
        if (!File.Exists(item.Path))
        {
            QueueReload();
            return;
        }
        ClosePlayer();
        int version = ++_openVersion;
        _playing = item;
        PlayerTitle.Text = item.Name;
        PlayerError.Visibility = Visibility.Collapsed;
        PlayerOverlay.Visibility = Visibility.Visible;
        ResetEditor();
        _updatingSeek = true;
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;
        _updatingSeek = false;
        PlayerTime.Text = "0:00";
        PlayerTotal.Text = item.DurationText.Length > 0 ? item.DurationText : "0:00";
        _lastVideoPos = -1;
        _prevTickPos = 0;
        try
        {
            // The clip's mixed track plays until its separate tracks are decoded, then those take over.
            Player.IsMuted = _muted || PlayerSilent;
            Player.Volume = 1;
            Player.Source = new Uri(item.Path);
            Player.Play();
            _isPlaying = true;
        }
        catch (Exception ex)
        {
            ShowPlayerError(ex.Message);
        }
        UpdatePlayGlyph();
        _playerTimer.Start();
        PlayPauseButton.Focus();
        _ = LoadAudioAsync(item, version);
    }

    private void ClosePlayer()
    {
        _playerTimer.Stop();
        _openVersion++;
        if (_playing == null && PlayerOverlay.Visibility != Visibility.Visible) return;
        SetFullscreen(false);
        _stemPlayer?.Dispose();
        _stemPlayer = null;
        _info = null;
        try
        {
            Player.Stop();
            Player.Close();   // releases the file so it can be renamed or deleted
            Player.Source = null;
        }
        catch { }
        _playing = null;
        _isPlaying = false;
        PlayerOverlay.Visibility = Visibility.Collapsed;
        Timeline.SetTracks([]);
    }

    private void ResetEditor()
    {
        _resettingEditor = true;
        Timeline.SetTracks([]);
        Timeline.Duration = 0;
        Timeline.Position = 0;
        Timeline.TrimStart = 0;
        Timeline.TrimEnd = 0;
        TrackASlider.Value = 100;
        TrackBSlider.Value = 100;
        TrackASlider.IsEnabled = true;
        TrackALabel.Text = "Audio";
        TrackBRow.Visibility = Visibility.Collapsed;
        _resettingEditor = false;
        SetAudioNote("");
        ExportProgress.Visibility = Visibility.Collapsed;
        ExportProgress.Value = 0;
        ApplyGains();
        UpdateTrimInfo();
        UpdateEditorButtons();
    }

    /// <summary>Reads what the clip contains, then decodes its audio tracks and hands them to the stem player.</summary>
    private async Task LoadAudioAsync(ClipItem item, int version)
    {
        if (_engine.FfmpegPath is not { } ffmpeg)
        {
            SetAudioNote("Volume and trimming need ffmpeg.exe next to the app.");
            return;
        }
        try
        {
            ClipInfo? info = await Task.Run(() => ClipAudio.Probe(ffmpeg, item.Path, _engine.Job));
            if (version != _openVersion) return;
            if (info == null)
            {
                SetAudioNote("This clip could not be read.");
                return;
            }
            _info = info;
            if (info.Seconds > 0) ApplyDuration(info.Seconds);
            ShowTrackRows(info, loading: true);
            UpdateEditorButtons();
            if (info.AudioStreams == 0)
            {
                SetAudioNote("This clip has no audio.");
                Timeline.SetTracks([]);
                TrackALabel.Text = "No audio";
                TrackASlider.IsEnabled = false;
                return;
            }
            if (info.Seconds > MaxStemSeconds)
            {
                SetAudioNote("Clips longer than 10 minutes play with their mixed audio.");
                Timeline.SetTracks([]);
                return;
            }

            ClipStems stems = await Task.Run(() => ClipAudio.Decode(ffmpeg, item.Path, info, MaxStemSeconds, _engine.Job));
            if (version != _openVersion) return;
            ShowTrackRows(info, loading: false, stems);

            StemPlayer? player = StemPlayer.Create(stems);
            if (player == null)
            {
                SetAudioNote("No audio output device; playing the clip's mixed audio.");
                return;
            }
            player.Failed += message =>
            {
                if (version != _openVersion) return;
                Log.Warn("Clip audio output stopped: " + message);
                _stemPlayer?.Dispose();
                _stemPlayer = null;
                Player.IsMuted = _muted || PlayerSilent;
                ApplyGains();
                SetAudioNote("Audio output stopped; playing the clip's mixed audio.");
            };
            _stemPlayer = player;
            player.Muted = _muted || PlayerSilent;
            Player.IsMuted = true;
            _lastVideoPos = -1; // line the audio up with the picture on the next tick
            ApplyGains();
        }
        catch (Exception ex)
        {
            Log.Warn($"Clip audio could not be loaded for {item.Path}: {ex.Message}");
            if (version == _openVersion)
            {
                SetAudioNote("Separate volume for each track is not available for this clip.");
                Timeline.SetTracks([]);
            }
        }
    }

    private void ShowTrackRows(ClipInfo info, bool loading, ClipStems? stems = null)
    {
        var tracks = new List<TimelineTrack>();
        if (info.HasStems)
        {
            TrackALabel.Text = Label(info.AudioTitles[1], "Desktop audio");
            TrackBLabel.Text = Label(info.AudioTitles[2], "Microphone");
            TrackBRow.Visibility = Visibility.Visible;
            tracks.Add(new TimelineTrack(TrackALabel.Text, DesktopColor, stems != null ? ClipAudio.Peaks(stems.First) : []));
            tracks.Add(new TimelineTrack(TrackBLabel.Text, MicColor, stems?.Second != null ? ClipAudio.Peaks(stems.Second) : []));
        }
        else if (info.AudioStreams > 0)
        {
            TrackALabel.Text = Label(info.AudioTitles[0], "Audio");
            TrackBRow.Visibility = Visibility.Collapsed;
            tracks.Add(new TimelineTrack(TrackALabel.Text, info.AudioTitles[0] == ClipAudio.MicTitle ? MicColor : DesktopColor,
                stems != null ? ClipAudio.Peaks(stems.First) : []));
        }
        if (!loading || tracks.Count > 0) Timeline.SetTracks(tracks);

        static string Label(string title, string fallback) => title switch
        {
            ClipAudio.DesktopTitle => "Desktop audio",
            ClipAudio.MicTitle => "Microphone",
            ClipAudio.MixTitle or "" => fallback,
            _ => title,
        };
    }

    private void ApplyDuration(double seconds)
    {
        bool untrimmed = Timeline.TrimEnd <= 0 || Timeline.TrimEnd >= Timeline.Duration - 0.01;
        Timeline.Duration = seconds;
        if (untrimmed || Timeline.TrimEnd > seconds) Timeline.TrimEnd = seconds;
        if (Timeline.TrimStart > seconds) Timeline.TrimStart = 0;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(0.1, seconds);
        _updatingSeek = false;
        PlayerTotal.Text = FormatTime(seconds);
        UpdateTrimInfo();
    }

    private void SetAudioNote(string text)
    {
        AudioNoteText.Text = text;
        AudioNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPlayerError(string message)
    {
        PlayerError.Text = "This clip could not be played here.\n" + message + "\nRight-click the clip and choose \"Open in default player\".";
        PlayerError.Visibility = Visibility.Visible;
        _isPlaying = false;
        UpdatePlayGlyph();
    }

    // ================================================================ playback

    private void UpdatePlayGlyph() => PlayPauseGlyph.Text = _isPlaying ? "" : "";

    private double VideoDuration => Timeline.Duration > 0 ? Timeline.Duration
        : Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;

    private double Fps => _info is { Fps: > 0 } i ? i.Fps : 60;

    private void TogglePlay()
    {
        if (_playing == null) return;
        if (_isPlaying) Player.Pause();
        else
        {
            double pos = Player.Position.TotalSeconds, total = VideoDuration;
            if (total > 0 && pos >= total - 0.05) SeekTo(Timeline.IsTrimmed ? Timeline.TrimStart : 0);
            else if (Timeline.IsTrimmed && pos >= Timeline.TrimEnd - 0.05) SeekTo(Timeline.TrimStart);
            Player.Play();
        }
        _isPlaying = !_isPlaying;
        UpdatePlayGlyph();
    }

    private void PauseAt(double seconds)
    {
        Player.Pause();
        _isPlaying = false;
        SeekTo(seconds);
        UpdatePlayGlyph();
    }

    private void SeekTo(double seconds)
    {
        if (_playing == null) return;
        double total = VideoDuration;
        seconds = Math.Clamp(seconds, 0, total > 0 ? total : seconds);
        Player.Position = TimeSpan.FromSeconds(seconds);
        _prevTickPos = seconds;
        _stemPlayer?.Seek(seconds);
        ShowPosition(seconds);
    }

    private void SeekBy(double seconds)
    {
        if (_playing == null) return;
        SeekTo(Player.Position.TotalSeconds + seconds);
    }

    private void StepFrame(int frames)
    {
        if (_playing == null) return;
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
            UpdatePlayGlyph();
        }
        SeekTo(Player.Position.TotalSeconds + frames / Fps);
    }

    private void ShowPosition(double seconds)
    {
        _updatingSeek = true;
        SeekSlider.Value = seconds;
        _updatingSeek = false;
        Timeline.Position = seconds;
        PlayerTime.Text = FormatTime(seconds);
    }

    private void RefreshPlayerPosition()
    {
        if (_playing == null || !Player.NaturalDuration.HasTimeSpan) return;
        double pos = Player.Position.TotalSeconds;
        long now = Environment.TickCount64;
        bool advanced = pos != _lastVideoPos;
        if (advanced)
        {
            _lastVideoPos = pos;
            _lastVideoAdvance = now;
        }

        // Keep the separate audio tracks on the video's clock. The video pauses, stalls and jumps; the audio follows.
        if (_stemPlayer is { } sp)
        {
            if (_isPlaying && advanced)
            {
                if (!sp.Playing)
                {
                    sp.Seek(pos + sp.LeadSeconds);
                    sp.Playing = true;
                }
                else if (Math.Abs(sp.HeardSeconds - pos) > 0.12) sp.Seek(pos + sp.LeadSeconds);
            }
            else if (!_isPlaying || now - _lastVideoAdvance > 300) sp.Playing = false;
        }

        // Playing through the end of the kept range stops there, like reaching the end of the clip.
        if (_isPlaying && Timeline.IsTrimmed && _prevTickPos < Timeline.TrimEnd && pos >= Timeline.TrimEnd)
        {
            PauseAt(Timeline.TrimEnd);
            return;
        }
        _prevTickPos = pos;
        if (!_dragging) ShowPosition(pos);
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        if (Timeline.Duration <= 0) ApplyDuration(Player.NaturalDuration.TimeSpan.TotalSeconds);
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _isPlaying = false;
        if (_stemPlayer != null) _stemPlayer.Playing = false;
        UpdatePlayGlyph();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn("Playback failed: " + e.ErrorException?.Message);
        ShowPlayerError(e.ErrorException?.Message ?? "");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void ToStart_Click(object sender, RoutedEventArgs e) => SeekTo(0);
    private void ToEnd_Click(object sender, RoutedEventArgs e) => SeekTo(VideoDuration);
    private void FrameBack_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void FrameForward_Click(object sender, RoutedEventArgs e) => StepFrame(1);
    private void PlayerClose_Click(object sender, RoutedEventArgs e) => ClosePlayer();
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void PlayerSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) SetFullscreen(!_fullscreen);
        else TogglePlay();
        e.Handled = true;
    }

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        if (_playing != null) SeekTo(SeekSlider.Value);
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || _playing == null || Player == null) return;
        SeekTo(e.NewValue);
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        MuteGlyph.Text = _muted ? "" : "";
        MuteButton.ToolTip = _muted ? "Unmute (M)" : "Mute (M)";
        if (_stemPlayer != null) _stemPlayer.Muted = _muted || PlayerSilent;
        Player.IsMuted = _stemPlayer != null || _muted || PlayerSilent;
    }

    private void PlayerShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_playing != null) ShowInFolder(_playing.Path);
    }

    private void PlayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_playing != null) DeleteClip(_playing);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey || PlayerOverlay.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (_fullscreen) SetFullscreen(false);
                else ClosePlayer();
                break;
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekBy(-5); break;
            case Key.Right: SeekBy(5); break;
            case Key.OemComma: StepFrame(-1); break;
            case Key.OemPeriod: StepFrame(1); break;
            case Key.Home: SeekTo(0); break;
            case Key.End: SeekTo(VideoDuration); break;
            case Key.I: SetTrimHere(start: true); break;
            case Key.O: SetTrimHere(start: false); break;
            case Key.F: SetFullscreen(!_fullscreen); break;
            case Key.M: ToggleMute(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ================================================================ full screen

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            PlayerHeader.Visibility = Visibility.Collapsed;
            TimelinePanel.Visibility = Visibility.Collapsed;
            TrimPanel.Visibility = Visibility.Collapsed;
            Grid.SetRow(ControlBar, 1);
            ControlBar.VerticalAlignment = VerticalAlignment.Bottom;
            ControlBar.Background = BarOverlay;
            Panel.SetZIndex(ControlBar, 2);
            _hideControls.Start();
        }
        else
        {
            _hideControls.Stop();
            ControlBar.Visibility = Visibility.Visible;
            PlayerSurface.Cursor = Cursors.Arrow;
            WindowState = _prevState;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            PlayerHeader.Visibility = Visibility.Visible;
            TimelinePanel.Visibility = Visibility.Visible;
            TrimPanel.Visibility = Visibility.Visible;
            Grid.SetRow(ControlBar, 2);
            ControlBar.VerticalAlignment = VerticalAlignment.Stretch;
            ControlBar.Background = BarBackground;
            Panel.SetZIndex(ControlBar, 0);
        }
        FullscreenGlyph.Text = on ? "" : "";
        FullscreenButton.ToolTip = on ? "Exit full screen (F, Esc)" : "Full screen (F)";
    }

    private void PlayerOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        if (ControlBar.Visibility != Visibility.Visible)
        {
            ControlBar.Visibility = Visibility.Visible;
            PlayerSurface.Cursor = Cursors.Arrow;
        }
        _hideControls.Stop();
        _hideControls.Start();
    }

    // ================================================================ volume and trim

    private void TrackVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_resettingEditor || TrackAPercent == null || TrackBPercent == null) return;
        ApplyGains();
        UpdateEditorButtons();
    }

    private static float Gain(Slider s) => (float)(Math.Round(s.Value / 5) * 5 / 100);

    private void ApplyGains()
    {
        float a = Gain(TrackASlider), b = Gain(TrackBSlider);
        TrackAPercent.Text = $"{a * 100:0}%";
        TrackBPercent.Text = $"{b * 100:0}%";
        if (_stemPlayer != null) _stemPlayer.SetGains(a, b);
        else Player.Volume = Math.Clamp(a, 0, 1);
    }

    private void SetTrimHere(bool start)
    {
        if (_playing == null || Timeline.Duration <= 0) return;
        double pos = Player.Position.TotalSeconds;
        if (start) Timeline.TrimStart = Math.Min(pos, Math.Max(0, Timeline.TrimEnd - 0.5));
        else Timeline.TrimEnd = Math.Max(pos, Math.Min(Timeline.Duration, Timeline.TrimStart + 0.5));
        UpdateTrimInfo();
        UpdateEditorButtons();
    }

    private void UpdateTrimInfo()
    {
        if (Timeline.Duration <= 0) TrimInfo.Text = "";
        else if (!Timeline.IsTrimmed) TrimInfo.Text = "Whole clip kept";
        else TrimInfo.Text = $"Keep {FormatTime(Timeline.TrimStart)} – {FormatTime(Timeline.TrimEnd)}  ({Timeline.TrimEnd - Timeline.TrimStart:0.0} s)";
    }

    private bool EditorChanged => Timeline.IsTrimmed || Gain(TrackASlider) != 1f || (TrackBRow.Visibility == Visibility.Visible && Gain(TrackBSlider) != 1f);

    private void UpdateEditorButtons()
    {
        SaveTrimButton.IsEnabled = _playing != null && _info != null && !_exporting && _engine.FfmpegPath != null && EditorChanged;
        SaveTrimButton.ToolTip = _exporting ? "Saving…"
            : _info == null ? "Reading the clip…"
            : !EditorChanged ? "Drag the handles on the video bar, or change a volume, to save a new copy of the clip"
            : "Save a new clip with this part and these volumes. The original is kept.";
    }

    private async void SaveTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_playing == null || _info == null || _exporting || _engine.FfmpegPath is not { } ffmpeg) return;
        ClipItem item = _playing;
        string output = ClipLibrary.UniquePath(Path.GetDirectoryName(item.Path)!, item.Name + " (trimmed)");
        var request = new TrimRequest(item.Path, output, Timeline.TrimStart, Timeline.TrimEnd, Gain(TrackASlider), Gain(TrackBSlider),
            _info, _engine.Settings.Current.Quality);
        _exporting = true;
        UpdateEditorButtons();
        ExportProgress.Value = 0;
        ExportProgress.Visibility = Visibility.Visible;
        IProgress<double> progress = new Progress<double>(p => ExportProgress.Value = p);
        string? error;
        try
        {
            error = await Task.Run(() => ClipEditor.Export(ffmpeg, request, _engine.Job, progress.Report));
        }
        catch (Exception ex)
        {
            Log.Error("Saving the trim failed", ex);
            error = ex.Message;
        }
        _exporting = false;
        ExportProgress.Visibility = Visibility.Collapsed;
        UpdateEditorButtons();
        if (error != null)
        {
            MessageBox.Show(this, error, "Save trim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        QueueReload();
        if (File.Exists(output)) OpenPlayer(new ClipItem(new FileInfo(output)));
    }

    // ================================================================ diagnostics

    public bool PlayFirstClip()
    {
        if (_clips.Count == 0) return false;
        OpenPlayer(_clips[0]);
        return true;
    }

    public string PlayerDebug()
    {
        double video = Player.Position.TotalSeconds;
        string stems = _stemPlayer is { } sp ? $"stems=on heard={sp.HeardSeconds:F2} drift={sp.HeardSeconds - video:F3}" : "stems=off";
        return $"hasDuration={Player.NaturalDuration.HasTimeSpan} duration={(Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0):F2} " +
               $"position={video:F2} video={Player.NaturalVideoWidth}x{Player.NaturalVideoHeight} hasAudio={Player.HasAudio} error={PlayerError.Visibility == Visibility.Visible} " +
               $"info={(_info == null ? "none" : $"{_info.Width}x{_info.Height}@{_info.Fps:0.##} {_info.Seconds:F2}s audio={_info.AudioStreams}")} {stems} note=\"{AudioNoteText.Text}\"";
    }

    public void ClosePlayerForTest() => ClosePlayer();
}
