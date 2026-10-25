using System;
using System.Runtime.InteropServices;

namespace SanctumHelper;

/// <summary>Controller buttons through XInput (Xbox and compatible pads, the way any app reads
/// them; nothing of the game is touched), as a second overlay key next to the keyboard one.
/// Buttons of all connected pads are combined.</summary>
public sealed class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int GetState14(int user, out XInputState state);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int GetState910(int user, out XInputState state);

    // XInput's button bits, plus the two triggers as buttons of their own.
    public const int LeftTrigger = 0x10000, RightTrigger = 0x20000;
    static readonly (int bit, string name)[] Names =
    [
        (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"),
        (0x0100, "LB"), (0x0200, "RB"), (LeftTrigger, "LT"), (RightTrigger, "RT"),
        (0x0040, "Left stick click"), (0x0080, "Right stick click"),
        (0x0020, "View / Back"), (0x0010, "Menu / Start"),
        (0x0001, "D-pad up"), (0x0002, "D-pad down"), (0x0004, "D-pad left"), (0x0008, "D-pad right"),
    ];

    public static string Name(int button)
    {
        if (button == 0) return "None";
        foreach (var (bit, name) in Names) if (button == bit) return name;
        return $"Button 0x{button:X}";
    }

    bool useOld;
    readonly bool[] connected = new bool[4];
    long nextScan;

    /// <summary>All buttons held right now on any connected pad (0 when none or no pad).</summary>
    public int Buttons { get; private set; }

    /// <summary>Reads the pads. Empty slots are only re-checked twice a second (asking an empty
    /// slot is slow).</summary>
    public void Poll()
    {
        long now = Environment.TickCount64;
        bool scan = now >= nextScan;
        if (scan) nextScan = now + 500;
        int all = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!connected[i] && !scan) continue;
            if (!TryGet(i, out var st)) { connected[i] = false; continue; }
            connected[i] = true;
            all |= st.Buttons;
            if (st.LeftTrigger > 60) all |= LeftTrigger;
            if (st.RightTrigger > 60) all |= RightTrigger;
        }
        Buttons = all;
    }

    bool TryGet(int user, out XInputState st)
    {
        st = default;
        try { return (useOld ? GetState910(user, out st) : GetState14(user, out st)) == 0; }
        catch (DllNotFoundException) when (!useOld) { useOld = true; return TryGet(user, out st); }
        catch (Exception) { return false; }
    }

    public bool IsDown(int button) => button != 0 && (Buttons & button) == button;
}
