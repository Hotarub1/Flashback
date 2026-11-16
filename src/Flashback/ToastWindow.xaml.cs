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
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        Dot.Background = new SolidColorBrush(isError ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0xDB, 0x27, 0x77));
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        _hide.Stop();
        Show();
        Reposition();
        _hide.Start();
    }

    private void Reposition()
    {
        Rect area = SystemParameters.WorkArea; // main monitor
        Left = area.Right - ActualWidth - 12;
        Top = area.Top + 12;
    }
}
