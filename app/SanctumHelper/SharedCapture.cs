using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;

namespace SanctumHelper;

/// <summary>Reads the game picture the aLogs app shares with its modules, so the game is captured once for all
/// tools, on the graphics card. While the app runs, pictures come only from here (Engine.Grab waits for the next
/// one); when it isn't running (standalone Sanctum Helper), TryGrab returns null and the normal window capture is used.
///
/// We ask for the box around everything we read in the last couple of seconds; only that leaves the graphics card,
/// and the app writes it into our own picture buffer at our own pace (a part outside it is asked for and taken
/// from the next picture).
///
/// Layout of "Local\aLogs.Capture.v2" (little-endian; written by aLogs app/src-tauri/src/capture.rs):
///   0 u32 magic "ALCP", 4 u32 version (2), 8 u64 header seq, 16 u32 client width, 20 u32 client height,
///   28 u32 flags (bit 0 = valid image, bit 1 = our overlays may be in it: Desktop Duplication; bits 4-7 source),
///   32 u64 newest picture time (unix ms), 40 u64 window handle, 48/52 i32 client x/y,
///   64 request slots: 16 x 16 { u64 wanted until (unix ms), u32 interval ms, u32 process id },
///   320 picture slots: 16 x 64 { 0 u16 x0, y0, x1, y1 (share of the client size, 0-65535), 8 u32 request id,
///   12 u32 served id, 16 u64 seq (odd while written), 24 u64 picture time, 32 u32 pixel offset from 4096,
///   36 u32 stride, 40 u16 x, y, w, h (client pixels), 48 u32 flags, 52 u32 copy microseconds,
///   56 u16 client width, height, 60 u8 shrink wanted, 61 u8 shrink used },
///   1344 u64 clean until (unix ms): a proof is being taken (the meter); overlays stay out of screen captures until then,
///   1352 overlay windows: 3 x 144 { u64 updated (unix ms), u32 pid, u32 count (<= 8), 8 x i32 x0, y0, x1, y1 screen
///   px }, owners 0 the meter, 1 Sanctum Helper, 2 the app's button: every reader of a screen copy covers them all,
///   so overlays can stay in screenshots and recordings (a block not updated for 3 s is stale),
///   2048 u64 heartbeat (unix ms) of the app's capture thread,
///   4096 the pictures, BGRA, top row first.</summary>
public static class SharedCapture
{
    const string Name = @"Local\aLogs.Capture.v2";
    const uint Magic = 0x50434C41; // "ALCP"
    const int Header = 4096, Slots = 16, SlotBase = 64, PictureBase = 320, Picture = 64;
    const int Heartbeat = 2048;      // the app's capture thread stamps it (unix ms)
    const long HeartbeatStaleMs = 5000; // older: the app was closed, so the mapping is let go
    const long AreaKeepMs = 2000; // an area stays asked for this long after its last use

    static MemoryMappedFile? file;
    static MemoryMappedViewAccessor? view;
    static IntPtr basePtr;
    static long nextOpen;
    static int slot = -1;
    static long lastCall;
    static double interval = 250;
    static readonly object gate = new();
    static bool announced;
    static long lastUsed;
    static readonly System.Collections.Generic.List<(long At, Rect Area)> areas = new();
    static Rect box;
    static uint request;

    /// <summary>True while frames have recently come from the aLogs app.</summary>
    public static bool InUse => lastUsed != 0 && Environment.TickCount64 - lastUsed < 3000;
    static readonly int pid = Environment.ProcessId;

    /// <summary>Set false to never use the shared frame.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The aLogs app is running and sharing its capture: then pictures come only from it, and we wait for
    /// its next one instead of copying the window ourselves.</summary>
    public static bool AppPresent
    {
        get
        {
            if (!Enabled) return false;
            lock (gate)
            {
                if (!Open()) return false;
                long beat = I64(Heartbeat);
                if (beat != 0 && NowMs - beat > HeartbeatStaleMs) { Close(); return false; }   // the app is gone
                return true;
            }
        }
    }

    /// <summary>The last picture we took may have our overlays in it (flags bit 1: Desktop Duplication), so our
    /// overlay must be hidden from capture.</summary>
    public static bool ScreenCopy { get; private set; }

    const int CleanUntil = 1344, OverlayBase = 1352, OverlayBlock = 144, OverlayMax = 8;
    const long OverlayStaleMs = 3000;
    public const int OwnerSanctum = 1;

