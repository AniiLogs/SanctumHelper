using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace SanctumHelper;

/// <summary>Finds the Aniimo window by its title and class only (no process access).</summary>
public sealed class GameWindow
{
    public IntPtr Handle { get; private set; }
    /// <summary>Client area in physical screen pixels.</summary>
    public Rect Client { get; private set; }

    public bool IsValid => Handle != IntPtr.Zero && Native.IsWindow(Handle) && !Native.IsIconic(Handle);
    public bool IsForeground => Handle != IntPtr.Zero && Native.GetForegroundWindow() == Handle;

    public bool Refresh(string titleMatch)
    {
        if (!IsValid || !Native.WindowText(Handle).Contains(titleMatch, StringComparison.OrdinalIgnoreCase))
            Handle = Find(titleMatch);
        if (Handle == IntPtr.Zero) return false;
        if (!Native.GetClientRect(Handle, out var r)) return false;
        var p = new Native.POINT();
        Native.ClientToScreen(Handle, ref p);
        Client = new Rect(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
        return Client.Width > 100 && Client.Height > 100;
    }

    static IntPtr Find(string titleMatch)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            var title = Native.WindowText(h);
            if (!title.Contains(titleMatch, StringComparison.OrdinalIgnoreCase)) return true;
            // The game is a Unity player; this keeps browser tabs titled "Aniimo ..." out.
            if (Native.ClassName(h) != "UnityWndClass") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}

/// <summary>Copies game pixels without a capture border. Two ways:
/// - Window: PrintWindow(PW_RENDERFULLCONTENT) asks Windows' compositor for the game window's
///   own image, so windows on top of it (our overlay) are never in it. The overlay can then be
///   visible to OBS, ShadowPlay and screenshots.
/// - Screen: BitBlt of the desktop; the overlay must then be hidden from capture
///   (WDA_EXCLUDEFROMCAPTURE) so it doesn't feed back into detection.</summary>
public static class ScreenGrabber
{
    /// <summary>Copies the part `sub` (client coordinates) of the window's client area,
    /// or null when the window gave no image (then fall back to the screen).</summary>
    public static Mat? GrabWindow(IntPtr hwnd, int clientW, int clientH, Rect sub)
    {
        // The aLogs app's shared frame when it's running, so the window is copied once for all tools.
        if (SharedCapture.TryGrab(hwnd, clientW, clientH, sub) is { } shared) return shared;
        sub = sub.Intersect(new Rect(0, 0, clientW, clientH));
        if (sub.Width <= 0 || sub.Height <= 0) return null;
        // PrintWindow draws the whole client area: into a DIB section, whose pixels are read in place, and only the
        // part asked for is converted. (It went into a device bitmap that was then copied whole into a second buffer:
        // two 33 MB copies per 4K frame even when only the 400x200 title corner or the party screen's top band was wanted.)
        using var dib = new Dib(clientW, clientH);
        if (!Native.PrintWindow(hwnd, dib.Dc, Native.PW_CLIENTONLY | Native.PW_RENDERFULLCONTENT)) return null;
        Native.GdiFlush();
        using var full = Mat.FromPixelData(clientH, clientW, MatType.CV_8UC4, dib.Bits, clientW * 4L);
        using var part = new Mat(full, sub);
        var bgr = new Mat();
        Cv2.CvtColor(part, bgr, ColorConversionCodes.BGRA2BGR);
        // A window that can't be printed comes back black.
        using (var flat = bgr.Reshape(1))
            if (Cv2.CountNonZero(flat) == 0) { bgr.Dispose(); return null; }
        return bgr;
    }

    public static Mat Grab(Rect r)
    {
        // Straight into a DIB section and converted from there (was a device bitmap copied out with GetDIBits into
        // a second buffer first).
        using var dib = new Dib(r.Width, r.Height);
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try { Native.BitBlt(dib.Dc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, Native.SRCCOPY); }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        Native.GdiFlush();
        using var bgra = Mat.FromPixelData(r.Height, r.Width, MatType.CV_8UC4, dib.Bits, r.Width * 4L);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    /// <summary>A top-down 32-bit DIB section selected into a memory DC; all of it is released on Dispose.</summary>
    sealed class Dib : IDisposable
    {
        public readonly IntPtr Dc, Bits;
        readonly IntPtr bmp, old;

        public Dib(int w, int h)
        {
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            };
            Dc = Native.CreateCompatibleDC(IntPtr.Zero);
            bmp = Native.CreateDIBSection(Dc, ref bi, 0, out Bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || Bits == IntPtr.Zero) { Native.DeleteDC(Dc); throw new InvalidOperationException($"no {w}x{h} bitmap"); }
            old = Native.SelectObject(Dc, bmp);
        }

        public void Dispose()
        {
            Native.SelectObject(Dc, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(Dc);
        }
    }
}

/// <summary>A picture of part of the screen without some of our own windows in it (the Magnification API's filter
/// list, as screen sharing in browsers does it): for a map drawn over what is read while it stays in screenshots and
/// recordings (a copy of the screen, Desktop Duplication, has it in). Each thread gets its own hidden magnifier.
/// About as costly as a plain screen copy of the same area.</summary>
public static class Magnifier
{
    /// <summary>True once the magnifier gave no picture here: the map then stays out of captures instead.</summary>
    public static bool Failed { get; private set; }

