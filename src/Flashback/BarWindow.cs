using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Flashback.Core;

namespace Flashback;

/// <summary>
/// Shows the player's control bar over the video in full screen. The video is a window of its own that WPF cannot
/// draw over, but another see-through window can sit on top of it: Windows blends this one over the picture, so the
/// video carries on under the bar. It only shows a live picture of the real bar, which stays in the player's window
/// (invisible, under the video): this window lets every click through, so the real bar gets them the way clicks on
/// the video always reached the player. A soft shade fades in towards the bottom so the controls stay readable.
/// </summary>
internal sealed class BarWindow : Window
{
    /// <summary>How far above the controls the shade starts.</summary>
    public const double FadeHeight = 96;

    private readonly Grid _root;
    private readonly Rectangle _picture;
    private readonly VisualBrush _brush;
    private IntPtr _hwnd;

    public BarWindow(Window owner, Visual controls)
    {
        Owner = owner;
        ThemeMode = ThemeMode.None;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x3A, 0x2B, 0x26), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0x3A, 0x2B, 0x26), 0.45));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0x3A, 0x2B, 0x26), 1));
        shade.Freeze();
        _brush = new VisualBrush(controls) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
        _picture = new Rectangle { Fill = _brush, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
        _root = new Grid { Background = shade, Children = { _picture } };
        Content = _root;
    }

    public bool Shown => _root.Visibility == Visibility.Visible;

    public void SetShown(bool shown) => _root.Visibility = shown ? Visibility.Visible : Visibility.Hidden;

    /// <summary>The real bar's size, in WPF units: its picture is drawn at that size along the bottom.</summary>
    public void SetBarSize(double width, double height)
    {
        _brush.Viewbox = new Rect(0, 0, width, height);
        _picture.Width = width;
        _picture.Height = height;
    }

    /// <summary>Puts the window at this place on the screen, in pixels.</summary>
    public void Place(int x, int y, int width, int height)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.RECT now = ScreenRect;
        if (now.Left == x && now.Top == y && now.Width == width && now.Height == height) return;
        const uint NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, NoZOrder | NoActivate | NoOwnerZOrder);
    }

    /// <summary>Where the window is on the screen, in pixels.</summary>
    public Win32.RECT ScreenRect => _hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out Win32.RECT r) ? r : default;

    /// <summary>Whether Windows sends the mouse straight through it and never makes it the active window.</summary>
    public bool ClickThrough => _hwnd != IntPtr.Zero && (Win32.GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64() & (WsExNoActivate | WsExTransparent)) == (WsExNoActivate | WsExTransparent);

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000, WsExTransparent = 0x20, WsExToolWindow = 0x80;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long style = Win32.GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExTransparent | WsExToolWindow));
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
