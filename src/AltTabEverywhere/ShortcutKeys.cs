using System;

namespace AltTabEverywhere;

[Flags]
internal enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key combination that opens the switcher, parsed from text such as "Alt+Tab", "Win+Tab",
/// "Ctrl+Alt+Tab" or "Alt+`". Pure parsing (no Win32), so it can be unit-tested.
///
/// The "`" key means the key above Tab, wherever it is on the keyboard layout: it is matched by
/// its scan code, not by the character it types.
/// </summary>
internal readonly record struct ShortcutKeys(KeyModifiers Modifiers, int VirtualKey, int ScanCode)
{
    public const int VK_TAB = 0x09, VK_SPACE = 0x20;
    /// <summary>Scan code of the key above Tab (` on US layouts, ^ on German, ² on French...).</summary>
    public const int AboveTabScanCode = 0x29;

    /// <summary>Whether this key event is this shortcut's main key (ignoring modifiers).</summary>
    public bool IsKey(int vk, int scanCode) => ScanCode != 0 ? scanCode == ScanCode : vk == VirtualKey;

    public static bool TryParse(string? text, out ShortcutKeys keys)
    {
        keys = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = KeyModifiers.None;
        int vk = 0, scan = 0;
        bool haveKey = false;
        foreach (string raw in text.Split('+'))
        {
            string token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0)
            {
                // "Alt++" would mean the plus key; not supported.
                return false;
            }

            KeyModifiers modifier = token switch
            {
                "alt" => KeyModifiers.Alt,
                "ctrl" or "control" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "win" or "windows" or "super" or "meta" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (modifier != KeyModifiers.None)
            {
                if ((modifiers & modifier) != 0) return false; // repeated
                modifiers |= modifier;
                continue;
            }

            if (haveKey || !TryParseKey(token, out vk, out scan)) return false;
            haveKey = true;
        }

        // A shortcut needs a key and at least one of Alt / Ctrl / Win: a bare key (or Shift+key)
        // would take that key away from every app.
        if (!haveKey || (modifiers & (KeyModifiers.Alt | KeyModifiers.Ctrl | KeyModifiers.Win)) == 0) return false;
        keys = new ShortcutKeys(modifiers, vk, scan);
        return true;
    }

    private static bool TryParseKey(string token, out int vk, out int scan)
    {
        vk = 0;
        scan = 0;
        switch (token)
        {
            case "tab": vk = VK_TAB; return true;
            case "space": vk = VK_SPACE; return true;
            case "`" or "backquote" or "backtick" or "grave" or "abovetab":
                scan = AboveTabScanCode;
                return true;
            case "esc" or "escape":
                vk = 0x1B;
                return true;
        }
        if (token.Length == 1 && token[0] is >= 'a' and <= 'z') { vk = char.ToUpperInvariant(token[0]); return true; }
        if (token.Length == 1 && token[0] is >= '0' and <= '9') { vk = token[0]; return true; }
        if (token.Length is 2 or 3 && token[0] == 'f' && int.TryParse(token.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            vk = 0x70 + f - 1; // VK_F1..VK_F24
            return true;
        }
        return false;
    }

    public override string ToString()
    {
        string mods = string.Concat(
            (Modifiers & KeyModifiers.Ctrl) != 0 ? "Ctrl+" : "",
            (Modifiers & KeyModifiers.Alt) != 0 ? "Alt+" : "",
            (Modifiers & KeyModifiers.Shift) != 0 ? "Shift+" : "",
            (Modifiers & KeyModifiers.Win) != 0 ? "Win+" : "");
        string key = ScanCode == AboveTabScanCode ? "`"
            : VirtualKey == VK_TAB ? "Tab"
            : VirtualKey == VK_SPACE ? "Space"
            : VirtualKey == 0x1B ? "Esc"
            : VirtualKey is >= 0x70 and <= 0x87 ? $"F{VirtualKey - 0x70 + 1}"
            : ((char)VirtualKey).ToString();
        return mods + key;
    }
}

/// <summary>
/// A key pressed while the switcher is open, such as "Shift+Enter", "Insert" or "F2" (moveHereKey).
/// Only Shift and Ctrl may be added: the switcher's own modifier is already held.
/// </summary>
internal readonly record struct SwitcherKey(int VirtualKey, bool Shift, bool Ctrl)
{
    public const int VK_RETURN = 0x0D, VK_INSERT = 0x2D;

    /// <summary>Parses the setting; empty or "off" means no key (returns false).</summary>
    public static bool TryParse(string? text, out SwitcherKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
        bool shift = false, ctrl = false;
        int vk = 0;
        foreach (string raw in text.Split('+'))
        {
            string token = raw.Trim().ToLowerInvariant();
            switch (token)
            {
                case "shift" when !shift: shift = true; continue;
                case "ctrl" or "control" when !ctrl: ctrl = true; continue;
            }
            if (vk != 0) return false; // two keys
            vk = token switch
            {
                "enter" or "return" => VK_RETURN,
                "insert" or "ins" => VK_INSERT,
                "space" => ShortcutKeys.VK_SPACE,
                _ when token.Length == 1 && token[0] is >= 'a' and <= 'z' => char.ToUpperInvariant(token[0]),
                _ when token.Length is 2 or 3 && token[0] == 'f' && int.TryParse(token.AsSpan(1), out int f) && f is >= 1 and <= 24 => 0x70 + f - 1,
                _ => 0,
            };
            if (vk == 0) return false;
        }
        if (vk == 0) return false;
        // Plain Enter already switches; moving needs something on top of it.
        if (vk == VK_RETURN && !shift && !ctrl) return false;
        key = new SwitcherKey(vk, shift, ctrl);
        return true;
    }
}