    /// <summary>A proof is being taken (the meter): our overlays stay out of screen captures meanwhile.</summary>
    public static bool CleanNow
    {
        get { lock (gate) return Open() && NowMs < I64(CleanUntil); }
    }

    /// <summary>Our overlay windows' spots (screen px), for the meter and the app to cover in their pictures.</summary>
    public static void PublishOverlays(int owner, System.Collections.Generic.IReadOnlyList<Rect> rects)
    {
        lock (gate)
        {
            if (!Open()) return;
            int at = OverlayBase + owner * OverlayBlock, n = Math.Min(OverlayMax, rects.Count);
            for (int k = 0; k < n; k++)
            {
                var r = rects[k];
                Marshal.WriteInt32(basePtr, at + 16 + 16 * k, r.X); Marshal.WriteInt32(basePtr, at + 20 + 16 * k, r.Y);
                Marshal.WriteInt32(basePtr, at + 24 + 16 * k, r.Right); Marshal.WriteInt32(basePtr, at + 28 + 16 * k, r.Bottom);
            }
            Marshal.WriteInt32(basePtr, at + 8, pid);
            Marshal.WriteInt32(basePtr, at + 12, n);
            Marshal.WriteInt64(basePtr, at, NowMs);
        }
    }

    /// <summary>Every aLogs overlay's spot now (screen px), ours included unless skipped: covered in a picture of the screen.</summary>
    public static System.Collections.Generic.List<Rect> Overlays(int skipOwner = -1)
    {
        var all = new System.Collections.Generic.List<Rect>();
        lock (gate)
        {
            if (!Open()) return all;
            long now = NowMs;
            for (int owner = 0; owner < 3; owner++)
            {
                if (owner == skipOwner) continue;
                int at = OverlayBase + owner * OverlayBlock;
                if (now - I64(at) > OverlayStaleMs) continue;
                int n = (int)Math.Min(OverlayMax, U32(at + 12));
                for (int k = 0; k < n; k++)
                {
                    int x0 = Marshal.ReadInt32(basePtr, at + 16 + 16 * k), y0 = Marshal.ReadInt32(basePtr, at + 20 + 16 * k);
                    int x1 = Marshal.ReadInt32(basePtr, at + 24 + 16 * k), y1 = Marshal.ReadInt32(basePtr, at + 28 + 16 * k);
                    if (x1 > x0 && y1 > y0) all.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
                }
            }
        }
        return all;
    }

