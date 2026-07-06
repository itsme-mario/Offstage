using System;

namespace Offstage;

/// <summary>
/// A parsed global hotkey — RegisterHotKey modifier flags plus a virtual-key code. Accepts strings
/// like "Ctrl+Alt+S", "Shift+Win+F2", "Ctrl+Alt+1". Requires at least one modifier plus a key.
/// </summary>
internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        uint mods = 0;
        uint vk = 0;

        foreach (string token in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= NativeMethods.MOD_CONTROL; break;
                case "alt": mods |= NativeMethods.MOD_ALT; break;
                case "shift": mods |= NativeMethods.MOD_SHIFT; break;
                case "win":
                case "windows":
                case "super": mods |= NativeMethods.MOD_WIN; break;
                default:
                    if (vk != 0 || !TryParseKey(token, out vk))
                        return false; // second non-modifier token, or unrecognised key.
                    break;
            }
        }

        if (mods == 0 || vk == 0)
            return false;

        hotkey = new Hotkey(mods | NativeMethods.MOD_NOREPEAT, vk);
        return true;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;

        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c; // VK codes for A–Z and 0–9 equal their ASCII values.
                return true;
            }
        }

        if (key.Length >= 2 && (key[0] is 'f' or 'F') &&
            int.TryParse(key.AsSpan(1), out int n) && n is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (n - 1)); // VK_F1 = 0x70.
            return true;
        }

        return false;
    }
}
