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
/// The clip player: video through a <see cref="VideoView"/>, audio through <see cref="StemPlayer"/> so each app and
/// the microphone can be balanced while watching, a timeline for trimming, and full-screen viewing. Trimmed copies
/// are written by <see cref="ClipEditor"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Longer clips play with their mixed audio only: ten minutes of decoded tracks already take about 230 MB.</summary>
    private const int MaxStemSeconds = 600;
    private static readonly Color MicColor = Color.FromRgb(0xF5, 0xE6, 0xCA);
    /// <summary>
    /// Colours for desktop audio and the apps, in track order: Beige, White, a light coffee, then more shades of the
    /// palette. The microphone keeps Cream.
    /// </summary>
    private static readonly Color[] TrackColors =
    [
        Color.FromRgb(0xDC, 0xC7, 0xAA), Color.FromRgb(0xFF, 0xFC, 0xF7), Color.FromRgb(0xA5, 0x7F, 0x60), Color.FromRgb(0xC9, 0xA2, 0x7E),
        Color.FromRgb(0xE8, 0xD5, 0xB5), Color.FromRgb(0x8F, 0x6A, 0x50), Color.FromRgb(0xB8, 0x95, 0x76), Color.FromRgb(0xF0, 0xE3, 0xD0),
    ];

    /// <summary>
    /// While the playhead is dragged the video is asked for a new frame at most this often. Every seek makes the
    /// player decode from the previous keyframe; asking on every mouse movement queued up far more work than it could
    /// do, which made dragging lag behind the mouse and stutter.
    /// </summary>
    private const int ScrubSeekIntervalMs = 90;
    /// <summary>Width of the label column left of the timeline. The seek bar starts after the same width, so it lines up with the timeline.</summary>
    public static readonly GridLength TrackLabelWidth = new(210);
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _hideControls = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    /// <summary>Writes the volume to the settings once the slider has rested, not on every step of a drag.</summary>
    private readonly DispatcherTimer _volumeSave = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Slider> _trackControls = new();

    private ClipItem? _playing;
    private bool _isPlaying, _dragging, _updatingSeek, _muted, _exporting, _fullscreen, _resettingEditor, _volumeReady;
    private int _openVersion;
    /// <summary>Where the clip being opened starts playing, in seconds, once the video has opened.</summary>
    private double _startAt;
    private ClipInfo? _info;
    private StemPlayer? _stemPlayer;
    private double _lastVideoPos = -1, _prevTickPos;
    private long _lastVideoAdvance;
    private bool _scrubbing, _resumeAfterScrub, _scrubPending;
    private double _scrubTarget, _scrubApplied = -1;
    private long _lastScrubSeek;
    private int _scrubSeeks;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;
    /// <summary>Whether full screen made the window cover the screen (the diagnostics only lay the player out for it).</summary>
    private bool _tookScreen;
    /// <summary>Shows the control bar over the video in full screen; made the first time.</summary>
    private BarWindow? _barWindow;
    private Win32.POINT _lastPointer;

    /// <summary>Set by diagnostics (--render-ui) so the check plays nothing through the speakers.</summary>
    public bool PlayerSilent { get; set; }

    private void InitPlayer()
    {
        _playerTimer.Tick += (_, _) => RefreshPlayerPosition();
        _hideControls.Tick += (_, _) => HideIdleBar();
        _scrubTimer.Tick += (_, _) =>
        {
            if (_scrubPending && Environment.TickCount64 - _lastScrubSeek >= ScrubSeekIntervalMs) ApplyScrubSeek();
        };
        _volumeSave.Tick += (_, _) => SaveVolume();
        Video.Opened += Video_Opened;
        Video.Ended += Video_Ended;
        Video.Failed += Video_Failed;
        // In full screen the bar's own window follows the video's area.
        PlayerSurface.SizeChanged += (_, _) => PlaceBar();
        ControlBar.SizeChanged += (_, _) => PlaceBar();
        LocationChanged += (_, _) => PlaceBar();
        SeekSlider.SizeChanged += (_, _) => AlignSeekSlider();
        VolumeSlider.Value = _engine.Settings.Current.PlayerVolume;
        _volumeReady = true;
        Timeline.SeekRequested += ScrubTo;
        Timeline.ScrubStarted += BeginScrub;
        Timeline.ScrubEnded += EndScrub;
        Timeline.TrimChanged += () =>
        {
            UpdateTrimInfo();
            UpdateEditorButtons();
        };
    }

    // ================================================================ open and close

    /// <summary>Opens a clip in the player and plays it, from <paramref name="startAt"/> seconds in.</summary>
    private void OpenPlayer(ClipItem item, double startAt = 0)
    {
        if (!File.Exists(item.Path))
        {
            QueueReload();
            return;
        }
        ClosePlayer();
        int version = ++_openVersion;
        _playing = item;
        _startAt = startAt;
        PlayerTitle.Text = item.Title.ToUpperInvariant();
        PlayerTitle.ToolTip = item.Name;
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
            ApplyVolume();
            Video.Visibility = Visibility.Visible;
            Video.Open(item.Path);
            Video.Play();
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
        if (_volumeSave.IsEnabled) SaveVolume();
        _playerTimer.Stop();
        _scrubTimer.Stop();
        _scrubbing = _dragging = _scrubPending = false;
        _startAt = 0;
        _openVersion++;
        if (_playing == null && PlayerOverlay.Visibility != Visibility.Visible) return;
        SetFullscreen(false);
        _stemPlayer?.Dispose();
        _stemPlayer = null;
        _info = null;
        Video.Close(); // releases the file so it can be renamed or deleted
        _playing = null;
        _isPlaying = false;
        PlayerOverlay.Visibility = Visibility.Collapsed;
        Timeline.SetTracks([]);
        Timeline.Filmstrip = null;
    }

    private void ResetEditor()
    {
        _resettingEditor = true;
        Timeline.SetTracks([]);
        Timeline.Duration = 0;
        Timeline.Position = 0;
        Timeline.TrimStart = 0;
        Timeline.TrimEnd = 0;
        Timeline.Filmstrip = null;
        BuildTrackRows([("Audio", TrackColors[0])]);
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
            _ = LoadFilmstripAsync(item, info.Seconds, version);
            ShowTrackRows(info, loading: true);
            UpdateEditorButtons();
            if (info.AudioStreams == 0)
            {
                SetAudioNote("This clip has no audio.");
                Timeline.SetTracks([]);
                BuildTrackRows([("No audio", TrackColors[0])], enabled: false);
                return;
            }
            if (info.Seconds > MaxStemSeconds)
            {
                SetAudioNote("Clips longer than 10 minutes play with their mixed audio.");
                ShowTrackRows(info, loading: false);
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
                ApplyGains();
                SetAudioNote("Audio output stopped; playing the clip's mixed audio.");
            };
            _stemPlayer = player;
            _lastVideoPos = -1; // line the audio up with the picture on the next tick
            ApplyGains();       // also mutes the video's own sound, now that the tracks play it
        }
        catch (Exception ex)
        {
            Log.Warn($"Clip audio could not be loaded for {item.Path}: {ex.Message}");
            if (version == _openVersion && _info != null)
            {
                SetAudioNote("Separate volume for each track is not available for this clip.");
                ShowTrackRows(_info, loading: false);
            }
        }
    }

    /// <summary>Puts frames from across the clip into the timeline's video bar.</summary>
    private async Task LoadFilmstripAsync(ClipItem item, double seconds, int version)
    {
        if (_engine.FfmpegPath is not { } ffmpeg) return;
        try
        {
            ImageSource? strip = await Task.Run(() => ClipLibrary.LoadFilmstrip(ffmpeg, item.Path, item.Key, seconds, _engine.Job));
            if (version == _openVersion) Timeline.Filmstrip = strip;
        }
        catch (Exception ex)
        {
            Log.Warn("Filmstrip error: " + ex.Message);
        }
    }

    /// <summary>
    /// Shows the clip's audio tracks: a volume row each beside the timeline (made once, when the clip has been read)
    /// and a waveform each in the timeline (once the audio is decoded; without <paramref name="stems"/> the rows stay
    /// empty, or say "Loading audio" while <paramref name="loading"/>).
    /// </summary>
    private void ShowTrackRows(ClipInfo info, bool loading, ClipStems? stems = null)
    {
        IReadOnlyList<int> streams = info.StemStreams;
        var rows = new List<(string Label, Color Color)>();
        int colour = 0;
        foreach (int stream in streams)
        {
            string title = info.AudioTitles[stream];
            rows.Add((TrackLabel(title), title == ClipAudio.MicTitle ? MicColor : TrackColors[colour++ % TrackColors.Length]));
        }
        if (stems == null) BuildTrackRows(rows);

        var tracks = new List<TimelineTrack>();
        for (int i = 0; i < rows.Count; i++)
        {
            float[] peaks = stems != null && i < stems.Tracks.Count ? ClipAudio.Peaks(stems.Tracks[i].Pcm) : loading ? [] : [0f];
            tracks.Add(new TimelineTrack(rows[i].Label, rows[i].Color, peaks));
        }
        Timeline.SetTracks(tracks);
    }

    private static string TrackLabel(string title) => title switch
    {
        ClipAudio.DesktopTitle => "Desktop audio",
        ClipAudio.MicTitle => "Microphone",
        ClipAudio.MixTitle or "" => "Audio",
        _ => title,
    };

    /// <summary>Makes one volume row per track beside the timeline, at the timeline's row height.</summary>
    private void BuildTrackRows(IReadOnlyList<(string Label, Color Color)> rows, bool enabled = true)
    {
        bool was = _resettingEditor;
        _resettingEditor = true;
        TrackRows.Children.Clear();
        _trackControls.Clear();
        double height = TimelineControl.TrackHeightFor(rows.Count);
        var mono = (FontFamily)FindResource("MonoFont");
        var sliderStyle = (Style)FindResource("ThinSliderDark");
        foreach (var (label, color) in rows)
        {
            // A hollow marker in the track's colour (the same as its waveform), its name, and its volume.
            var marker = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Stroke = new SolidColorBrush(color), StrokeThickness = 1.3,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            var name = new TextBlock
            {
                Text = label.ToUpperInvariant(), FontFamily = mono, FontSize = 10, Foreground = _beige, Width = 92,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label,
            };
            var slider = new Slider
            {
                Style = sliderStyle, Minimum = 0, Maximum = 200, Value = 100, SmallChange = 5, LargeChange = 25, IsEnabled = enabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += TrackVolume_Changed;
            var row = new DockPanel { Height = height, Margin = new Thickness(0, TimelineControl.RowGap, 0, 0) };
            DockPanel.SetDock(marker, Dock.Left);
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(name);
            row.Children.Add(slider);
            TrackRows.Children.Add(row);
            _trackControls.Add(slider);
        }
        _resettingEditor = was;
        ApplyGains();
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
        FsTime.Text = $"{PlayerTime.Text} / {PlayerTotal.Text}";
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
        Video.Visibility = Visibility.Collapsed; // the message would be under the video's window otherwise
        _isPlaying = false;
        UpdatePlayGlyph();
    }

    // ================================================================ playback

    private void UpdatePlayGlyph() => SetPlayGlyph(_isPlaying);

    private void SetPlayGlyph(bool playing) => PlayPauseGlyph.Text = playing ? "" : "";

    private double VideoDuration => Timeline.Duration > 0 ? Timeline.Duration
        : Video.Duration;

    private double Fps => _info is { Fps: > 0 } i ? i.Fps : 60;

    private void TogglePlay()
    {
        if (_playing == null) return;
        if (_scrubbing)
        {
            // Space while dragging: decide whether playback continues once the playhead is let go.
            _resumeAfterScrub = !_resumeAfterScrub;
            SetPlayGlyph(_resumeAfterScrub);
            return;
        }
        if (_isPlaying) Video.Pause();
        else
        {
            double pos = Video.Position, total = VideoDuration;
            if (total > 0 && pos >= total - 0.05) SeekTo(Timeline.IsTrimmed ? Timeline.TrimStart : 0);
            else if (Timeline.IsTrimmed && pos >= Timeline.TrimEnd - 0.05) SeekTo(Timeline.TrimStart);
            Video.Play();
        }
        _isPlaying = !_isPlaying;
        UpdatePlayGlyph();
    }

    private void PauseAt(double seconds)
    {
        Video.Pause();
        _isPlaying = false;
        SeekTo(seconds);
        UpdatePlayGlyph();
    }

    private double ClampToClip(double seconds)
    {
        double total = VideoDuration;
        return Math.Clamp(seconds, 0, total > 0 ? total : Math.Max(0, seconds));
    }

    private void SeekTo(double seconds)
    {
        if (_playing == null) return;
        seconds = ClampToClip(seconds);
        Video.Position = seconds;
        _prevTickPos = seconds;
        _stemPlayer?.Seek(seconds);
        ShowPosition(seconds);
    }

    // ---------------------------------------------------------------- dragging the playhead

    /// <summary>
    /// The playhead is being dragged (on the timeline or the seek bar). Playback pauses until it is let go, the clip's
    /// separate audio stays quiet, and the picture follows the mouse at a pace the video decoder can keep up with.
    /// </summary>
    private void BeginScrub()
    {
        if (_scrubbing || _playing == null) return;
        _scrubbing = _dragging = true;
        _resumeAfterScrub = _isPlaying;
        if (_isPlaying)
        {
            Video.Pause();
            _isPlaying = false;
        }
        if (_stemPlayer != null) _stemPlayer.Playing = false;
        _scrubTarget = Video.Position;
        _scrubApplied = -1;
        _scrubPending = false;
        _lastScrubSeek = 0;
        _scrubTimer.Start();
    }

    private void ScrubTo(double seconds)
    {
        if (_playing == null) return;
        if (!_scrubbing)
        {
            SeekTo(seconds);
            return;
        }
        _scrubTarget = ClampToClip(seconds);
        _scrubPending = true;
        ShowPosition(_scrubTarget);
        if (Environment.TickCount64 - _lastScrubSeek >= ScrubSeekIntervalMs) ApplyScrubSeek();
    }

    private void ApplyScrubSeek()
    {
        _scrubPending = false;
        if (_scrubTarget == _scrubApplied) return;
        _lastScrubSeek = Environment.TickCount64;
        _scrubApplied = _scrubTarget;
        _scrubSeeks++;
        Video.Position = _scrubTarget;
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = _dragging = _scrubPending = false;
        _scrubTimer.Stop();
        if (_playing == null) return;
        double target = _scrubTarget;
        if (target != _scrubApplied) Video.Position = target;
        _prevTickPos = target;
        _stemPlayer?.Seek(target);
        ShowPosition(target);
        if (_resumeAfterScrub)
        {
            Video.Play();
            _isPlaying = true;
            _lastVideoPos = -1;
        }
        UpdatePlayGlyph();
    }

    private void SeekBy(double seconds)
    {
        if (_playing == null) return;
        SeekTo(Video.Position + seconds);
    }

    private void StepFrame(int frames)
    {
        if (_playing == null) return;
        if (_isPlaying)
        {
            Video.Pause();
            _isPlaying = false;
            UpdatePlayGlyph();
        }
        SeekTo(Video.Position + frames / Fps);
    }

    /// <summary>
    /// A slider keeps its thumb inside its ends, so its first and last values sit half a thumb in from its edges, while
    /// the timeline has them at its edges. Widening the slider by that much on each side puts every moment at the same
    /// x on both, for the thumb and for a click.
    /// </summary>
    private void AlignSeekSlider()
    {
        if (SeekSlider.Template?.FindName("PART_Track", SeekSlider) is not Track { Thumb: { } thumb } track || thumb.DesiredSize.Width <= 0) return;
        double half = thumb.DesiredSize.Width / 2; // the track lays the thumb out at its desired width
        double trackLeft = track.TranslatePoint(new Point(0, 0), SeekSlider).X;
        double trackRight = SeekSlider.ActualWidth - track.TranslatePoint(new Point(track.ActualWidth, 0), SeekSlider).X;
        Thickness now = SeekSlider.Margin;
        double left = -(trackLeft + half), right = -(trackRight + half); // the track's place inside the slider does not depend on the margin
        if (Math.Abs(left - now.Left) > 0.25 || Math.Abs(right - now.Right) > 0.25)
            SeekSlider.Margin = new Thickness(left, now.Top, right, now.Bottom);
    }

    private void ShowPosition(double seconds)
    {
        _updatingSeek = true;
        SeekSlider.Value = seconds;
        _updatingSeek = false;
        Timeline.Position = seconds;
        PlayerTime.Text = FormatTime(seconds);
        FsTime.Text = $"{PlayerTime.Text} / {PlayerTotal.Text}";
    }

    private void RefreshPlayerPosition()
    {
        if (_playing == null || !Video.HasDuration) return;
        double pos = Video.Position;
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

    private void Video_Opened()
    {
        if (!Video.HasDuration) return;
        if (Timeline.Duration <= 0) ApplyDuration(Video.Duration);
        if (_startAt > 0)
        {
            // Opened from a still in the Latest panel: start where that still is. The clip's separate audio, once it
            // is decoded, lines up with the picture wherever it is.
            double start = _startAt;
            _startAt = 0;
            SeekTo(start);
        }
    }

    private void Video_Ended()
    {
        Video.Pause();
        _isPlaying = false;
        if (_stemPlayer != null) _stemPlayer.Playing = false;
        UpdatePlayGlyph();
    }

    private void Video_Failed(string message)
    {
        Log.Warn("Playback failed: " + message);
        ShowPlayerError(message);
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

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => BeginScrub();

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubTarget = ClampToClip(SeekSlider.Value);
        EndScrub();
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || _playing == null || Video == null) return;
        ScrubTo(e.NewValue);
    }

    private void ToggleMute()
    {
        // With the slider all the way down, "unmute" means turning it back up to something audible.
        if (!_muted && VolumeSlider.Value <= 0) VolumeSlider.Value = 50;
        else _muted = !_muted;
        ApplyVolume();
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_volumeReady) return;
        _muted = false; // moving the volume means wanting to hear it, as in other players
        ApplyVolume();
        _volumeSave.Stop();
        _volumeSave.Start();
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        NudgeVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void NudgeVolume(int percent)
    {
        _muted = false; // also when the slider is already at the end and does not move
        VolumeSlider.Value = Math.Clamp(Math.Round((VolumeSlider.Value + percent) / 5) * 5, 0, 100);
        ApplyVolume();
    }

    private void SaveVolume()
    {
        _volumeSave.Stop();
        int volume = (int)Math.Round(VolumeSlider.Value);
        AppSettings s = _engine.Settings.Current;
        if (s.PlayerVolume == volume) return;
        s.PlayerVolume = volume;
        _engine.Settings.Save(s);
    }

    /// <summary>
    /// The volume slider as a level for the sound: squared, so the slider sounds even along its length (half way is
    /// a quarter of the level, about -12 dB) instead of all the change happening near the bottom. Zero while muted.
    /// </summary>
    private float MasterVolume => _muted ? 0f : (float)Math.Pow(VolumeSlider.Value / 100, 2);

    /// <summary>Applies the volume slider and mute to whichever output is playing the clip's sound, and updates the speaker icon.</summary>
    private void ApplyVolume()
    {
        float master = MasterVolume;
        bool silent = _muted || PlayerSilent;
        if (_stemPlayer != null)
        {
            _stemPlayer.Volume = master;
            _stemPlayer.Muted = silent;
            Video.IsMuted = true;
        }
        else
        {
            // The video's own (mixed) sound: its track slider and the volume together.
            float[] gains = Gains;
            Video.IsMuted = silent;
            Video.Volume = Math.Clamp((gains.Length > 0 ? gains.Max() : 1) * master, 0, 1);
        }

        double v = VolumeSlider.Value;
        MuteGlyph.Text = _muted ? "" : v <= 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        MuteButton.ToolTip = _muted || v <= 0 ? "Unmute (M)" : "Mute (M)";
        VolumeSlider.ToolTip = $"Volume {(_muted ? 0 : v):0}% (Up / Down, or the mouse wheel). Saved trims are not affected.";
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
            case Key.Up: NudgeVolume(5); break;
            case Key.Down: NudgeVolume(-5); break;
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

    private void SetFullscreen(bool on) => SetFullscreen(on, takeScreen: true);

    /// <param name="takeScreen">False only for the diagnostics: the player is laid out for full screen, the window stays as it is.</param>
    private void SetFullscreen(bool on, bool takeScreen)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _tookScreen = takeScreen;
            if (takeScreen)
            {
                _prevState = WindowState;
                _prevStyle = WindowStyle;
                _prevResize = ResizeMode;
                ApplyChrome(fullscreen: true);
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            PlayerHeader.Visibility = Visibility.Collapsed;
            PlayerRail.Visibility = Visibility.Collapsed;
            PlayerTitleRow.Visibility = Visibility.Collapsed;
            TimelinePanel.Visibility = Visibility.Collapsed;
            Grid.SetRow(PlayerBody, 0);
            Grid.SetRowSpan(PlayerBody, 2);
            Grid.SetColumn(PlayerContent, 0);
            Grid.SetColumnSpan(PlayerContent, 2);
            PlayerContent.Margin = new Thickness(0);
            PlayerSurface.Background = Brushes.Black;
            // No timeline to line up with: the seek bar goes on top, across the whole width, with the time beside it.
            Grid.SetRow(SeekRow, 0);
            Grid.SetRow(TransportRow, 1);
            SeekRow.Margin = new Thickness(0, 0, 0, 8);
            SeekLabelColumn.Width = GridLength.Auto;
            FsTime.Visibility = Visibility.Visible;
            ControlBar.Padding = new Thickness(24, 12, 24, 12);
            ShowBarOverVideo();
            _hideControls.Start();
        }
        else
        {
            _hideControls.Stop();
            _barWindow?.Hide();
            Grid.SetRow(ControlBarHost, 2);
            ControlBarHost.VerticalAlignment = VerticalAlignment.Stretch;
            ControlBarHost.Opacity = 1;
            ControlBarHost.Visibility = Visibility.Visible;
            Panel.SetZIndex(ControlBarHost, 0);
            PlayerSurface.Cursor = Cursors.Arrow;
            if (_tookScreen)
            {
                WindowState = _prevState;
                WindowStyle = _prevStyle;
                ResizeMode = _prevResize;
                ApplyChrome(fullscreen: false);
            }
            PlayerHeader.Visibility = Visibility.Visible;
            PlayerRail.Visibility = Visibility.Visible;
            PlayerTitleRow.Visibility = Visibility.Visible;
            TimelinePanel.Visibility = Visibility.Visible;
            Grid.SetRow(PlayerBody, 1);
            Grid.SetRowSpan(PlayerBody, 1);
            Grid.SetColumn(PlayerContent, 1);
            Grid.SetColumnSpan(PlayerContent, 1);
            PlayerContent.Margin = new Thickness(24, 16, 26, 14);
            PlayerSurface.Background = (Brush)FindResource("EspressoDeepBrush");
            Grid.SetRow(TransportRow, 0);
            Grid.SetRow(SeekRow, 1);
            SeekRow.Margin = new Thickness(0, 6, 0, 0);
            SeekLabelColumn.Width = TrackLabelWidth;
            FsTime.Visibility = Visibility.Collapsed;
            ControlBar.Padding = new Thickness(0, 10, 0, 2);
        }
        UpdateFrame();
        FullscreenGlyph.Text = on ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = on ? "Exit full screen (F, Esc)" : "Full screen (F)";
    }

    /// <summary>
    /// Floats the control bar over the bottom of the video. WPF cannot draw over the video's window, so the real bar
    /// stays in the player's window, invisible but taking the clicks (they reach it through the video, as clicks on the
    /// video always did), and a see-through window on top shows a live picture of it with the video showing through.
    /// </summary>
    private void ShowBarOverVideo()
    {
        Grid.SetRow(ControlBarHost, 1);
        ControlBarHost.VerticalAlignment = VerticalAlignment.Bottom;
        ControlBarHost.Opacity = 0;
        Panel.SetZIndex(ControlBarHost, 2);
        BarWindow bar = _barWindow ??= CreateBarWindow();
        ShowFullscreenBar(true);
        _lastPointer = Win32.GetCursorPos(out Win32.POINT p) ? p : default;
        // Shown once the player is laid out full screen, so it never flashes up at the old size.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_fullscreen) return;
            PlaceBar();
            bar.Show();
        }, DispatcherPriority.Loaded);
    }

    private BarWindow CreateBarWindow()
    {
        var bar = new BarWindow(this, ControlBar);
        bar.DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceBar);
        return bar;
    }

    /// <summary>Lays the bar's window over the bottom of the video's area, as tall as the bar and its shade.</summary>
    private void PlaceBar()
    {
        if (!_fullscreen || _barWindow is not { } bar || !PlayerSurface.IsVisible || PlayerSurface.ActualWidth <= 0 || ControlBar.ActualHeight <= 0
            || PresentationSource.FromVisual(PlayerSurface) is not { CompositionTarget: { } target }) return;
        bar.SetBarSize(ControlBar.ActualWidth, ControlBar.ActualHeight);
        Point topLeft = PlayerSurface.PointToScreen(new Point(0, 0));
        Point bottomRight = PlayerSurface.PointToScreen(new Point(PlayerSurface.ActualWidth, PlayerSurface.ActualHeight));
        double scale = target.TransformToDevice.M22;
        int height = (int)Math.Round(Math.Min((BarWindow.FadeHeight + ControlBar.ActualHeight) * scale, bottomRight.Y - topLeft.Y));
        int left = (int)Math.Round(topLeft.X), right = (int)Math.Round(bottomRight.X), bottom = (int)Math.Round(bottomRight.Y);
        bar.Place(left, bottom - height, right - left, height);
    }

    private void ShowFullscreenBar(bool shown)
    {
        _barWindow?.SetShown(shown);
        // Hidden, the bar takes no clicks: a click there is a click on the video again.
        ControlBarHost.Visibility = shown ? Visibility.Visible : Visibility.Hidden;
        PlayerSurface.Cursor = shown ? Cursors.Arrow : Cursors.None;
    }

    /// <summary>Hides the bar and the pointer once the mouse has rested, but not while it is on the bar or dragging.</summary>
    private void HideIdleBar()
    {
        _hideControls.Stop();
        if (!_fullscreen) return;
        if (ControlBar.IsMouseOver || _scrubbing)
        {
            _hideControls.Start();
            return;
        }
        ShowFullscreenBar(false);
    }

    /// <summary>
    /// The mouse moved: show the bar and start counting again. Only a real move counts. WPF also reports a "move" when
    /// what is under a still pointer changes, which would bring the bar straight back as it hides.
    /// </summary>
    private void PokeFullscreenBar()
    {
        if (!_fullscreen || !Win32.GetCursorPos(out Win32.POINT p) || (p.X == _lastPointer.X && p.Y == _lastPointer.Y)) return;
        _lastPointer = p;
        if (_barWindow is { Shown: false }) ShowFullscreenBar(true);
        _hideControls.Stop();
        _hideControls.Start();
    }

    private void PlayerOverlay_MouseMove(object sender, MouseEventArgs e) => PokeFullscreenBar();

    // ================================================================ volume and trim

    private void TrackVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_resettingEditor) return;
        ApplyGains();
        UpdateEditorButtons();
    }

    private static float Gain(Slider s) => (float)(Math.Round(s.Value / 5) * 5 / 100);

    private float[] Gains => _trackControls.Select(Gain).ToArray();

    private void ApplyGains()
    {
        float[] gains = Gains;
        for (int i = 0; i < gains.Length; i++) _trackControls[i].ToolTip = $"Volume {gains[i] * 100:0}%, while watching and in a saved trim";
        _stemPlayer?.SetGains(gains);
        ApplyVolume();
    }

    private void SetTrimHere(bool start)
    {
        if (_playing == null || Timeline.Duration <= 0) return;
        double pos = Video.Position;
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

    private bool EditorChanged => Timeline.IsTrimmed || Gains.Any(g => g != 1f);

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
        var request = new TrimRequest(item.Path, output, Timeline.TrimStart, Timeline.TrimEnd, Gains, _info, _engine.Settings.Current.Quality);
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
        double video = Video.Position;
        string stems = _stemPlayer is { } sp ? $"stems=on heard={sp.HeardSeconds:F2} drift={sp.HeardSeconds - video:F3}" : "stems=off";
        return $"hasDuration={Video.HasDuration} duration={Video.Duration:F2} " +
               $"position={video:F2} video={Video.NaturalWidth}x{Video.NaturalHeight} shown={Video.ActualWidth:0}x{Video.ActualHeight:0} hasAudio={Video.HasAudio} error={PlayerError.Visibility == Visibility.Visible} " +
               $"info={(_info == null ? "none" : $"{_info.Width}x{_info.Height}@{_info.Fps:0.##} {_info.Seconds:F2}s audio={_info.AudioStreams}")} {stems} note=\"{AudioNoteText.Text}\"";
    }

    public void ClosePlayerForTest() => ClosePlayer();

    /// <summary>
    /// Lays the player out for full screen without making the window full screen (that would take over the user's
    /// screen), reports where the bar's window went, that it lets clicks through to the real bar right under its
    /// picture, that play / pause and the seek bar work there, hides and shows it, draws it over the clip's still to
    /// <paramref name="png"/>, and checks that the bar is back in its place afterwards.
    /// </summary>
    public async Task<string> FullscreenCheckAsync(string png)
    {
        if (_playing == null) return "no clip open";
        IntPtr foreground = GetForegroundWindow();
        string before = DescribeWindow(foreground);
        SetFullscreen(true, takeScreen: false);
        await Task.Delay(600);
        var parts = new List<string>();
        if (_barWindow is not { } bar || bar.Content is not FrameworkElement barRoot) return "no bar window";
        Point tl = PlayerSurface.PointToScreen(new Point(0, 0));
        Point br = PlayerSurface.PointToScreen(new Point(PlayerSurface.ActualWidth, PlayerSurface.ActualHeight));
        Point controlsTop = ControlBar.PointToScreen(new Point(0, 0));
        Win32.RECT r = bar.ScreenRect;
        parts.Add($"surface {tl.X:0},{tl.Y:0}-{br.X:0},{br.Y:0}, bar window {r.Left},{r.Top}-{r.Right},{r.Bottom} ({r.Width}x{r.Height}), " +
                  $"real bar at {controlsTop.X:0},{controlsTop.Y:0} {ControlBar.ActualWidth:0}x{ControlBar.ActualHeight:0} (picture at {r.Left},{r.Bottom - ControlBar.ActualHeight:0})");
        parts.Add($"shown={bar.IsVisible} clickThrough={bar.ClickThrough} active={bar.IsActive} foreground unchanged={GetForegroundWindow() == foreground} " +
                  $"(before: {before}, now: {DescribeWindow(GetForegroundWindow())})");

        // A click where the picture shows a control lands on that control in the player's window.
        string HitAt(FrameworkElement element, double fx, double fy)
        {
            Point at = element.TranslatePoint(new Point(element.ActualWidth * fx, element.ActualHeight * fy), this);
            return InputHitTest(at) is DependencyObject hit && (hit == element || element.IsAncestorOf(hit)) ? "yes" : $"no ({InputHitTest(at)?.GetType().Name ?? "nothing"})";
        }
        Point aboveBar = ControlBar.TranslatePoint(new Point(ControlBar.ActualWidth / 2, -40), this);
        parts.Add($"hits: play={HitAt(PlayPauseButton, 0.5, 0.5)} seek bar={HitAt(SeekSlider, 0.25, 0.5)} volume={HitAt(VolumeSlider, 0.5, 0.5)} exit={HitAt(FullscreenButton, 0.5, 0.5)} " +
                  $"shade above it={InputHitTest(aboveBar)?.GetType().Name ?? "nothing"}");
        bool wasPlaying = _isPlaying;
        PlayPauseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        bool afterClick = _isPlaying;
        PlayPauseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        double target = 0;
        if (SeekSlider.Template?.FindName("PART_Track", SeekSlider) is Track track)
        {
            target = track.ValueFromPoint(new Point(track.ActualWidth * 0.25, track.ActualHeight / 2));
            SeekSlider.Value = target; // what a click on the seek bar does (IsMoveToPointEnabled)
        }
        await Task.Delay(400);
        parts.Add($"play / pause: {wasPlaying} -> {afterClick} -> {_isPlaying}; seek bar click to {target:F2}s: video at {Video.Position:F2}s");

        // What it looks like: the bar's window drawn over the part of the clip's still it covers.
        int w = (int)Math.Ceiling(barRoot.ActualWidth), h = (int)Math.Ceiling(barRoot.ActualHeight);
        if (w > 0 && h > 0)
        {
            var barImage = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            barImage.Render(barRoot);
            var picture = new DrawingVisual();
            using (DrawingContext dc = picture.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
                if (_playing.Thumbnail is { } still && Video.ActualWidth > 0)
                {
                    // Where the video is drawn, in the bar window's terms (the bar covers the bottom of the surface).
                    double x = (PlayerSurface.ActualWidth - Video.ActualWidth) / 2;
                    double y = (PlayerSurface.ActualHeight - Video.ActualHeight) / 2 - (PlayerSurface.ActualHeight - h);
                    dc.DrawImage(still, new Rect(x, y, Video.ActualWidth, Video.ActualHeight));
                }
                dc.DrawImage(barImage, new Rect(0, 0, w, h));
            }
            var composite = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            composite.Render(picture);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composite));
            using (FileStream fs = File.Create(png)) encoder.Save(fs);
        }

        HideIdleBar();
        parts.Add($"after resting: shown={bar.Shown} takes clicks={ControlBarHost.IsVisible} pointer={(PlayerSurface.Cursor == Cursors.None ? "hidden" : "shown")}");
        ShowFullscreenBar(true);
        parts.Add($"after a move: shown={bar.Shown} takes clicks={ControlBarHost.IsVisible}");

        SetFullscreen(false);
        await Task.Delay(200);
        parts.Add($"after leaving: bar row {Grid.GetRow(ControlBarHost)} opacity {ControlBarHost.Opacity} window hidden={!bar.IsVisible}");
        return string.Join("; ", parts);
    }

    /// <summary>A window as "this app's main window", "this app's bar", or another program's name, for the checks.</summary>
    private string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        if (hwnd == new System.Windows.Interop.WindowInteropHelper(this).Handle) return "this app's main window";
        if (_barWindow != null && hwnd == new System.Windows.Interop.WindowInteropHelper(_barWindow).Handle) return "this app's bar";
        GetWindowThreadProcessId(hwnd, out int pid);
        try { return pid == Environment.ProcessId ? "another window of this app" : System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return $"process {pid}"; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    /// <summary>
    /// Puts the playhead at a few places and reports how far apart the seek bar's thumb and the timeline's playhead
    /// are drawn, and which time a click on the seek bar at the playhead's x would pick.
    /// </summary>
    public string AlignCheck()
    {
        double total = Timeline.Duration;
        if (_playing == null || total <= 0 || SeekSlider.Template?.FindName("PART_Track", SeekSlider) is not Track { Thumb: { } thumb } track)
            return "nothing to check";
        var parts = new List<string>();
        foreach (double fraction in new[] { 0, 0.25, 0.5, 0.75, 1 })
        {
            double t = total * fraction;
            ShowPosition(t);
            UpdateLayout();
            double head = Timeline.TranslatePoint(new Point(t / total * Timeline.ActualWidth, 0), this).X;
            double thumbCentre = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, 0), this).X;
            double clicked = track.ValueFromPoint(TranslatePoint(new Point(head, 0), track));
            parts.Add($"{t:F2}s thumb-playhead={thumbCentre - head:+0.0;-0.0;0.0}px click={clicked:F2}s");
        }
        ShowPosition(Video.Position);
        return $"seek bar margin {SeekSlider.Margin.Left:0.#}/{SeekSlider.Margin.Right:0.#}, timeline {Timeline.ActualWidth:0} px: " + string.Join("; ", parts);
    }

    /// <summary>Works the volume slider, mute and the volume keys the way a user would and reports what reaches the outputs.</summary>
    public string VolumeCheck()
    {
        double before = VolumeSlider.Value;
        bool wasMuted = _muted;
        var steps = new List<string>();
        void Step(string name) => steps.Add($"{name}: slider={VolumeSlider.Value:0} muted={_muted} " +
            $"stems={(_stemPlayer is { } sp ? $"{sp.Volume:F4}{(sp.Muted ? " (silent)" : "")}" : "off")} " +
            $"video={Video.Volume:F4}{(Video.IsMuted ? " (muted)" : "")} icon=U+{(int)MuteGlyph.Text[0]:X4}");
        VolumeSlider.Value = 50; Step("set 50%");
        ToggleMute(); Step("M");
        NudgeVolume(5); Step("Up");
        VolumeSlider.Value = 0; Step("set 0%");
        ToggleMute(); Step("M at 0%");
        _muted = wasMuted;
        VolumeSlider.Value = before;
        ApplyVolume();
        return string.Join("; ", steps);
    }

    /// <summary>Drags the playhead the way a mouse would (a move every few milliseconds) and reports what the video got asked to do.</summary>
    public async Task<string> ScrubCheckAsync()
    {
        double total = VideoDuration;
        if (_playing == null || total < 1) return "no clip to drag through";
        bool wasPlaying = _isPlaying;
        int seeksBefore = _scrubSeeks, moves = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        BeginScrub();
        while (watch.ElapsedMilliseconds < 1500)
        {
            ScrubTo(total * (0.1 + 0.8 * watch.ElapsedMilliseconds / 1500.0));
            moves++;
            await Task.Delay(4);
        }
        double target = total * 0.5;
        ScrubTo(target);
        EndScrub();
        await Task.Delay(600);
        return $"{moves} moves in {watch.ElapsedMilliseconds} ms gave {_scrubSeeks - seeksBefore} video seeks; let go at {target:F2}s, " +
               $"video now at {Video.Position:F2}s, playing={_isPlaying} (was {wasPlaying}), {PlayerDebug()}";
    }
}
