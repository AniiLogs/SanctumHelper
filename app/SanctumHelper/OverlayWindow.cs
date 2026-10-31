using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SanctumHelper;

/// <summary>Borderless, click-through, always-on-top window over the game's client area.
/// It never takes focus. It shows in recordings unless detection has to read the screen
/// (then it is excluded from capture so it can't feed back into detection).</summary>
public sealed class OverlayWindow : Window
{
    readonly OverlayView view;
    readonly Settings settings;
    IntPtr hwnd;
    bool forceExclude;
    OpenCvSharp.Rect placed;
    long lastTopmost;
    OverlayFrame? live;

    public OverlayWindow(LayoutDb db, Settings settings)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Sanctum Helper overlay";
        Left = -32000; Top = -32000; Width = 10; Height = 10;
        this.settings = settings;
        view = new OverlayView(db, settings);
        Content = view;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            ApplyCaptureMode();
        };
    }

    bool? excluded;
    bool overGame;     // the window spans the game and draws over what's read (the labels on the in-game map)
    bool shown;

    /// <summary>In screenshots and recordings unless the player hid overlays from them (Recordable overlay off), on any
    /// Windows. Its spot is shared (OwnOverlays): the meter and the app cover it in their pictures of the screen, and
    /// where it is over what we read (the in-game map, over the game's minimap) we read that part without it
    /// (Magnifier). Only where the magnifier gives nothing does a map over what's read stay out of screen captures.
    /// Every overlay steps out for the moment the meter takes a proof.</summary>
    public void ApplyCaptureMode(bool screenFallback = false)
    {
        forceExclude |= screenFallback;
        if (hwnd == IntPtr.Zero) return;
        bool screenCopy = forceExclude || (SharedCapture.InUse && SharedCapture.ScreenCopy);
        bool exclude = !settings.RecordableOverlay || SharedCapture.CleanNow
            || (Magnifier.Failed && (forceExclude || settings.MiniMapCorner == "OverGame" || (overGame && screenCopy)));
        OwnOverlays.InCapture("map", hwnd, shown && !exclude);
        if (excluded == exclude) return;
        excluded = exclude;
        Native.SetWindowDisplayAffinity(hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    public void Apply(OverlayFrame f)
    {
        if (hwnd == IntPtr.Zero) return;
        live = f;
        if (placing)
        {
            // Keep covering the game (or the screen) while the user drags; follow the game if it
            // shows up or moves meanwhile.
            if (f.Visible && f.Client.Width > 0 && f.Client != view.PlacementClient) Cover(f.Client);
            view.Frame = f;
            view.InvalidateVisual();
            return;
        }
        // A panel-style map only needs a window around the panel. A transparent window is redrawn
        // through a CPU copy of all its pixels, so a whole-screen one costs ~33 MB per frame at 4K
        // while the map glides: that stuttered and flickered on 4K screens.
        var want = f.Client;
        var offset = new Point();
        if (f.Visible && f.Mini && f.Layout != null && f.Client.Width > 0
            && view.MiniStyle(f, f.Layout) is string st && st is "Corner" or "Disc" or "Radar" or "Compass" or "Big" or "Overlay")
        {
            double ui = Regions.Ui(f.Client.Width, f.Client.Height), m = 70 * ui; // room for labels, rim arrows, the legend
            Rect r;
            if (st == "Big") { r = OverlayView.BigPanel(settings, f.Client.Width, f.Client.Height, ui); m = 24 * ui; }  // its caption
            else if (st == "Overlay") { r = OverlayView.SeeThroughArea(f.Client.Width, f.Client.Height); m = 0; }
            else r = view.MiniPanel(st, f.Client.Width, f.Client.Height, ui);
            r = new Rect(r.X - m, r.Y - m, r.Width + 2 * m, r.Height + 2 * m);
            r.Intersect(new Rect(0, 0, f.Client.Width, f.Client.Height));
            if (!r.IsEmpty)
            {
                want = new OpenCvSharp.Rect(f.Client.X + (int)r.X, f.Client.Y + (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
                offset = new Point((int)r.X, (int)r.Y);
            }
        }
        view.Offset = offset;
        // the window's spot, for the other programs to cover (and for us to read under it where it's over what's read)
        overGame = f.Visible && want == f.Client;
        shown = f.Visible && want.Width > 0;
        OwnOverlays.Set("map", shown ? want : null);
        ApplyCaptureMode();
        if (f.Visible && want.Width > 0 && want != placed)
        {
            placed = want;
            // Physical pixels; the view undoes WPF's DPI scaling so it can draw in them too.
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, want.X, want.Y, want.Width, want.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
        }
        else if (f.Visible && Environment.TickCount64 - lastTopmost > 250)
        {
            // Keep on top; a few times a second is plenty (frames now arrive ~40 times a second).
            lastTopmost = Environment.TickCount64;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x1 | 0x2 | Native.SWP_NOACTIVATE);
        }
        view.Frame = f;
        view.InvalidateVisual();
    }

    // ---- Placement mode: the user drags the on-screen map where they want it. The overlay is
    // normally click-through (WS_EX_TRANSPARENT); while placing it takes the mouse, but only
    // where it draws something (a layered window passes clicks through fully transparent
    // pixels), so the game stays clickable around the panel. It never takes keyboard focus
    // (WS_EX_NOACTIVATE stays): Enter/Esc are read with GetAsyncKeyState instead.
    bool placing, dragging;
    Point dragFrom;
    Rect dragStart;
    double? oldX, oldY;
    double oldSize;
    System.Windows.Threading.DispatcherTimer? keys;
    bool enterWasDown, escWasDown;

    public void BeginPlacement()
    {
        if (hwnd == IntPtr.Zero || placing) return;
        placing = true;
        oldX = settings.MiniMapX; oldY = settings.MiniMapY; oldSize = settings.MiniMapSize;
        // Over the game when it's up, else over the primary screen (sample map).
        view.Offset = new Point();
        var area = live is { Visible: true, Client.Width: > 0 } ? live.Client : PrimaryScreen();
        Cover(area);
        SetClickThrough(false);
        view.Placing = true;
        view.Frame = live;
        Cursor = System.Windows.Input.Cursors.SizeAll;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseRightButtonUp += OnRight;
        // Keys held when placement starts (e.g. Enter on a menu) don't count until released.
        enterWasDown = (Native.GetAsyncKeyState(0x0D) & 0x8000) != 0;
        escWasDown = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
        keys = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        keys.Tick += (_, _) =>
        {
            bool enter = (Native.GetAsyncKeyState(0x0D) & 0x8000) != 0, esc = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (enter && !enterWasDown) EndPlacement(true);
            else if (esc && !escWasDown) EndPlacement(false);
            enterWasDown = enter; escWasDown = esc;
        };
        keys.Start();
        view.InvalidateVisual();
        Log.Write("mini map placement started");
    }

    public void EndPlacement(bool keep)
    {
        if (!placing) return;
        placing = dragging = false;
        keys?.Stop(); keys = null;
        ReleaseMouseCapture();
        MouseLeftButtonDown -= OnDown;
        MouseMove -= OnMove;
        MouseLeftButtonUp -= OnUp;
        MouseWheel -= OnWheel;
        MouseRightButtonUp -= OnRight;
        Cursor = null;
        if (!keep) { settings.MiniMapX = oldX; settings.MiniMapY = oldY; settings.MiniMapSize = oldSize; }
        else try { settings.Save(); } catch (Exception e) { Log.Write("save placement: " + e.Message); }
        view.Placing = false;
        SetClickThrough(true);
        // Back to wherever the engine wants the overlay (the next frame moves it if needed).
        placed = default;
        if (live != null) Apply(live); else { view.Frame = null; view.InvalidateVisual(); }
        PlacementEnded?.Invoke();
        Log.Write($"mini map placement {(keep ? "saved" : "cancelled")}: {(settings.MiniMapX is double x ? $"{x:0.000},{settings.MiniMapY:0.000}" : "default")} size {settings.MiniMapSize:0}");
    }

    /// <summary>Raised when placement ends (saved or cancelled), e.g. for the settings window to refresh.</summary>
    public event Action? PlacementEnded;
    public bool IsPlacing => placing;

    void Cover(OpenCvSharp.Rect area)
    {
        view.PlacementClient = area;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
    }

    static OpenCvSharp.Rect PrimaryScreen()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new OpenCvSharp.Rect(b.X, b.Y, b.Width, b.Height);
    }

    void SetClickThrough(bool on)
    {
        long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Mouse position in the view's drawing units (physical pixels).</summary>
    Point Phys(System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(view);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X * dpi.DpiScaleX, p.Y * dpi.DpiScaleY);
    }

    void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var p = Phys(e);
        if (view.DoneButton.Contains(p)) { EndPlacement(true); return; }
        if (view.CancelButton.Contains(p)) { EndPlacement(false); return; }
        if (view.ResetButton.Contains(p))
        {
            settings.MiniMapX = settings.MiniMapY = null;
            settings.MiniMapSize = new Settings().MiniMapSize;
            view.InvalidateVisual();
            return;
        }
        if (!Rect.Inflate(view.PanelRect, 8, 8).Contains(p)) return;
        dragging = true;
        dragFrom = p;
        dragStart = view.PanelRect;
        CaptureMouse();
    }

    void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!dragging) return;
        var p = Phys(e);
        var c = view.PlacementClient;
        double ui = Regions.Ui(c.Width, c.Height);
        var moved = new Rect(dragStart.X + p.X - dragFrom.X, dragStart.Y + p.Y - dragFrom.Y, dragStart.Width, dragStart.Height);
        var fit = OverlayView.Fit(moved, c.Width, c.Height, ui);
        settings.MiniMapX = (fit.X + fit.Width / 2) / c.Width;
        settings.MiniMapY = (fit.Y + fit.Height / 2) / c.Height;
        view.InvalidateVisual();
    }

    void OnUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        dragging = false;
        ReleaseMouseCapture();
    }

    void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Resize around the panel's center (the stored position is the center).
        if (settings.MiniMapX == null)
        {
            var c = view.PlacementClient;
            var r = view.PanelRect;
            settings.MiniMapX = (r.X + r.Width / 2) / c.Width;
            settings.MiniMapY = (r.Y + r.Height / 2) / c.Height;
        }
        settings.MiniMapSize = Math.Clamp(settings.MiniMapSize + (e.Delta > 0 ? 20 : -20), 160, 720);
        view.InvalidateVisual();
    }

    void OnRight(object sender, System.Windows.Input.MouseButtonEventArgs e) => EndPlacement(true);
}

