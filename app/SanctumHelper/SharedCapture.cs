using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;

namespace SanctumHelper;

/// <summary>Reads the game frame the aLogs app shares with its modules, so the game window is
/// copied once instead of once per tool. When the app isn't running (standalone Sanctum Helper)
/// or its frame is stale, TryGrab returns null and the normal window capture is used.
///
/// Layout of "Local\aLogs.Capture.v1" (little-endian; written by aLogs app/src-tauri/src/capture.rs):
///   0 u32 magic "ALCP", 4 u32 version, 8 u64 seq (odd while writing),
///   16 u32 width, 20 u32 height, 24 u32 stride, 28 u32 flags (bit 0 = valid image),
///   32 u64 frame time (unix ms), 40 u64 window handle, 48/52 i32 client x/y,
///   64 request slots: 16 x { u64 wanted until (unix ms), u32 interval ms, u32 process id },
///   4096 pixels, BGRA, top row first.</summary>
public static class SharedCapture
{
    const string Name = @"Local\aLogs.Capture.v1";
    const uint Magic = 0x50434C41; // "ALCP"
    const int Header = 4096, Slots = 16, SlotBase = 64;
    const long Size = Header + 3840L * 2160 * 4;

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

    /// <summary>True while frames have recently come from the aLogs app.</summary>
    public static bool InUse => lastUsed != 0 && Environment.TickCount64 - lastUsed < 3000;
    static readonly int pid = Environment.ProcessId;

    /// <summary>Set false to never use the shared frame.</summary>
    public static bool Enabled { get; set; } = true;

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static bool Open()
    {
        if (basePtr != IntPtr.Zero) return true;
        if (Environment.TickCount64 < nextOpen) return false;
        nextOpen = Environment.TickCount64 + 2000;
        try
        {
            file = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.ReadWrite);
            view = file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
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

    static uint U32(int at) => (uint)Marshal.ReadInt32(basePtr, at);
    static long I64(int at) => Marshal.ReadInt64(basePtr, at);

    /// <summary>Keeps our request slot alive: frames are captured only while someone asks.</summary>
    static bool Request(long now)
    {
        if (slot < 0 || U32(SlotBase + slot * 16 + 12) != (uint)pid)
        {
            slot = -1;
            for (int i = 0; i < Slots && slot < 0; i++)
            {
                int at = SlotBase + i * 16;
                if (I64(at) < now || U32(at + 12) == (uint)pid) slot = i;
            }
            if (slot < 0) return false;
        }
        int a = SlotBase + slot * 16;
        Marshal.WriteInt32(basePtr, a + 12, pid);
        Marshal.WriteInt32(basePtr, a + 8, (int)interval);
        Marshal.WriteInt64(basePtr, a, now + 2500);
        return true;
    }

    /// <summary>The part `sub` (client coordinates) of the shared frame as BGR, or null when there
    /// is no fresh frame of this window at this size.</summary>
    public static Mat? TryGrab(IntPtr hwnd, int clientW, int clientH, Rect sub)
    {
        if (!Enabled) return null;
        lock (gate)
        {
            long t = Environment.TickCount64;
            // Ask at the pace we're being called (about 10 a second while tracking).
            if (lastCall != 0) interval = Math.Clamp(0.7 * interval + 0.3 * (t - lastCall), 40, 1000);
            lastCall = t;
            if (!Open()) return null;
            long now = NowMs;
            if (!Request(now)) return null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                long seq = Seq();
                if ((seq & 1) != 0) { Thread.SpinWait(200); continue; }
                int w = (int)U32(16), h = (int)U32(20), stride = (int)U32(24);
                if ((U32(28) & 1) == 0 || I64(40) != (long)hwnd || w != clientW || h != clientH) return null;
                if (now - I64(32) > Math.Max(150, 2 * interval)) return null;
                sub = sub.Intersect(new Rect(0, 0, w, h));
                if (sub.Width <= 0 || sub.Height <= 0) return null;
                var src = basePtr + Header + sub.Y * stride + sub.X * 4;
                using var bgra = Mat.FromPixelData(sub.Height, sub.Width, MatType.CV_8UC4, src, stride);
                var bgr = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                if (Seq() == seq)
                {
                    if (!announced) { announced = true; Log.Write("using the aLogs app's shared capture"); }
                    lastUsed = Environment.TickCount64;
                    return bgr;
                }
                bgr.Dispose();
            }
            return null;
        }
    }

    /// <summary>The frame's sequence number (aligned 8-byte read, so atomic on x64), fenced so the
    /// pixel reads stay between the two checks.</summary>
    static long Seq()
    {
        Interlocked.MemoryBarrier();
        long seq = Marshal.ReadInt64(basePtr, 8);
        Interlocked.MemoryBarrier();
        return seq;
    }
}