    /// <summary>How long the app took to read our last picture off the graphics card (ms).</summary>
    public static double CopyMs { get; private set; }

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static bool Open()
    {
        if (basePtr != IntPtr.Zero) return true;
        if (Environment.TickCount64 < nextOpen) return false;
        nextOpen = Environment.TickCount64 + 2000;
        try
        {
            file = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.ReadWrite);
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite); // the whole mapping
            var p = view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)view.PointerOffset;
            if ((uint)Marshal.ReadInt32(p) != Magic) { Close(); return false; }
            basePtr = p;
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    static void Close()
    {
        view?.Dispose(); file?.Dispose();
        view = null; file = null; basePtr = IntPtr.Zero; slot = -1;
    }

    static int U16(int at) => (ushort)Marshal.ReadInt16(basePtr, at);
    static uint U32(int at) => (uint)Marshal.ReadInt32(basePtr, at);
    static long I64(int at) => Marshal.ReadInt64(basePtr, at);

    /// <summary>Keeps our request slot alive: pictures are taken only while someone asks.</summary>
    static bool Request(long now)
    {
        if (slot < 0 || U32(SlotBase + slot * 16 + 12) != (uint)pid)
        {
            slot = -1;
            box = default;
            for (int i = 0; i < Slots && slot < 0; i++)
            {
                int at = SlotBase + i * 16;
                if (I64(at) < now || U32(at + 12) == (uint)pid) slot = i;
            }
            if (slot < 0) return false;
            Marshal.WriteByte(basePtr, PictureBase + slot * Picture + 60, 0);   // no shrink (its last owner may have asked)
        }
        int a = SlotBase + slot * 16;
        Marshal.WriteInt32(basePtr, a + 12, pid);
        Marshal.WriteInt32(basePtr, a + 8, (int)interval);
        Marshal.WriteInt64(basePtr, a, now + 2500);
        return true;
    }

    /// <summary>Asks for the box (shares of the client size, 0-65535) around `area` and the areas used lately;
    /// a new box gets a new request id.</summary>
    static void AskArea(Rect area)
    {
        long t = Environment.TickCount64;
        areas.RemoveAll(a => t - a.At > AreaKeepMs || a.Area == area);
        areas.Add((t, area));
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
        foreach (var (_, a) in areas) { x0 = Math.Min(x0, a.X); y0 = Math.Min(y0, a.Y); x1 = Math.Max(x1, a.Right); y1 = Math.Max(y1, a.Bottom); }
        var want = new Rect(x0, y0, x1 - x0, y1 - y0);
        int at = PictureBase + slot * Picture;
        if (want == box && U32(at + 8) == request) return;
        box = want;
        request = request % uint.MaxValue + 1;
        Marshal.WriteInt16(basePtr, at, (short)(ushort)x0);
        Marshal.WriteInt16(basePtr, at + 2, (short)(ushort)y0);
        Marshal.WriteInt16(basePtr, at + 4, (short)(ushort)x1);
        Marshal.WriteInt16(basePtr, at + 6, (short)(ushort)y1);
        Marshal.WriteInt32(basePtr, at + 8, (int)request);
    }

    /// <summary>The part `sub` (client coordinates) of the shared picture as BGR, or null when there
    /// is no fresh picture of this window at this size holding that part.</summary>
    public static Mat? TryGrab(IntPtr hwnd, int clientW, int clientH, Rect sub)
    {
        if (!Enabled) return null;
        lock (gate)
        {
            long t = Environment.TickCount64;
            // Ask at the pace we're being called: ~60 a second while the overlay key is held, twice a second on the
            // party screen, nothing otherwise (the request lapses).
            if (lastCall != 0) interval = Math.Clamp(0.7 * interval + 0.3 * (t - lastCall), 16, 1000);
            lastCall = t;
            if (!Open()) return null;
            long now = NowMs;
            if (!Request(now)) return null;
            sub = sub.Intersect(new Rect(0, 0, clientW, clientH));
            if (sub.Width <= 0 || sub.Height <= 0) return null;
            int sx0 = (int)((long)sub.X * 65535 / clientW), sy0 = (int)((long)sub.Y * 65535 / clientH);
            int sx1 = (int)Math.Min(65535, ((long)sub.Right * 65535 + clientW - 1) / clientW);
            int sy1 = (int)Math.Min(65535, ((long)sub.Bottom * 65535 + clientH - 1) / clientH);
            AskArea(new Rect(sx0, sy0, sx1 - sx0, sy1 - sy0));
            if (I64(40) != (long)hwnd) return null;
            int at = PictureBase + slot * Picture;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                long seq = Seq(at + 16);
                if ((seq & 1) != 0) { Thread.SpinWait(200); continue; }
                if (U32(at + 12) != request) return null;                  // our area isn't in yet
                uint flags = U32(at + 48);
                if ((flags & 1) == 0 || U16(at + 56) != clientW || U16(at + 58) != clientH) return null;
                if (Marshal.ReadByte(basePtr, at + 61) > 1) return null;   // shrunk: not what we asked for
                // Too old: no picture this time (accepted up to 100 ms, or two of our intervals).
                if (now - I64(at + 24) > Math.Max(100, 2 * interval)) return null;
                int offset = (int)U32(at + 32), stride = (int)U32(at + 36);
                var pic = new Rect(U16(at + 40), U16(at + 42), U16(at + 44), U16(at + 46));
                if (pic.Intersect(sub) != sub) return null;                // a picture of an older, smaller box
                var src = basePtr + Header + offset + (sub.Y - pic.Y) * stride + (sub.X - pic.X) * 4;
                using var bgra = Mat.FromPixelData(sub.Height, sub.Width, MatType.CV_8UC4, src, stride);
                var bgr = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                if (Seq(at + 16) == seq)
                {
                    ScreenCopy = (flags & 2) != 0;
                    CopyMs = U32(at + 52) / 1000.0;
                    if (!announced) { announced = true; Log.Write("using the aLogs app's shared capture"); }
                    lastUsed = Environment.TickCount64;
                    return bgr;
                }
                bgr.Dispose();
            }
            return null;
        }
    }

    /// <summary>A sequence number (aligned 8-byte read, so atomic on x64), fenced so the pixel reads stay
    /// between the two checks.</summary>
    static long Seq(int at)
    {
        Interlocked.MemoryBarrier();
        long seq = Marshal.ReadInt64(basePtr, at);
        Interlocked.MemoryBarrier();
        return seq;
    }
}