public sealed class OverlayView : FrameworkElement
{
    readonly LayoutDb db;
    readonly Settings settings;
    readonly Dictionary<(int, int), BitmapSource> images = new();
    readonly Dictionary<(int, int), BitmapSource> outlines = new();
    OverlayFrame? frame;
    public OverlayFrame? Frame
    {
        get => frame;
        set { frame = value; Animating = !Preview && value is { Mini: true, Visible: true, Player: not null }; }
    }
    /// <summary>Drawn inside the settings window: no DPI undo, no "not lined up" hint.</summary>
    public bool Preview { get; set; }
    /// <summary>UI scale used in preview mode (the live overlay follows the game's height).</summary>
    public double PreviewUi { get; set; } = 0.9;

    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly Dictionary<string, Color> Colors = new()
    {
        ["orange"] = Color.FromRgb(0xFF, 0x8A, 0x1E),
        ["blue"] = Color.FromRgb(0x3D, 0x8B, 0xFF),
        ["nest"] = Color.FromRgb(0xFF, 0x4F, 0xD8),
        ["key"] = Color.FromRgb(0xFF, 0xD3, 0x3A),
        ["legendary"] = Color.FromRgb(0xFF, 0xD3, 0x3A),
        ["luxurious"] = Color.FromRgb(0xFF, 0x9F, 0x43),
        ["exquisite"] = Color.FromRgb(0x5C, 0xC8, 0xFF),
        ["simple"] = Color.FromRgb(0xC8, 0xC8, 0xC8),
        ["pot"] = Color.FromRgb(0xC9, 0x8B, 0x5A),
        ["pile"] = Color.FromRgb(0x9B, 0xE5, 0x6A),
        ["mound"] = Color.FromRgb(0x9B, 0x86, 0x6A),
        ["player"] = Color.FromRgb(0x40, 0xFF, 0xD0),
    };

    /// <summary>Where the window sits inside the game client (physical px) when it only covers
    /// the map panel: drawing stays in client coordinates and is shifted by this.</summary>
    public Point Offset { get; set; }

    public OverlayView(LayoutDb db, Settings settings)
    {
        this.db = db;
        this.settings = settings;
        IsHitTestVisible = false;
    }

    // Hooked to the display's frame clock only while an on-screen map is up. (It was hooked for good in the
    // constructor: WPF then renders every display frame even with nothing shown, and every view made for the
    // aLogs layout browser stayed alive through the hook, each holding its decoded map: ~22 MB per layout viewed.)
    bool animating;
    bool Animating
    {
        set
        {
            if (value == animating) return;
            animating = value;
            if (value) { lastTick = DateTime.UtcNow; CompositionTarget.Rendering += Animate; }
            else { CompositionTarget.Rendering -= Animate; shown = null; shownHeading = shownCamera = null; }
        }
    }

    // The on-screen map glides toward each new position instead of jumping between fixes.
    OpenCvSharp.Point2d? shown;
    // ... and the arrow and view cone turn smoothly toward the latest readings.
    double? shownHeading, shownCamera;

    /// <summary>One display-rate step of an angle toward its target (shortest way round); true
    /// when it moved enough to redraw.</summary>
    static bool Turn(ref double? shownAngle, double? target, double a)
    {
        if (target is not double t) { bool had = shownAngle != null; shownAngle = null; return had; }
        if (shownAngle is not double cur) { shownAngle = t; return true; }
        double d = ((t - cur) % 360 + 540) % 360 - 180;
        if (Math.Abs(d) < 0.3) return false;
        shownAngle = (cur + d * a + 360) % 360;
        return true;
    }
    DateTime lastTick = DateTime.UtcNow;

