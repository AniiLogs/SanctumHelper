using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SanctumHelper;

/// <summary>Small notifications in the top-right corner of the game: each drops in from above the
/// screen edge, stays a few seconds and slides off to the right. Click-through, never takes
/// focus, and one at a time (later ones wait their turn).</summary>
public sealed class ToastWindow : Window
{
    readonly Settings settings;
    readonly Queue<(string text, Color accent)> queue = new();
    readonly TranslateTransform slide = new();
    readonly ScaleTransform scale = new();
    readonly Border card;
    readonly Border bar;
    readonly TextBlock label;
    IntPtr hwnd;
    bool showing;

    // Laid out at 1080p and scaled with the game's height.
    const double W = 340, H = 64, Margin = 18;

    public ToastWindow(Settings settings)
    {
        this.settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Sanctum Helper notification";
        Left = -32000; Top = -32000; Width = 10; Height = 10;

        bar = new Border { Width = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 10, 12, 10) };
        label = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        row.Children.Add(label);
        card = new Border
        {
            Width = W, Height = H, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 0, 16, 0),
            Background = new SolidColorBrush(Color.FromArgb(235, 20, 22, 31)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Margin, Margin, 0),
            RenderTransform = slide,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 },
            Child = row,
        };
        // The window is the corner it slides through; the card moves inside it, so it seems to
        // come from above the screen and leave past its right edge.
        Content = new Grid { ClipToBounds = true, LayoutTransform = scale, Children = { card } };
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Queues a notification over the game window (client area, physical pixels).</summary>
    public void Show(string text, Color accent, OpenCvSharp.Rect client)
    {
        if (!settings.ShowNotifications || client.Width <= 0) return;
        queue.Enqueue((text, accent));
        lastClient = client;
        if (!showing) Next();
    }

    OpenCvSharp.Rect lastClient;

    System.Windows.Threading.DispatcherTimer? gateTimer;
    bool? excluded;

    /// <summary>In screenshots and recordings unless the player hid overlays from them; out for the moment the meter
    /// takes a proof (checked 10 times a second while a card shows).</summary>
    void ApplyCaptureMode()
    {
        if (hwnd == IntPtr.Zero) return;
        bool exclude = !settings.RecordableOverlay || SharedCapture.CleanNow;
        if (excluded == exclude) return;
        excluded = exclude;
        Native.SetWindowDisplayAffinity(hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    void Next()
    {
        if (queue.Count == 0) { showing = false; gateTimer?.Stop(); OwnOverlays.Set("toast", null); Hide(); return; }
        showing = true;
        var (text, accent) = queue.Dequeue();
        label.Text = text;
        bar.Background = new SolidColorBrush(accent);

        if (hwnd == IntPtr.Zero) { base.Show(); Hide(); }
        ApplyCaptureMode();
        if (gateTimer == null)
        {
            gateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            gateTimer.Tick += (_, _) => { ApplyCaptureMode(); OwnOverlays.Tick(); };
        }
        gateTimer.Start();
        // Size and place in physical pixels, at the game's top-right corner.
        var dpi = VisualTreeHelper.GetDpi(this);
        double ui = Regions.Ui(lastClient.Width, lastClient.Height);
        scale.ScaleX = scale.ScaleY = ui / dpi.DpiScaleX;
        int w = (int)Math.Ceiling((W + Margin + 8) * ui), h = (int)Math.Ceiling((H + Margin + 16) * ui);
        base.Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, lastClient.Right - w, lastClient.Y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
        OwnOverlays.Set("toast", new OpenCvSharp.Rect(lastClient.Right - w, lastClient.Y, w, h));

        // Drop in from above, hold, slide out to the right.
        slide.X = 0;
        slide.Y = -(H + Margin + 10);
        var drop = new DoubleAnimation(slide.Y, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } };
        drop.Completed += (_, _) =>
        {
            var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            hold.Tick += (_, _) =>
            {
                hold.Stop();
                var leave = new DoubleAnimation(0, W + Margin + 20, TimeSpan.FromMilliseconds(340)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                leave.Completed += (_, _) => Next();
                slide.BeginAnimation(TranslateTransform.XProperty, leave);
            };
            hold.Start();
        };
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.BeginAnimation(TranslateTransform.YProperty, drop);
    }
}
