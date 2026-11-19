using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Flashback.Core;

namespace Flashback;

/// <summary>Small on-screen notice. Never takes focus and ignores the mouse, so it cannot interrupt a game.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.6) };

    public ToastWindow()
    {
        InitializeComponent();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (!_hide.IsEnabled) Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        };
        SizeChanged += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        const int GwlExStyle = -20;
        const long NoActivate = 0x08000000, Transparent = 0x20, ToolWindow = 0x80;
        long style = Win32.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | NoActivate | Transparent | ToolWindow));
    }

    public void ShowMessage(string title, string detail, bool isError)
    {
        TitleText.Text = title.ToUpperInvariant();
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        Dot.Background = new SolidColorBrush(isError ? Color.FromRgb(0xC4, 0x54, 0x3A) : Color.FromRgb(0xFF, 0xFC, 0xF7));
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        _hide.Stop();
        Show();
        Reposition();
        _hide.Start();
    }

    /// <summary>Draws the notice to a PNG without showing it (for --render-ui; showing it would flash on the user's screen).</summary>
    public void RenderPreview(string file)
    {
        TitleText.Text = "CLIP SAVED";
        DetailText.Text = "90 seconds  ·  Clip 2026-11-18 17-22-41.mp4";
        DetailText.Visibility = Visibility.Visible;
        Panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Panel.Arrange(new Rect(Panel.DesiredSize));
        Panel.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(Panel.DesiredSize.Width), (int)Math.Ceiling(Panel.DesiredSize.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Panel);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var fs = System.IO.File.Create(file);
        encoder.Save(fs);
    }

    private void Reposition()
    {
        Rect area = SystemParameters.WorkArea; // main monitor
        Left = area.Right - ActualWidth - 12;
        Top = area.Top + 12;
    }
}