    void Animate(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.1, (now - lastTick).TotalSeconds);
        lastTick = now;
        if (Frame is not { Mini: true, Visible: true, Player: OpenCvSharp.Point2d fix } fr) { shown = null; shownHeading = shownCamera = null; return; }
        var (liveHeading, liveCamera, liveStamp) = HeadingFeed.Get();
        bool live = (now - liveStamp).TotalSeconds < 1.5;
        // ~15 ms (was 35): readings now come ~60 a second already smoothed, so this only bridges
        // display frames; 35 ms trailed a fast camera turn by ~12 degrees on its own.
        double ta = 1 - Math.Exp(-dt / 0.015);
        bool turned = Turn(ref shownHeading, live ? liveHeading : fr.Heading, ta);
        turned |= Turn(ref shownCamera, live ? liveCamera : fr.Camera, ta);
        if (turned) InvalidateVisual();
        // Keep moving along the current velocity between fixes (up to 0.25 s ahead), so the map
        // glides at display rate instead of stepping at the tracking rate.
        var target = fix;
        if (fr.Velocity is OpenCvSharp.Point2d v && fr.Stamp != default)
        {
            double ahead = Math.Clamp((now - fr.Stamp).TotalSeconds, 0, 0.15);
            target = new OpenCvSharp.Point2d(fix.X + v.X * ahead, fix.Y + v.Y * ahead);
        }
        if (shown is not OpenCvSharp.Point2d cur || Math.Abs(cur.X - target.X) + Math.Abs(cur.Y - target.Y) > 300)
        {
            shown = target; // first fix or a confirmed teleport: snap
            InvalidateVisual();
            return;
        }
        if (Math.Abs(cur.X - target.X) + Math.Abs(cur.Y - target.Y) < 0.3) return;
        // ~35 ms (was 70): smooths ticks and small corrections, the tracker already filters. Fixes come
        // ~40 a second now (were ~20), so less is needed; 70 ms trailed a walking player by 70 ms.
        double a = 1 - Math.Exp(-dt / 0.035);
        shown = new OpenCvSharp.Point2d(cur.X + (target.X - cur.X) * a, cur.Y + (target.Y - cur.Y) * a);
        InvalidateVisual();
    }

    /// <summary>Where a floor image goes: the ground floor spans the layout's full image size
    /// (even when decoded smaller); upper floors are their own (sometimes cropped) size.</summary>
    static Rect ImageRect(Layout l, int floor, BitmapSource bmp, double tx, double ty, double scale) =>
        floor == 0 || LayoutDb.FloorCount(l) == 1
            ? new Rect(tx, ty, l.ImageSize[0] * scale, l.ImageSize[1] * scale)
            : new Rect(tx, ty, bmp.PixelWidth * scale, bmp.PixelHeight * scale);

    BitmapSource Image(Layout l, int floor = 0)
    {
        floor = Math.Clamp(floor, 0, LayoutDb.FloorCount(l) - 1);
        if (!images.TryGetValue((l.Number, floor), out var bmp))
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(db.FloorPath(l, floor));
            // The island art is 6144 px wide: half resolution is plenty for the on-screen map and
            // keeps redrawing it at display rate cheap.
            if (l.IsIsland) bi.DecodePixelWidth = l.ImageSize[0] / 2;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            Forget(images, l);
            images[(l.Number, floor)] = bmp = bi;
        }
        return bmp;
    }

    /// <summary>Wall outlines of a layout (Diablo-style see-through map): light edges with a faint
    /// floor tint, transparent everywhere else.</summary>
    BitmapSource Outline(Layout l, int level = 0)
    {
        level = Math.Clamp(level, 0, LayoutDb.FloorCount(l) - 1);
        if (outlines.TryGetValue((l.Number, level), out var bmp)) return bmp;
        using var rgba = OpenCvSharp.Cv2.ImRead(db.FloorPath(l, level), OpenCvSharp.ImreadModes.Unchanged);
        var ch = rgba.Split();
        using var floor = new OpenCvSharp.Mat();
        // Floors are light, walls/void dark: a floor mask, then its border is the wall line.
        using var gray = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Merge([ch[0], ch[1], ch[2]], gray);
        OpenCvSharp.Cv2.CvtColor(gray, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
        OpenCvSharp.Cv2.Threshold(gray, floor, 120, 255, OpenCvSharp.ThresholdTypes.Binary);
        OpenCvSharp.Cv2.BitwiseAnd(floor, ch[3], floor);
        using var k = OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Ellipse, new OpenCvSharp.Size(5, 5));
        OpenCvSharp.Cv2.MorphologyEx(floor, floor, OpenCvSharp.MorphTypes.Open, k);
        using var edge = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.MorphologyEx(floor, edge, OpenCvSharp.MorphTypes.Gradient, OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Ellipse, new OpenCvSharp.Size(5, 5)));
        using var outM = new OpenCvSharp.Mat(floor.Size(), OpenCvSharp.MatType.CV_8UC4, new OpenCvSharp.Scalar(0, 0, 0, 0));
        outM.SetTo(new OpenCvSharp.Scalar(30, 26, 22, 34), floor);          // faint floor tint (premultiplied BGRA)
        outM.SetTo(new OpenCvSharp.Scalar(235, 225, 190, 235), edge);       // soft blue-white walls
        foreach (var c in ch) c.Dispose();
        var bytes = new byte[outM.Total() * 4];
        System.Runtime.InteropServices.Marshal.Copy(outM.Data, bytes, 0, bytes.Length);
        bmp = BitmapSource.Create(outM.Width, outM.Height, 96, 96, PixelFormats.Pbgra32, null, bytes, outM.Width * 4);
        bmp.Freeze();
        Forget(outlines, l);
        return outlines[(l.Number, level)] = bmp;
    }

    /// <summary>Drops the decoded images of other layouts (each map is ~16-22 MB decoded; they used to pile up run
    /// after run). The island's stay: the player goes back and forth between it and the Sanctum.</summary>
    void Forget(Dictionary<(int, int), BitmapSource> cache, Layout keep)
    {
        int island = db.Island?.Number ?? int.MinValue;
        foreach (var k in cache.Keys.Where(k => k.Item1 != keep.Number && k.Item1 != island).ToList()) cache.Remove(k);
    }

    public static readonly PerfStat Perf = new();

    protected override void OnRender(DrawingContext dc)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RenderAll(dc); }
        finally { Perf.Add(System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
    }

    void RenderAll(DrawingContext dc)
    {
        var f = Frame;
        if (Placing)
        {
            // Drag the live map if one is up, else a sample; never the full-map overlay.
            if (f is not { Visible: true, Mini: true, Big: false, Layout: not null, Player: not null }) f = SampleFrame();
            else f = f with { Client = PlacementClient.Width > 0 ? PlacementClient : f.Client };
        }
        if (f == null || !f.Visible) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        dc.PushTransform(Preview ? Transform.Identity : new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
        dc.PushTransform(Placing || Preview ? Transform.Identity : new TranslateTransform(-Offset.X, -Offset.Y));
        double ui = Preview && !f.Mini ? PreviewUi : Regions.Ui(f.Client.Width, f.Client.Height); // UI scale, matching the game's
        double ms = settings.MarkerScale * ui;
        if (f.Mini && f.Layout != null && f.Player is OpenCvSharp.Point2d me)
        {
            if (!Preview && f == Frame) f = f with { Heading = shownHeading ?? f.Heading, Camera = shownCamera ?? f.Camera };
            RenderMini(dc, f, f.Layout, Preview || f.Player != Frame?.Player ? me : shown ?? me, ui);
            dc.Pop();
            dc.Pop();
            return;
        }

        // Placement: aligned to the in-game map when found, otherwise a centered panel.
        double scale, tx, ty;
        bool aligned = f.Pose != null;
        var layout = f.Pose?.Layout ?? f.Layout;
        if (f.Pose != null) { scale = f.Pose.Scale; tx = f.Pose.Tx; ty = f.Pose.Ty; }
        else if (layout != null)
        {
            // Fit the occupied part of the map into 80% of the screen.
            var pts = (layout.Rooms.Count > 0 ? layout.Rooms.Select(r => r.Img) : layout.Markers.Select(m => m.Img)).ToList();
            double minX = pts.Min(p => p[0]) - 120, maxX = pts.Max(p => p[0]) + 120;
            double minY = pts.Min(p => p[1]) - 120, maxY = pts.Max(p => p[1]) + 120;
            double fill = Preview ? 0.92 : 0.8;
            scale = Math.Min(f.Client.Width * fill / (maxX - minX), f.Client.Height * fill / (maxY - minY));
            tx = f.Client.Width / 2.0 - (minX + maxX) / 2 * scale;
            ty = f.Client.Height / 2.0 - (minY + maxY) / 2 * scale;
            if (!Preview) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 12, 14, 22)), null,
                new Rect(minX * scale + tx - 16, minY * scale + ty - 16, (maxX - minX) * scale + 32, (maxY - minY) * scale + 32), 14, 14);
        }
        else
        {
            Banner(dc, f, ui, f.Status);
            dc.Pop();
            return;
        }

        var L = layout!;
        Point P(double[] img) => new(img[0] * scale + tx, img[1] * scale + ty);

        if ((settings.ShowMapImage && !L.IsIsland) || !aligned)
        {
            var bmp = Image(L, f.Floor);
            dc.PushOpacity(aligned ? settings.MapOpacity : 0.95);
            dc.DrawImage(bmp, ImageRect(L, f.Floor, bmp, tx, ty, scale));
            dc.Pop();
        }

        DrawMarkers(dc, L, P, ms, settings.ShowLabels);

        if (settings.ShowPlayer && f.Player is OpenCvSharp.Point2d pl)
        {
            var p = new Point(pl.X * scale + tx, pl.Y * scale + ty);
            Player(dc, p, f.Heading, 40 * ms, f.PlayerFresh);
            if (settings.ShowLabels) Label(dc, Lang.T("You"), new Point(p.X, p.Y - 22 * ms), Colors["player"], 12 * ms, above: true);
        }

        Legend(dc, f, L, ui, aligned);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>The on-screen map styles, in the order the settings window lists them:
    /// (setting value, display name, description).</summary>
    public static readonly (string Value, string Name, string Description)[] MiniMapStyles =
    [
        ("Corner", "Corner map", "A framed north-up map of the area around you, like Path of Exile. Drag it anywhere."),
        ("Disc", "Round minimap", "A round map that turns with you, so up is always the way you face."),
        ("Radar", "Radar", "Just the doors, key rooms and nests around you on a faint outline, with arrows and distances to the ones out of range."),
        ("Compass", "Compass strip", "A strip along the top of the screen showing which way, and how far, the doors, key rooms and nests are."),
        ("Overlay", "See-through (Diablo)", "Faint wall outlines over the middle of the screen, centered on your character."),
        ("Off", "Off", "No on-screen map while the full map is closed."),
    ];

    // ---- Placement mode: the user drags the on-screen map where they want it (OverlayWindow
    // feeds the mouse in; the view draws the frame and buttons and says where they are).
    public bool Placing { get; set; }
    /// <summary>Game client (or screen) the overlay covers while placing, in physical pixels.</summary>
    public OpenCvSharp.Rect PlacementClient { get; set; }
    /// <summary>Last drawn panel and placement buttons, in the view's (physical pixel) coordinates.</summary>
    public Rect PanelRect { get; private set; }
    public Rect DoneButton { get; private set; }
    public Rect ResetButton { get; private set; }
    public Rect CancelButton { get; private set; }

    /// <summary>The style actually drawn: the see-through style doesn't work on the busy,
    /// full-color island art (corner map there), and placing shows a panel to drag.</summary>
    public string MiniStyle(OverlayFrame f, Layout L)
    {
        if (f.Big) return "Big";
        string s = settings.MiniMapMode;
        if (!MiniMapStyles.Any(x => x.Value == s)) s = "Corner";
        if (s is "Overlay" or "Off" && (L.IsIsland || Placing)) s = "Corner";
        return s;
    }

    /// <summary>Experimental "over the game's minimap" placement, for the corner and round maps (not while placing).</summary>
    bool CoversGameMap(string style) => settings.MiniMapCorner == "OverGame" && style is "Corner" or "Disc" && !Placing;

    /// <summary>The on-screen map's opacity: solid while it covers the game's minimap, so that doesn't show through.</summary>
    double MiniOpacity => settings.MiniMapCorner == "OverGame" && settings.MiniMapMode is "Corner" or "Disc" && !Placing
        ? 1.0 : settings.MiniMapOpacity;

    /// <summary>Size of the panel for a style, in screen pixels.</summary>
    Size PanelSize(string style, double ui)
    {
        double size = settings.MiniMapSize * ui;
        return style == "Compass" ? new Size(size * 2.4, 62 * ui) : new Size(size, size);
    }

    /// <summary>Where the panel goes: the user's spot if they placed it, else the default (top
    /// right, clear of the party portraits, or top center). Always kept on screen and off the
    /// game's own minimap and quest text in the top-left corner.</summary>
    public Rect MiniPanel(string style, double clientW, double clientH, double ui)
    {
        if (CoversGameMap(style))
        {
            // exactly over the game's minimap (where it's read from), as a square around its center
            var m = Regions.ToClient(settings.MinimapRegion, Anchor.TopLeft, (int)clientW, (int)clientH);
            double side = Math.Max(m.Width, m.Height);
            return new Rect(m.X + m.Width / 2.0 - side / 2, m.Y + m.Height / 2.0 - side / 2, side, side);
        }
        var sz = PanelSize(style, ui);
        Point c;
        if (settings.MiniMapX is double fx && settings.MiniMapY is double fy) c = new Point(fx * clientW, fy * clientH);
        else if (style == "Compass") c = new Point(clientW / 2, 96 * ui);
        else
        {
            // Clear of the party portraits at the right edge of the game's UI area (not the window's: on 21:9 the
            // UI is a centered 16:9 box).
            var hud = Regions.Hud((int)clientW, (int)clientH);
            double x = settings.MiniMapCorner == "TopCenter" ? (clientW - sz.Width) / 2 : hud.Right - sz.Width - 240 * ui;
            c = new Point(x + sz.Width / 2, 105 * ui + sz.Height / 2);
        }
        return Fit(new Rect(c.X - sz.Width / 2, c.Y - sz.Height / 2, sz.Width, sz.Height), clientW, clientH, ui);
    }

    /// <summary>Keeps a panel on screen, snaps it to an edge it's close to, and moves it out of
    /// the game's minimap/quest area (top-left), whichever way is the shorter move.</summary>
    public static Rect Fit(Rect r, double clientW, double clientH, double ui)
    {
        double m = 12 * ui, snap = 28 * ui;
        double x = Math.Clamp(r.X, m, Math.Max(m, clientW - r.Width - m));
        double y = Math.Clamp(r.Y, m, Math.Max(m, clientH - r.Height - m));
        if (x - m < snap) x = m;
        if (clientW - m - (x + r.Width) < snap) x = clientW - m - r.Width;
        if (y - m < snap) y = m;
        if (clientH - m - (y + r.Height) < snap) y = clientH - m - r.Height;
        // The game's minimap and quest text, at the left of its UI area.
        var hud = new Rect(Regions.Hud((int)clientW, (int)clientH).X, 0, 300 * ui, 260 * ui);
        if (new Rect(x, y, r.Width, r.Height).IntersectsWith(hud))
        {
            double right = hud.Right + m - x, down = hud.Bottom + m - y;
            if (right <= down) x += right; else y += down;
        }
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>A sample frame to drag around when nothing is being tracked (no run, no game).</summary>
    OverlayFrame SampleFrame()
    {
        var c = PlacementClient;
        var l = db.ByNumber(53) ?? db.Layouts.First(x => !x.IsIsland);
        var at = l.KeyRooms.FirstOrDefault()?.Door?.Img ?? l.Orange.Img;
        return new OverlayFrame(new OpenCvSharp.Rect(c.X, c.Y, Math.Max(1, c.Width), Math.Max(1, c.Height)), true, l, null,
            new OpenCvSharp.Point2d(at[0] + 40, at[1] + 30), true, null, "", Mini: true, Heading: 35);
    }

    /// <summary>The top of the game the meter reads (the boss bar, its break gauge and counter, the timer): the big map
    /// and the see-through outlines stay below it, as the meter covers our maps in its picture of the screen.</summary>
    public const double MeterBand = 0.13;

    /// <summary>Big map: a large panel in the middle, clear of the HUD corner on the left, (with its caption) below the
    /// meter's band at the top, and above the game's own bar at the bottom (where the default size always ended).</summary>
    public static Rect BigPanel(Settings settings, double w, double h, double ui)
    {
        double top = MeterBand * h + 24 * ui, bottom = 0.9 * h;
        double size = Math.Max(1, Math.Min(Math.Min(settings.BigMapSize * h, w - 2 * 340 * ui), bottom - top));
        double y = Math.Clamp((h - size) / 2, top, Math.Max(top, bottom - size));
        return new Rect((w - size) / 2, y, size, size);
    }

    /// <summary>Where the see-through outlines can show: the circle they fade out in (around the character, a little
    /// below the screen center), from the meter's band down.</summary>
    public static Rect SeeThroughArea(double w, double h)
    {
        double r = h * 0.55, cx = w / 2, cy = h * 0.56, top = MeterBand * h;
        var a = new Rect(cx - r, top, 2 * r, cy + r - top);
        a.Intersect(new Rect(0, 0, w, h));
        return a.IsEmpty ? new Rect(0, top, w, h - top) : a;
    }

    /// <summary>On-screen map while the full map is closed, centered on the player: one of
    /// MiniMapStyles, or the big zoom map while the key is held.</summary>
    void RenderMini(DrawingContext dc, OverlayFrame f, Layout L, OpenCvSharp.Point2d me, double ui)
    {
        string style = MiniStyle(f, L);
        bool center = style == "Overlay";
        // Same metres per screen pixel on the Sanctum (6.1 px/m art) and the island (4 px/m art).
        double zoom = f.Big ? settings.BigMapZoom : center ? settings.OverlayZoom : settings.MiniMapZoom;
        double scale = zoom * ui * (6.1 / Math.Max(1, L.PxPerMetre));
        double ms = settings.MarkerScale * ui * (f.Big ? 0.85 : center ? 0.75 : 0.6);
        Rect panel;
        if (center) panel = new Rect(0, 0, f.Client.Width, f.Client.Height);
        else if (f.Big) panel = BigPanel(settings, f.Client.Width, f.Client.Height, ui);
        else panel = MiniPanel(style, f.Client.Width, f.Client.Height, ui);
        PanelRect = panel;

        switch (style)
        {
            case "Overlay":
                // The character stands a little below the screen center in the game's camera.
                RenderSeeThrough(dc, f, L, me, new Point(f.Client.Width / 2.0, f.Client.Height * 0.56), scale, ms, ui);
                break;
            case "Disc": RenderDisc(dc, f, L, me, panel, scale, ms, ui); break;
            case "Radar": RenderRadar(dc, f, L, me, panel, scale, ms, ui); break;
            case "Compass": RenderCompass(dc, f, L, me, panel, ui); break;
            default: RenderPanel(dc, f, L, me, panel, scale, ms, ui); break;
        }
        if (Placing) PlacementChrome(dc, panel, style, ui);
    }

    /// <summary>Corner map (PoE-style) and the big map: a framed, north-up square.</summary>
    void RenderPanel(DrawingContext dc, OverlayFrame f, Layout L, OpenCvSharp.Point2d me, Rect panel, double scale, double ms, double ui)
    {
        var c = new Point(panel.X + panel.Width / 2, panel.Y + panel.Height / 2);
        double tx = c.X - me.X * scale, ty = c.Y - me.Y * scale;
        Point P(double[] img) => new(img[0] * scale + tx, img[1] * scale + ty);
        var clip = new RectangleGeometry(panel, 18 * ui, 18 * ui);
        dc.PushOpacity(MiniOpacity);
        dc.DrawGeometry(PanelFill, null, clip);
        dc.PushClip(clip);
        var bmp = Image(L, f.Floor);
        dc.PushOpacity(0.9);
        dc.DrawImage(bmp, ImageRect(L, f.Floor, bmp, tx, ty, scale));
        dc.Pop();
        DrawMarkers(dc, L, P, ms, labels: f.Big && settings.ShowLabels);
        // You: the game's arrow, turned the way you face, under the camera's view cone.
        ViewCone(dc, c, f.Camera, (f.Big ? 130 : 70) * ui);
        Player(dc, c, f.Heading, (f.Big ? 44 : 34) * ui);
        dc.Pop(); // clip
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.5 * ui), panel, 18 * ui, 18 * ui);
        Label(dc, "N", new Point(panel.X + panel.Width / 2, panel.Y + 4 * ui), System.Windows.Media.Colors.White, 13 * ui, above: false);
        Caption(dc, f, L, panel, ui);
        dc.Pop(); // opacity
    }

    /// <summary>Diablo-style: faint outlines that fade out towards the screen edges, so the game
    /// stays the focus and nothing ends in a hard edge.</summary>
    void RenderSeeThrough(DrawingContext dc, OverlayFrame f, Layout L, OpenCvSharp.Point2d me, Point c, double scale, double ms, double ui)
    {
        double tx = c.X - me.X * scale, ty = c.Y - me.Y * scale;
        Point P(double[] img) => new(img[0] * scale + tx, img[1] * scale + ty);
        var outline = Outline(L, f.Floor);
        var fade = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c,
            RadiusX = f.Client.Height * 0.55, RadiusY = f.Client.Height * 0.55,
        };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.45));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
        // and out towards the top, gone by the meter's band (the boss bar), where the window ends
        var area = SeeThroughArea(f.Client.Width, f.Client.Height);
        var top = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, area.Top), EndPoint = new Point(0, area.Top + 0.08 * f.Client.Height),
        };
        top.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
        top.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 1.0));
        dc.PushOpacityMask(top);
        dc.PushOpacityMask(fade);
        dc.PushOpacity(settings.OverlayOpacity);
        dc.DrawImage(outline, new Rect(tx, ty, outline.PixelWidth * scale, outline.PixelHeight * scale));
        DrawMarkers(dc, L, P, ms, labels: false);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        ViewCone(dc, c, f.Camera, 150 * ui);
        Player(dc, c, f.Heading, 46 * ui);
    }

    /// <summary>Round minimap that turns with the player (heading up, like a sat-nav), with north
    /// marked on the rim. North-up until the facing direction is known.</summary>
    void RenderDisc(DrawingContext dc, OverlayFrame f, Layout L, OpenCvSharp.Point2d me, Rect panel, double scale, double ms, double ui)
    {
        var c = new Point(panel.X + panel.Width / 2, panel.Y + panel.Height / 2);
        double r = panel.Width / 2;
        double turn = f.Heading is double h ? -h : 0;
        double tx = c.X - me.X * scale, ty = c.Y - me.Y * scale;
        Point P(double[] img) => new(img[0] * scale + tx, img[1] * scale + ty);
        var clip = new EllipseGeometry(c, r, r);
        dc.PushOpacity(MiniOpacity);
        dc.DrawGeometry(PanelFill, null, clip);
        dc.PushClip(clip);
        dc.PushTransform(new RotateTransform(turn, c.X, c.Y));
        spin = -turn; // icons and labels stay upright
        var bmp = Image(L, f.Floor);
        dc.PushOpacity(0.9);
        dc.DrawImage(bmp, ImageRect(L, f.Floor, bmp, tx, ty, scale));
        dc.Pop();
        DrawMarkers(dc, L, P, ms, labels: false);
        spin = 0;
        dc.Pop(); // rotation
        // A soft vignette so the edge reads as a lens, not a cut.
        var vignette = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c, RadiusX = r, RadiusY = r };
        vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 8, 10, 18), 0.78));
        vignette.GradientStops.Add(new GradientStop(Color.FromArgb(170, 8, 10, 18), 1.0));
        dc.DrawEllipse(vignette, null, c, r, r);
        // The map turns with the heading, so the camera's cone turns with it.
        ViewCone(dc, c, f.Camera is double cam ? cam + turn : null, r * 0.45);
        Player(dc, c, f.Heading == null ? null : 0, 34 * ui);
        dc.Pop(); // clip
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1.5 * ui), c, r, r);
        // North on the rim, where north now is.
        double a = turn * Math.PI / 180;
        var n = new Point(c.X + Math.Sin(a) * r, c.Y - Math.Cos(a) * r);
        dc.DrawEllipse(Disc, DiscRing, n, 10 * ui, 10 * ui);
        Label(dc, "N", new Point(n.X, n.Y - 8 * ui), Color.FromRgb(0xFF, 0x6B, 0x5E), 12 * ui, above: false);
        Caption(dc, f, L, panel, ui);
        dc.Pop(); // opacity
    }

    /// <summary>Radar: no map art, only a faint outline and what matters (doors, key doors, nests,
    /// the timed challenge; the boat and entrances outside). Anything out of range sits on the rim
    /// with an arrow and its distance, so the way to it is always on screen.</summary>
    void RenderRadar(DrawingContext dc, OverlayFrame f, Layout L, OpenCvSharp.Point2d me, Rect panel, double scale, double ms, double ui)
    {
        var c = new Point(panel.X + panel.Width / 2, panel.Y + panel.Height / 2);
        double r = panel.Width / 2;
        double tx = c.X - me.X * scale, ty = c.Y - me.Y * scale;
        var clip = new EllipseGeometry(c, r, r);
        dc.PushOpacity(MiniOpacity);
        dc.DrawGeometry(RadarFill, null, clip);
        dc.PushClip(clip);
        if (L.IsIsland)
        {
            var bmp = Image(L, f.Floor);
            dc.PushOpacity(0.3);
            dc.DrawImage(bmp, ImageRect(L, f.Floor, bmp, tx, ty, scale));
            dc.Pop();
        }
        else
        {
            var outline = Outline(L, f.Floor);
            dc.PushOpacity(0.55);
            dc.DrawImage(outline, new Rect(tx, ty, outline.PixelWidth * scale, outline.PixelHeight * scale));
            dc.Pop();
        }
        // Range rings with their distance in metres, and a crosshair.
        double metresPerPx = 1 / (scale * Math.Max(1, L.PxPerMetre));
        foreach (var k in new[] { 1 / 3.0, 2 / 3.0 })
        {
            dc.DrawEllipse(null, RadarRing, c, r * k, r * k);
            Label(dc, $"{r * k * metresPerPx:0} m", new Point(c.X, c.Y - r * k - 1 * ui), Color.FromArgb(150, 180, 230, 255), 9.5 * ui, above: true);
        }
        dc.DrawLine(RadarRing, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
        dc.DrawLine(RadarRing, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
        dc.Pop(); // clip

        double icon = 36 * ms;
        var rim = Nearest(Targets(L), me, L.IsIsland).ToHashSet();
        foreach (var t in Targets(L))
        {
            var p = new Point(t.Img[0] * scale + tx, t.Img[1] * scale + ty);
            double dx = p.X - c.X, dy = p.Y - c.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= r - icon * 0.55) { Icon(dc, t.Icon, p, icon * t.Size); continue; }
            if (!rim.Contains(t)) continue;
            // Out of range: on the rim, pointing the way, with how far.
            double metres = Dist(t.Img, me) / Math.Max(1, L.PxPerMetre);
            double ux = dx / d, uy = dy / d;
            var at = new Point(c.X + ux * (r - icon * 0.45), c.Y + uy * (r - icon * 0.45));
            var tip = new Point(c.X + ux * (r + 9 * ui), c.Y + uy * (r + 9 * ui));
            var bl = new Point(c.X + ux * (r + 1 * ui) - uy * 6 * ui, c.Y + uy * (r + 1 * ui) + ux * 6 * ui);
            var br = new Point(c.X + ux * (r + 1 * ui) + uy * 6 * ui, c.Y + uy * (r + 1 * ui) - ux * 6 * ui);
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(tip, true, true); g.LineTo(bl, true, false); g.LineTo(br, true, false); }
            dc.DrawGeometry(new SolidColorBrush(t.Color), new Pen(Brushes.Black, 1 * ui), tri);
            Icon(dc, t.Icon, at, icon * t.Size * 0.8, 0.95);
            Label(dc, $"{metres:0} m", new Point(at.X - ux * icon * 0.55, at.Y - uy * icon * 0.55 + 4 * ui), t.Color, 10 * ui, above: false);
        }
        ViewCone(dc, c, f.Camera, r * 0.45);
        Player(dc, c, f.Heading, 32 * ui);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(170, 120, 210, 255)), 1.5 * ui), c, r, r);
        Label(dc, "N", new Point(c.X, panel.Y + 4 * ui), System.Windows.Media.Colors.White, 12 * ui, above: false);
        Caption(dc, f, L, panel, ui);
        dc.Pop(); // opacity
    }

    /// <summary>Compass strip (like an open-world game's): the way you face in the middle, 90° to
    /// each side, and where the doors, key doors and nests are with their distance. Anything behind
    /// you is pinned to the nearer end.</summary>
    void RenderCompass(DrawingContext dc, OverlayFrame f, Layout L, OpenCvSharp.Point2d me, Rect panel, double ui)
    {
        double facing = f.Heading ?? 0;
        double cx = panel.X + panel.Width / 2, half = panel.Width / 2;
        double ppd = half / 90; // pixels per degree
        double Rel(double bearing) => ((bearing - facing) % 360 + 540) % 360 - 180;

        dc.PushOpacity(MiniOpacity);
        // Fades out at both ends instead of stopping in a hard box.
        var ends = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        ends.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        ends.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.12));
        ends.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.88));
        ends.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        dc.PushOpacityMask(ends);
        dc.DrawRoundedRectangle(PanelFill, null, panel, panel.Height / 2, panel.Height / 2);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1 * ui), new Point(panel.X, panel.Bottom - 1), new Point(panel.Right, panel.Bottom - 1));
        // Ticks every 15°, letters every 45°.
        string[] names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        for (int deg = 0; deg < 360; deg += 15)
        {
            double rel = Rel(deg);
            if (Math.Abs(rel) > 90) continue;
            double x = cx + rel * ppd;
            bool major = deg % 45 == 0;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(major ? (byte)200 : (byte)110, 255, 255, 255)), (major ? 2 : 1) * ui),
                new Point(x, panel.Y + 4 * ui), new Point(x, panel.Y + (major ? 13 : 9) * ui));
            if (major) Label(dc, names[deg / 45], new Point(x, panel.Y + 14 * ui), deg == 0 ? Color.FromRgb(0xFF, 0x6B, 0x5E) : System.Windows.Media.Colors.White, (deg % 90 == 0 ? 13 : 10.5) * ui, above: false);
        }
        dc.Pop(); // mask
        // The way you face: a small marker in the middle.
        var mark = new StreamGeometry();
        using (var g = mark.Open())
        {
            g.BeginFigure(new Point(cx, panel.Y + 9 * ui), true, true);
            g.LineTo(new Point(cx - 6 * ui, panel.Y - 1 * ui), true, false);
            g.LineTo(new Point(cx + 6 * ui, panel.Y - 1 * ui), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Colors["player"]), new Pen(Brushes.Black, 1 * ui), mark);

        double icon = 26 * ui;
        int pinnedLeft = 0, pinnedRight = 0;
        var rows = new[] { new List<double>(), new List<double>() };
        // Nearest first for the pinned ones (they take the end slots), drawn far to near otherwise.
        var all = Nearest(Targets(L), me, L.IsIsland).Select(t => (t, rel: Rel(Math.Atan2(t.Img[0] - me.X, -(t.Img[1] - me.Y)) * 180 / Math.PI), d: Dist(t.Img, me))).ToList();
        foreach (var (t, rel, _) in all.Where(x => Math.Abs(x.rel) > 90).OrderBy(x => x.d).Concat(all.Where(x => Math.Abs(x.rel) <= 90).OrderByDescending(x => x.d)))
        {
            bool behind = Math.Abs(rel) > 90;
            double x = cx + Math.Clamp(rel, -90, 90) * ppd;
            // Behind you: pinned inside the nearer end, side by side so they don't overlap.
            if (behind) x -= Math.Sign(rel) * icon * (0.6 + 1.5 * (rel > 0 ? pinnedRight++ : pinnedLeft++));
            var p = new Point(x, panel.Y + panel.Height * 0.66);
            double metres = Dist(t.Img, me) / Math.Max(1, L.PxPerMetre);
            Icon(dc, t.Icon, p, icon * t.Size, behind ? 0.6 : 1);
            if (behind && (rel > 0 ? pinnedRight : pinnedLeft) == 1) // one chevron per end, outside the first pin
                Label(dc, rel > 0 ? "›" : "‹", new Point(x + Math.Sign(rel) * icon * 0.75, p.Y - 12 * ui), System.Windows.Media.Colors.White, 18 * ui, above: false);
            // Distances in up to two rows under the strip; one that fits neither is left out.
            double lw = 34 * ui;
            int row = rows[0].Any(o => Math.Abs(o - x) < lw) ? rows[1].Any(o => Math.Abs(o - x) < lw) ? -1 : 1 : 0;
            if (row < 0) continue;
            rows[row].Add(x);
            Label(dc, $"{metres:0} m", new Point(x, panel.Bottom + (2 + 12 * row) * ui), t.Color, 10.5 * ui, above: false);
        }
        // Below the two rows of distances (above needs no extra room).
        Caption(dc, f, L, panel, ui, settings.CaptionPosition == "Above" ? 0 : 28 * ui);
        dc.Pop(); // opacity
    }

    /// <summary>On the island there are five of each entrance and eight rare chests: pointing at
    /// all of them is noise, so only the nearest of each kind (the Sanctum's few are all kept).</summary>
    static IEnumerable<(string Icon, double[] Img, Color Color, double Size)> Nearest(
        IEnumerable<(string Icon, double[] Img, Color Color, double Size)> targets, OpenCvSharp.Point2d me, bool island) =>
        island ? targets.GroupBy(t => t.Icon).Select(g => g.MinBy(t => Dist(t.Img, me))) : targets;

    static double Dist(double[] img, OpenCvSharp.Point2d me) => Math.Sqrt(Math.Pow(img[0] - me.X, 2) + Math.Pow(img[1] - me.Y, 2));

    /// <summary>What the radar and compass point at, honouring the layer toggles.</summary>
    IEnumerable<(string Icon, double[] Img, Color Color, double Size)> Targets(Layout L)
    {
        if (L.IsIsland)
        {
            foreach (var m in L.Markers)
            {
                if (m.Kind == "entrance_main" && settings.Layer("doors")) yield return ("door_orange", m.Img, Colors["orange"], 1);
                else if (m.Kind == "entrance_side" && settings.Layer("doors")) yield return ("door_blue", m.Img, Colors["blue"], 1);
                else if (m.Kind == "boat") yield return ("boat", m.Img, Color.FromRgb(0xB8, 0xF5, 0x6E), 1.1);
                else if (m.Kind == "rare" && settings.Layer("legendary")) yield return ("treasure", m.Img, Colors["legendary"], 0.9);
            }
            yield break;
        }
        if (settings.Layer("doors") && L.HasDoors)
        {
            yield return ("door_blue", L.Blue.Img, Colors["blue"], 1.1);
            yield return ("door_orange", L.Orange.Img, Colors["orange"], 1);
        }
        if (settings.Layer("key"))
            foreach (var r in L.KeyRooms)
                yield return ("key", r.Door?.Img ?? r.Img, KeyColor(r), 1.2);
        if (settings.Layer("challenge"))
            foreach (var r in L.Rooms.Where(r => r.Kind == "challenge")) yield return ("challenge", r.Img, Color.FromRgb(0xE9, 0xEA, 0xF2), 1);
        if (settings.Layer("nest"))
            foreach (var m in L.Markers.Where(m => m.Kind == "nest")) yield return ("nest", m.Img, Colors["nest"], 1);
    }

    static Color KeyColor(Room r) => r.Door?.Color switch
    {
        "purple" => Color.FromRgb(0xC0, 0x7C, 0xFF),
        "blue" => Color.FromRgb(0x5C, 0xB8, 0xFF),
        _ => Colors["key"],
    };

    /// <summary>Layout name under a panel.</summary>
    /// <summary>The line above or below a map panel: which Sanctum and the difficulty, as chosen in
    /// the settings. gap: extra room the style needs (the compass has distances under it).</summary>
    void Caption(DrawingContext dc, OverlayFrame f, Layout L, Rect panel, double ui, double gap = 0)
    {
        if (settings.CaptionPosition == "Off") return;
        var parts = new List<string>();
        if (settings.CaptionLayout) parts.Add(L.DisplayTitle);
        if (settings.CaptionDifficulty && f.Difficulty is string d && !string.IsNullOrEmpty(d)) parts.Add(Lang.Difficulty(d));
        if (parts.Count == 0) return;
        bool above = settings.CaptionPosition == "Above";
        var at = new Point(panel.X + panel.Width / 2, above ? panel.Top - 4 * ui - gap : panel.Bottom + 4 * ui + gap);
        Label(dc, string.Join("  ·  ", parts), at, DifficultyColor(f.Difficulty), 13 * ui, above);
    }

    /// <summary>Caption color: the difficulty's, so it reads at a glance.</summary>
    public static Color DifficultyColor(string? d) => d switch
    {
        "Normal" => Color.FromRgb(0x9B, 0xE8, 0x8F),
        "Hard" => Color.FromRgb(0xFF, 0xC8, 0x5C),
        "Nightmare" => Color.FromRgb(0xFF, 0x7A, 0x6B),
        "Chaos" => Color.FromRgb(0xD3, 0x8B, 0xFF),
        _ => Color.FromRgb(0xE9, 0xEA, 0xF2),
    };

    /// <summary>While placing: a dashed frame with a grip, the hint, and Done / Reset / Cancel.</summary>
    void PlacementChrome(DrawingContext dc, Rect panel, string style, double ui)
    {
        var accent = Color.FromRgb(0x4D, 0xD8, 0xFF);
        var frame = Rect.Inflate(panel, 6 * ui, 6 * ui);
        // A faint fill over the whole panel keeps every pixel of it hit-testable (alpha > 0), so
        // it can be grabbed anywhere; the rest of the screen stays click-through.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)),
            new Pen(new SolidColorBrush(accent), 2 * ui) { DashStyle = DashStyles.Dash }, frame, 20 * ui, 20 * ui);
        // A grip in the bottom-right corner: "this can be moved and resized".
        for (int i = 0; i < 3; i++)
        {
            double o = (6 + i * 5) * ui;
            dc.DrawLine(new Pen(new SolidColorBrush(accent), 1.6 * ui), new Point(frame.Right - o, frame.Bottom - 4 * ui), new Point(frame.Right - 4 * ui, frame.Bottom - o));
        }
        string hint = Lang.T(style == "Compass" ? "Drag to move · scroll to change width" : "Drag to move · scroll to resize") + " · " + Lang.T("Enter = done, Esc = cancel");
        var ft = new FormattedText(hint, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 13 * ui, Brushes.White, 1.0);
        double bw = 84 * ui, bh = 30 * ui, gap = 8 * ui;
        double rowW = Math.Max(ft.Width + 24 * ui, 3 * bw + 2 * gap + 24 * ui);
        double boxH = ft.Height + bh + 26 * ui;
        double clientW = Math.Max(1, PlacementClient.Width), clientH = Math.Max(1, PlacementClient.Height);
        double top = frame.Bottom + 30 * ui;
        if (top + boxH > clientH - 8 * ui) top = frame.Top - boxH - 30 * ui; // no room below: above
        double left = Math.Clamp(panel.X + panel.Width / 2 - rowW / 2, 8 * ui, Math.Max(8 * ui, clientW - rowW - 8 * ui));
        var box = new Rect(left, top, rowW, boxH);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 14, 17, 28)), new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1 * ui), box, 10 * ui, 10 * ui);
        dc.DrawText(ft, new Point(box.X + (box.Width - ft.Width) / 2, box.Y + 8 * ui));
        double by = box.Y + ft.Height + 16 * ui, bx = box.X + (box.Width - (3 * bw + 2 * gap)) / 2;
        DoneButton = Button(dc, new Rect(bx, by, bw, bh), Lang.T("Done"), accent, true, ui);
        ResetButton = Button(dc, new Rect(bx + bw + gap, by, bw, bh), Lang.T("Reset"), accent, false, ui);
        CancelButton = Button(dc, new Rect(bx + 2 * (bw + gap), by, bw, bh), Lang.T("Cancel"), accent, false, ui);
    }

    static Rect Button(DrawingContext dc, Rect r, string text, Color accent, bool primary, double ui)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(primary ? accent : Color.FromRgb(40, 45, 62)), null, r, 7 * ui, 7 * ui);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 13 * ui,
            primary ? new SolidColorBrush(Color.FromRgb(8, 20, 30)) : Brushes.White, 1.0);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        return r;
    }

    static readonly Brush PanelFill = Freeze(new SolidColorBrush(Color.FromArgb(215, 14, 16, 26)));
    static readonly Brush RadarFill = Freeze(new SolidColorBrush(Color.FromArgb(200, 6, 18, 28)));
    static readonly Pen RadarRing = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 120, 210, 255)), 1));

    /// <summary>Doors, key rooms, challenge room, nests and loot with the game's own map icons.</summary>
    void DrawMarkers(DrawingContext dc, Layout L, Func<double[], Point> P, double ms, bool labels)
    {
        // Everything below uses the game's own map-mark icons (Assets/icons, from the game files).
        double icon = 36 * ms;

        // Key rooms: frame the whole room (its square, from the game data) in the color of the
        // key its locked door takes, and put the game's key mark on that door.
        var keyRooms = L.KeyRooms.Select(r => r.Id).ToHashSet();
        if (settings.Layer("key"))
        {
            foreach (var r in L.KeyRooms)
            {
                var keyColor = KeyColor(r);
                string keyName = r.Door?.Color switch { "purple" => Lang.T("Purple key door"), "blue" => Lang.T("Blue key door"), _ => Lang.T("Gold key door") };
                Point a, b;
                if (r.ImgRect is { Length: 4 } q) { a = P([q[0], q[1]]); b = P([q[2], q[3]]); }
                else
                {
                    // Older data: frame the room by its contents (plus ~3 m).
                    var inside = L.Markers.Where(m => m.Room == r.Id).Select(m => m.Img).Append(r.Img).ToList();
                    double pad = 18 + (inside.Count == 1 ? 30 : 0);
                    a = P([inside.Min(p => p[0]) - pad, inside.Min(p => p[1]) - pad]);
                    b = P([inside.Max(p => p[0]) + pad, inside.Max(p => p[1]) + pad]);
                }
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, keyColor.R, keyColor.G, keyColor.B)), Math.Max(2, 2.5 * ms)) { DashStyle = DashStyles.Dash };
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(28, keyColor.R, keyColor.G, keyColor.B)), pen, new Rect(a, b), 6, 6);
                var at = r.Door != null ? P(r.Door.Img) : new Point((a.X + b.X) / 2, Math.Min(a.Y, b.Y));
                Icon(dc, "key", at, icon * 1.3);
                if (labels) Label(dc, keyName, new Point(at.X, at.Y - icon * 0.6), keyColor, 13 * ms, above: true);
            }
        }

        if (settings.Layer("challenge"))
        {
            foreach (var r in L.Rooms.Where(r => r.Kind == "challenge"))
            {
                var c = P(r.Img);
                Icon(dc, "challenge", c, icon * 1.05);
                if (labels) Label(dc, Lang.Term("Challenge", "Limited-Time Challenge"), new Point(c.X, c.Y + icon * 0.6), Color.FromRgb(0xE9, 0xEA, 0xF2), 12 * ms, above: false);
            }
        }

        // Loot, small first so the important ones draw on top. Chest tiers use the game's
        // treasure-chest art (white < blue < purple < gold), small stuff its generic loot mark.
        string[] order = ["pile", "pot", "mound", "simple", "exquisite", "luxurious", "legendary", "nest"];
        foreach (var kind in order)
        {
            if (!settings.Layer(kind)) continue;
            foreach (var m in L.Markers.Where(m => m.Kind == kind))
            {
                var p = P(m.Img);
                switch (kind)
                {
                    case "nest":
                        Icon(dc, "nest", p, icon * 1.1);
                        if (labels) Label(dc, Lang.Term("EggNest", "Nest"), new Point(p.X, p.Y + icon * 0.55), Colors["nest"], 12 * ms, above: false);
                        break;
                    case "legendary":
                        // Always there in a key room; elsewhere only a chance.
                        bool sure = keyRooms.Contains(m.Room);
                        Icon(dc, "chest_gold", p, sure ? icon : icon * 0.72, sure ? 1 : 0.8);
                        if (sure && labels) Label(dc, Lang.T("Legendary"), new Point(p.X, p.Y + icon * 0.5), Colors["legendary"], 11 * ms, above: false);
                        break;
                    case "luxurious": Icon(dc, "chest_purple", p, icon * 0.75); break;
                    case "exquisite": Icon(dc, "chest_blue", p, icon * 0.62); break;
                    case "simple": Icon(dc, "chest_white", p, icon * 0.52); break;
                    default: Icon(dc, "chest", p, icon * 0.45, 0.85); break;
                }
            }
        }

        if (settings.Layer("doors") && L.HasDoors)
        {
            Door(dc, "door_orange", P(L.Orange.Img), Colors["orange"], Lang.T("Orange door"), icon, ms);
            Door(dc, "door_blue", P(L.Blue.Img), Colors["blue"], Lang.T("Blue door"), icon, ms);
        }

        // Island marks: Sanctum entrances (same icons as its doors), the Egg Boat, rare chests, places.
        foreach (var m in L.Markers)
        {
            var p = P(m.Img);
            switch (m.Kind)
            {
                case "entrance_main" when settings.Layer("doors"):
                    Icon(dc, "door_orange", p, icon);
                    if (labels) Label(dc, Lang.T("Main entrance"), new Point(p.X, p.Y + icon * 0.6), Colors["orange"], 12 * ms, above: false);
                    break;
                case "entrance_side" when settings.Layer("doors"):
                    Icon(dc, "door_blue", p, icon);
                    if (labels) Label(dc, Lang.T("Side entrance"), new Point(p.X, p.Y + icon * 0.6), Colors["blue"], 12 * ms, above: false);
                    break;
                case "boat":
                    Icon(dc, "boat", p, icon * 1.1);
                    if (labels) Label(dc, Lang.Term("EggBoat", "Egg Boat"), new Point(p.X, p.Y + icon * 0.65), Color.FromRgb(0xB8, 0xF5, 0x6E), 13 * ms, above: false);
                    break;
                case "rare" when settings.Layer("legendary"):
                    Icon(dc, "treasure", p, icon * 0.9);
                    if (labels) Label(dc, Lang.T("Rare chest"), new Point(p.X, p.Y + icon * 0.55), Colors["legendary"], 11 * ms, above: false);
                    break;
                case "area" when labels:
                    Label(dc, m.Name, p, Color.FromRgb(0xF2, 0xEE, 0xE0), 14 * ms, above: true);
                    break;
            }
        }
    }

    static readonly Dictionary<string, BitmapSource?> Icons = new();

    /// <summary>One of the game's map icons from Assets/icons, or null if missing.</summary>
    public static BitmapSource? IconImage(string name)
    {
        if (Icons.TryGetValue(name, out var bmp)) return bmp;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", name + ".png");
        if (System.IO.File.Exists(path))
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            bmp = bi;
        }
        return Icons[name] = bmp;
    }

    /// <summary>Draws an icon centered on c, fitted into a size x size box.</summary>
    static readonly Brush Disc = Freeze(new SolidColorBrush(Color.FromArgb(225, 24, 27, 40)));
    static readonly Pen DiscRing = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.2));
    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
    /// <summary>Set while drawing inside a rotated map (the round minimap): icons and labels are
    /// turned back by this much so they stay upright.</summary>
    [ThreadStatic] static double spin;

    /// <summary>Draws an icon centered on c, fitted into a size x size box. Icons without their
    /// own backing sit on a dark disc, like the game's map marks.</summary>
    static void Icon(DrawingContext dc, string name, Point c, double size, double opacity = 1)
    {
        if (spin != 0) dc.PushTransform(new RotateTransform(spin, c.X, c.Y));
        DrawIcon(dc, name, c, size, opacity);
        if (spin != 0) dc.Pop();
    }

    static void DrawIcon(DrawingContext dc, string name, Point c, double size, double opacity)
    {
        var bmp = IconImage(name);
        if (opacity < 1) dc.PushOpacity(opacity);
        if (!name.StartsWith("door_") && name != "key")
        {
            dc.DrawEllipse(Disc, DiscRing, c, size * 0.56, size * 0.56);
            size *= name.StartsWith("chest_") ? 0.9 : 0.78;
        }
        if (opacity < 1) dc.Pop();
        if (bmp == null) { dc.DrawEllipse(Brushes.White, null, c, size / 4, size / 4); return; }
        double k = size / Math.Max(bmp.PixelWidth, bmp.PixelHeight);
        double w = bmp.PixelWidth * k, h = bmp.PixelHeight * k;
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.DrawImage(bmp, new Rect(c.X - w / 2, c.Y - h / 2, w, h));
        if (opacity < 1) dc.Pop();
    }

    /// <summary>Where the camera looks: a soft white wedge from the player, like the game's
    /// minimap draws (~80 deg wide, fading out with distance).</summary>
    void ViewCone(DrawingContext dc, Point p, double? camera, double length)
    {
        if (camera is not double deg || !settings.ShowViewCone) return;
        var wedge = new StreamGeometry();
        using (var g = wedge.Open())
        {
            const double Half = 40 * Math.PI / 180;
            double a = deg * Math.PI / 180;
            Point At(double ang) => new(p.X + Math.Sin(ang) * length, p.Y - Math.Cos(ang) * length);
            g.BeginFigure(p, true, true);
            g.LineTo(At(a - Half), false, false);
            g.ArcTo(At(a + Half), new Size(length, length), 0, false, SweepDirection.Clockwise, false, false);
        }
        wedge.Freeze();
        var fill = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = p, GradientOrigin = p, RadiusX = length, RadiusY = length };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(165, 255, 255, 255), 0.0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(95, 255, 255, 255), 0.55));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
        fill.Freeze();
        dc.DrawGeometry(fill, null, wedge);
    }

    /// <summary>The player: the game's own minimap arrow, turned the way they face (a plain
    /// marker while the direction isn't known yet).</summary>
    static void Player(DrawingContext dc, Point p, double? heading, double size, bool fresh = true)
    {
        if (HeadingFeed.Egg && IconImage("egg_marker") is BitmapSource eggIcon)
        {
            // Twined with an egg: the game's own egg marker (Img_Map_Mark_GrabEgg_Transformation_Egg,
            // which has its own dark base), as the game's minimap shows it. It doesn't turn.
            double w = size * 0.95;
            dc.DrawImage(eggIcon, new Rect(p.X - w / 2, p.Y - w / 2, w, w));
            return;
        }
        var arrow = IconImage("player_arrow");
        if (arrow == null || heading is not double h)
        {
            var c = Colors["player"];
            byte a = fresh ? (byte)255 : (byte)120;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(a / 3), c.R, c.G, c.B)), null, p, size * 0.6, size * 0.6);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), new Pen(Brushes.Black, size * 0.08), p, size * 0.25, size * 0.25);
            return;
        }
        dc.PushTransform(new RotateTransform(h, p.X, p.Y));
        if (!fresh) dc.PushOpacity(0.55);
        dc.DrawImage(arrow, new Rect(p.X - size / 2, p.Y - size / 2, size, size));
        if (!fresh) dc.Pop();
        dc.Pop();
    }

    static void Door(DrawingContext dc, string icon, Point p, Color c, string text, double size, double ms)
    {
        // A ring around the game's own door icon so it stands out above the map art.
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B)), 3 * ms), p, size * 0.72, size * 0.72);
        Icon(dc, icon, p, size * 1.15);
        Label(dc, text, new Point(p.X, p.Y + size * 0.78), c, 13 * ms, above: false);
    }

    static void Label(DrawingContext dc, string text, Point at, Color color, double size, bool above)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Math.Max(9, size), new SolidColorBrush(color), 1.0);
        var origin = new Point(at.X - ft.Width / 2, above ? at.Y - ft.Height : at.Y);
        var outline = ft.BuildGeometry(origin);
        if (spin != 0) dc.PushTransform(new RotateTransform(spin, at.X, at.Y));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), Math.Max(2.5, size / 4)) { LineJoin = PenLineJoin.Round }, outline);
        dc.DrawText(ft, origin);
        if (spin != 0) dc.Pop();
    }

    void Legend(DrawingContext dc, OverlayFrame f, Layout layout, double ui, bool aligned)
    {
        var lines = new List<(string, Color)>
        {
            ($"{layout.DisplayTitle}{(f.Difficulty != null ? "  ·  " + Lang.Difficulty(f.Difficulty) : "")}", Colors["key"]),
            (layout.IsIsland
                ? Lang.F("Sanctum entrances: orange = main, blue = side  ·  {0} to escape", Lang.Term("EggBoat", "Egg Boat"))
                : Lang.F("Blue door to the {0}", Lang.T(layout.Compass)) + "  ·  " + Count(layout.KeyRooms.Count(), "1 key room", "{0} key rooms") + "  ·  " + Count(layout.NestCount, "1 possible nest", "{0} possible nests"), System.Windows.Media.Colors.White),
        };
        if (!aligned && !Preview) lines.Add((Lang.T("Not lined up: open the full map (M) and hold the key again"), Color.FromRgb(0xFF, 0xB0, 0x60)));
        double size = 15 * ui, y = 18 * ui;
        double w = 0, h = 0;
        var fts = lines.Select(l => new FormattedText(l.Item1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, new SolidColorBrush(l.Item2), 1.0)).ToList();
        foreach (var ft in fts) { w = Math.Max(w, ft.Width); h += ft.Height + 3 * ui; }
        // Top center: the game's map keeps its title top-left and zoom bar on the left.
        double x = Preview ? 22 * ui : (f.Client.Width - w) / 2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(185, 10, 12, 20)), null, new Rect(x - 12 * ui, y - 8 * ui, w + 24 * ui, h + 14 * ui), 10, 10);
        foreach (var ft in fts) { dc.DrawText(ft, new Point(x, y)); y += ft.Height + 3 * ui; }
    }

    static string Count(int n, string one, string many) => n == 1 ? Lang.T(one) : Lang.F(many, n);

    static void Banner(DrawingContext dc, OverlayFrame f, double ui, string text)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 16 * ui, Brushes.White, 1.0);
        double x = (f.Client.Width - ft.Width) / 2, y = 90 * ui;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 10, 12, 20)), null, new Rect(x - 16 * ui, y - 10 * ui, ft.Width + 32 * ui, ft.Height + 20 * ui), 10, 10);
        dc.DrawText(ft, new Point(x, y));
    }
}
