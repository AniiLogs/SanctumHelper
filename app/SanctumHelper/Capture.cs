using System;
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
