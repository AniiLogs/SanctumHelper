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
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr bmp = Native.CreateCompatibleBitmap(screen, clientW, clientH);
        IntPtr old = Native.SelectObject(mem, bmp);
        try
        {
            if (!Native.PrintWindow(hwnd, mem, Native.PW_CLIENTONLY | Native.PW_RENDERFULLCONTENT)) return null;
            Native.SelectObject(mem, old);
            using var full = new Mat(clientH, clientW, MatType.CV_8UC4);
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = clientW, biHeight = -clientH, biPlanes = 1, biBitCount = 32,
            };
            Native.GetDIBits(mem, bmp, 0, (uint)clientH, full.Data, ref bi, 0);
            sub = sub.Intersect(new Rect(0, 0, clientW, clientH));
            using var part = new Mat(full, sub);
            var bgr = new Mat();
            Cv2.CvtColor(part, bgr, ColorConversionCodes.BGRA2BGR);
            // A window that can't be printed comes back black.
            if (Cv2.CountNonZero(bgr.Reshape(1)) == 0) { bgr.Dispose(); return null; }
            return bgr;
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static Mat Grab(Rect r)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr bmp = Native.CreateCompatibleBitmap(screen, r.Width, r.Height);
        IntPtr old = Native.SelectObject(mem, bmp);
        try
        {
            Native.BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, Native.SRCCOPY);
            var bgra = new Mat(r.Height, r.Width, MatType.CV_8UC4);
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = r.Width, biHeight = -r.Height, biPlanes = 1, biBitCount = 32,
            };
            Native.SelectObject(mem, old);
            Native.GetDIBits(mem, bmp, 0, (uint)r.Height, bgra.Data, ref bi, 0);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            bgra.Dispose();
            return bgr;
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
