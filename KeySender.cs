using System.Runtime.InteropServices;

namespace WinNotch;

/// <summary>Presses keyboard shortcuts (used to send Spotify its own shortcuts).</summary>
internal static class KeySender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["prtscn"] = 0x2C,
        ["plus"] = 0xBB, ["="] = 0xBB, ["minus"] = 0xBD, ["-"] = 0xBD,
        [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, [";"] = 0xBA, ["'"] = 0xDE,
        ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["`"] = 0xC0,
    };

    // Keys that need the "extended" flag to be read correctly by some apps
    private static readonly HashSet<ushort> Extended = new()
    {
        0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x5B, 0x2C
    };

    private static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B;

    /// <summary>Presses a shortcut like "Ctrl+Shift+S" or "Alt+Left".</summary>
    public static void Send(string shortcut)
    {
        var keys = new List<ushort>();
        foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryGetVk(raw, out ushort vk)) keys.Add(vk);
            else return; // unknown key name: do nothing rather than press the wrong thing
        }
        if (keys.Count == 0) return;

        // Modifiers first, then the main key(s); release in reverse order
        var ordered = keys.Where(IsModifier).Concat(keys.Where(k => !IsModifier(k))).ToList();
        var inputs = new List<INPUT>();
        foreach (var vk in ordered) inputs.Add(Key(vk, up: false));
        for (int i = ordered.Count - 1; i >= 0; i--) inputs.Add(Key(ordered[i], up: true));

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Turns "Ctrl+Alt+N" into RegisterHotKey modifiers (Alt=1, Ctrl=2, Shift=4, Win=8) and a key.</summary>
    public static bool TryParseHotkey(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetVk(raw, out ushort vk)) return false;
            switch (vk)
            {
                case 0x12: modifiers |= 1; break;
                case 0x11: modifiers |= 2; break;
                case 0x10: modifiers |= 4; break;
                case 0x5B: modifiers |= 8; break;
                default: key = vk; break;
            }
        }
        return key != 0;
    }

    private static bool TryGetVk(string name, out ushort vk)
    {
        if (Named.TryGetValue(name, out vk)) return true;

        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            vk = char.ToUpperInvariant(name[0]); // A-Z and 0-9 match their virtual key codes
            return true;
        }

        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + f - 1);
            return true;
        }

        vk = 0;
        return false;
    }

    private static INPUT Key(ushort vk, bool up)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, 0),
                    dwFlags = flags
                }
            }
        };
    }
}
