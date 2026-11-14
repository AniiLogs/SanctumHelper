using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SanctumHelper;

/// <summary>Global low-level keyboard/mouse hook that only watches one key. It never
/// swallows input, so the game still receives the key.</summary>
public sealed class KeyHook : IDisposable
{
    // Mouse buttons use pseudo virtual-key codes, the same ones Windows uses.
    public const int VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06, VK_MBUTTON = 0x04;

    readonly Native.LowLevelProc kbProc, msProc;
    readonly IntPtr module;
    IntPtr kbHook, msHook;
    volatile bool down;
    int key;
    bool capturing;

    /// <summary>The overlay key. The mouse hook is only there while it is a mouse button (or a key is being picked): every
    /// mouse move of the whole PC went through this process all day for nothing (14 Nov).</summary>
    public int Key { get => key; set { key = value; UpdateMouseHook(); } }
    /// <summary>A new key is being picked (a mouse button can be one): the mouse is watched meanwhile. Set on the UI thread.</summary>
    public bool Capturing { get => capturing; set { capturing = value; UpdateMouseHook(); } }
    public bool IsDown => down;
    /// <summary>Raised on the UI thread's hook callback for any key press; used by the settings
    /// window to capture a new binding.</summary>
    public event Action<int>? AnyKeyPressed;
    public event Action<bool>? StateChanged;

    public KeyHook(int key)
    {
        kbProc = KeyboardProc;
        msProc = MouseProc;
        using var mod = Process.GetCurrentProcess().MainModule;
        module = Native.GetModuleHandle(mod?.ModuleName);
        kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, module, 0);
        if (kbHook == IntPtr.Zero) Log.Write("keyboard hook failed: " + Marshal.GetLastWin32Error());
        Key = key;   // (installs the mouse hook when it is a mouse button)
    }

    static bool IsMouse(int vk) => vk is VK_MBUTTON or VK_XBUTTON1 or VK_XBUTTON2;

    /// <summary>The mouse hook on while a mouse button is the key or a key is being picked, off otherwise (on the thread
    /// that made this hook, which runs the message loop the hooks need).</summary>
    void UpdateMouseHook()
    {
        bool want = IsMouse(key) || capturing;
        if (want && msHook == IntPtr.Zero)
        {
            msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, msProc, module, 0);
            if (msHook == IntPtr.Zero) Log.Write("mouse hook failed: " + Marshal.GetLastWin32Error());
        }
        else if (!want && msHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(msHook);
            msHook = IntPtr.Zero;
        }
    }

    void Set(bool value)
    {
        if (down == value) return;
        down = value;
        StateChanged?.Invoke(value);
    }

    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg == 0x100 || msg == 0x104; // WM_KEYDOWN, WM_SYSKEYDOWN
            bool isUp = msg == 0x101 || msg == 0x105;
            int vk = (int)k.vkCode;
            if (isDown) AnyKeyPressed?.Invoke(vk);
            if (Matches(vk))
            {
                if (isDown) Set(true);
                else if (isUp) Set(false);
            }
        }
        return Native.CallNextHookEx(kbHook, code, wParam, lParam);
    }

    bool Matches(int vk) => vk == Key
        // Left/right variants arrive as specific codes; accept them for the generic key.
        || (Key == 0x10 && (vk == 0xA0 || vk == 0xA1))
        || (Key == 0x11 && (vk == 0xA2 || vk == 0xA3))
        || (Key == 0x12 && (vk == 0xA4 || vk == 0xA5));

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int vk = 0; bool isDown = false;
            switch (msg)
            {
                case 0x207: vk = VK_MBUTTON; isDown = true; break;      // WM_MBUTTONDOWN
                case 0x208: vk = VK_MBUTTON; break;
                case 0x20B: vk = (m.mouseData >> 16) == 1 ? VK_XBUTTON1 : VK_XBUTTON2; isDown = true; break; // WM_XBUTTONDOWN
                case 0x20C: vk = (m.mouseData >> 16) == 1 ? VK_XBUTTON1 : VK_XBUTTON2; break;
            }
            if (vk != 0)
            {
                if (isDown) AnyKeyPressed?.Invoke(vk);
                if (vk == Key) Set(isDown);
            }
        }
        return Native.CallNextHookEx(msHook, code, wParam, lParam);
    }

    /// <summary>Safety net for a missed key-up (e.g. focus changes while held).</summary>
    public void Poll()
    {
        if (down && (Native.GetAsyncKeyState(Key) & 0x8000) == 0) Set(false);
    }

    public static string Name(int vk) => vk switch
    {
        0x14 => "Caps Lock", 0x09 => "Tab", 0x20 => "Space", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl", 0xA4 => "Left Alt", 0xA5 => "Right Alt",
        0xC0 => "` (backtick)", 0x04 => "Middle mouse", 0x05 => "Mouse back (X1)", 0x06 => "Mouse forward (X2)",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Numpad " + (vk - 0x60),
        _ => $"Key 0x{vk:X2}",
    };

    public void Dispose()
    {
        if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
        if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
        kbHook = msHook = IntPtr.Zero;
    }
}