    [ThreadStatic] static Instance? mine;
    static readonly object gate = new();
    static bool initialized;

    public static Mat? Grab(Rect screen, IReadOnlyList<IntPtr> without)
    {
        if (Failed || screen.Width <= 0 || screen.Height <= 0) return null;
        try
        {
            lock (gate) if (!initialized) { if (!MagInitialize()) { Fail("not available"); return null; } initialized = true; }
            mine ??= new Instance();
            var m = mine.Grab(screen, without);
            if (m == null) Fail("gave no picture");
            return m;
        }
        catch (Exception e) { Fail(e.Message); return null; }
    }

    static void Fail(string why)
    {
        if (Failed) return;
        Failed = true;
        Log.Write($"magnifier {why}: maps over the game stay out of screen captures where the picture is a copy of the screen");
    }

    sealed class Instance
    {
        readonly IntPtr host, mag;
        readonly ImageScalingCallback callback;   // kept alive while the magnifier may call it
        Mat? got;
        Size size;

        public Instance()
        {
            host = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "Static", "SanctumHelper magnifier", WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            SetLayeredWindowAttributes(host, 0, 255, LWA_ALPHA);
            mag = CreateWindowEx(0, "Magnifier", "", WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (host == IntPtr.Zero || mag == IntPtr.Zero) throw new InvalidOperationException("no magnifier window");
            callback = OnImage;
            if (!MagSetImageScalingCallback(mag, callback)) throw new InvalidOperationException("no image callback");
        }

        bool OnImage(IntPtr hwnd, IntPtr src, MAGIMAGEHEADER sh, IntPtr dst, MAGIMAGEHEADER dh, RECT unclipped, RECT clipped, IntPtr dirty)
        {
            if (sh.width != size.Width || sh.height != size.Height) return true;
            using var bgra = Mat.FromPixelData((int)sh.height, (int)sh.width, MatType.CV_8UC4, src + (int)sh.offset, sh.stride);
            got?.Dispose();
            got = new Mat();
            Cv2.CvtColor(bgra, got, ColorConversionCodes.BGRA2BGR);
            return true;
        }

        public Mat? Grab(Rect r, IReadOnlyList<IntPtr> without)
        {
            got?.Dispose(); got = null;
            if (r.Width != size.Width || r.Height != size.Height)
            {
                size = new Size(r.Width, r.Height);
                SetWindowPos(host, IntPtr.Zero, 0, 0, r.Width, r.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                SetWindowPos(mag, IntPtr.Zero, 0, 0, r.Width, r.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            var list = without.Where(h => h != IntPtr.Zero).ToArray();
            MagSetWindowFilterList(mag, MW_FILTERMODE_EXCLUDE, list.Length, list);
            if (!MagSetWindowSource(mag, new RECT { Left = r.X, Top = r.Y, Right = r.Right, Bottom = r.Bottom })) return null;
            InvalidateRect(mag, IntPtr.Zero, true);
            UpdateWindow(mag);
            // this thread has no message loop: drop what the hidden windows were sent meanwhile
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE)) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            var m = got; got = null;
            return m;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MAGIMAGEHEADER { public uint width, height; public Guid format; public uint stride, offset; public UIntPtr cbSize; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate bool ImageScalingCallback(IntPtr hwnd, IntPtr src, MAGIMAGEHEADER sh, IntPtr dst, MAGIMAGEHEADER dh, RECT unclipped, RECT clipped, IntPtr dirty);

    const int MW_FILTERMODE_EXCLUDE = 0, LWA_ALPHA = 2, PM_REMOVE = 1;
    const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    [DllImport("Magnification.dll")] static extern bool MagInitialize();
    [DllImport("Magnification.dll")] static extern bool MagSetImageScalingCallback(IntPtr hwnd, ImageScalingCallback cb);
    [DllImport("Magnification.dll")] static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] list);
    [DllImport("Magnification.dll")] static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
}
