using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFmpegNativePlayer;

// Full-screen confidence/broadcast display driven by the authoritative FINAL PROGRAM frame.
// This does not reopen media and therefore follows playlist transitions, CG and Program processing.
public sealed class GraphicsCardOutputWindow : Window
{
    private readonly System.Windows.Controls.Image _image;

    public GraphicsCardOutputWindow()
    {
        Title = "SMART PLAYOUT • GRAPHICS CARD OUTPUT";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Black;
        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true
        };
        Content = _image;
    }

    public void ShowOnMonitor(int monitorIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) throw new InvalidOperationException("No Windows display output was detected.");
        monitorIndex = Math.Max(0, Math.Min(monitorIndex, screens.Length - 1));
        var b = screens[monitorIndex].Bounds;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void UpdateFrame(BitmapSource frame)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateFrame(frame)));
            return;
        }
        _image.Source = frame;
    }
}
