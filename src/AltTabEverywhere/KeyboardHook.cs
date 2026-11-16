using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>Navigation keys that jump rather than step.</summary>
internal enum JumpKey { Home, End, PageUp, PageDown }

/// <summary>
/// Global low-level keyboard hook that takes over the switcher shortcuts (Alt+Tab by default).
///
/// The hook callback must return fast (Windows silently removes hooks that time out), so it only
/// decides swallow / pass-through and posts the actual work to the dispatcher. The hook keeps its
/// own session state (which shortcut is open, how many search keystrokes) because posted work may
/// not have run yet when the next key arrives.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_DELETE = 0x2E,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_NUMPAD1 = 0x61, VK_NUMPAD9 = 0x69,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private static readonly IntPtr Swallow = new(1);
    private static readonly int[] SidedModifiers = { VK_LMENU, VK_RMENU, VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN };

    /// <summary>A shortcut as the hook needs it: keys plus whether its sessions stay open.</summary>
    public readonly record struct Binding(ShortcutKeys Keys, bool StayOpen);

    private readonly Native.LowLevelKeyboardProc _proc; // kept in a field so the GC can't collect it
    private readonly Dispatcher _dispatcher;
    private readonly bool[] _swallowedDown = new bool[256]; // swallow the key-up of every key-down we swallowed
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _chars = new char[8];
    private IntPtr _hook;
    private Binding[] _bindings = Array.Empty<Binding>();
    private int _session = -1;      // index of the shortcut whose session is open, -1 when none
    private int _searchStrokes;     // keystrokes typed into the search in this session

    /// <summary>(shortcut index, isFirst, reverse): the shortcut was pressed (again). Reverse when Shift is held.</summary>
    public event Action<int, bool, bool>? Shortcut;
    /// <summary>-1 / +1: previous / next tile (Left/Right).</summary>
    public event Action<int>? Move;
    /// <summary>-1 / +1: one row up / down (Up/Down arrows).</summary>
    public event Action<int>? MoveRow;
    public event Action<JumpKey>? Jump;
    /// <summary>-1 / +1: the key above Tab (with Shift: -1). Opens the selected stack, or moves within an open one.</summary>
    public event Action<int>? StackKey;
    /// <summary>Leave an open stack: Enter on its Back tile, or Backspace with nothing typed.</summary>
    public event Action? StackBack;
    /// <summary>The shortcut's modifier keys were all released.</summary>
    public event Action? ModifiersReleased;
    public event Action? Commit;
    public event Action? Cancel;
    /// <summary>Delete pressed while the switcher is open: close the selected window.</summary>
    public event Action? CloseSelected;
    /// <summary>1–9 pressed (before anything was typed into the search).</summary>
    public event Action<int>? Number;
    /// <summary>Text typed into the search (one keystroke).</summary>
    public event Action<string>? SearchType;
    /// <summary>Backspace: remove the last search keystroke.</summary>
    public event Action? SearchBackspace;
    /// <summary>Esc with search text: clear the search (a second Esc closes the switcher).</summary>
    public event Action? SearchClear;

    public bool Enabled { get; set; } = true;

    /// <summary>Whether Delete closes the selected window (otherwise it passes through).</summary>
    public bool DeleteCloses { get; set; } = true;

    public bool TypeToSearch { get; set; } = true;

    /// <summary>Set by the controller: the selection is an open stack's Back tile, so Enter goes back instead of switching.</summary>
    public bool BackSelected { get; set; }

    public bool NumberKeys { get; set; } = true;

    /// <summary>moveHereKey: switch to the selection and bring it to this monitor. Null: none.</summary>
    public SwitcherKey? MoveHereKey { get; set; }

    /// <summary>The move-here key was pressed (the session has ended, as for Enter).</summary>
    public event Action? MoveHere;

    public KeyboardHook()
    {
        _proc = HookProc;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// The shortcuts to watch. Stay-open: releasing the modifiers doesn't end the hook's session,
    /// so Tab, arrows, Enter and Esc keep working; the controller still decides whether to commit
    /// (a quick tap commits).
    /// </summary>
    public void SetBindings(IReadOnlyList<Binding> bindings)
    {
        _bindings = [.. bindings];
        if (_session >= _bindings.Length) _session = -1;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    /// <summary>
    /// Removes and re-installs the hook. Windows silently drops low-level hooks in some situations
    /// (e.g. a hook callback that once took too long, sleep/resume); re-installing is harmless.
    /// </summary>
    public void Reinstall()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        EndSession();
        Array.Clear(_swallowedDown);
        Install();
    }

    /// <summary>Called by the controller when a session ends for any reason (click, Esc, Enter...).</summary>
    public void EndSession()
    {
        _session = -1;
        _searchStrokes = 0;
        BackSelected = false;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled)
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((key.flags & Native.LLKHF_INJECTED) == 0 && Handle(key)) return Swallow;
            }
        }
        catch (Exception ex)
        {
            Log.Error("keyboard hook", ex);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <returns>true to swallow the key.</returns>
    private bool Handle(KBDLLHOOKSTRUCT key)
    {
        int vk = (int)key.vkCode & 0xFF;
        int scan = (int)key.scanCode;
        bool isUp = (key.flags & Native.LLKHF_UP) != 0;

        if (isUp)
        {
            // Releasing the session's modifiers normally picks the selected window. Let the key-up
            // through so Windows doesn't think the modifier is stuck.
            if (_session >= 0 && FamilyOf(vk) is var family && family != KeyModifiers.None &&
                (_bindings[_session].Keys.Modifiers & family) != 0 && !OtherModifiersDown(_bindings[_session].Keys.Modifiers, vk))
            {
                if (!_bindings[_session].StayOpen) EndSession();
                Post(() => ModifiersReleased?.Invoke());
                return false;
            }
            if (_swallowedDown[vk])
            {
                _swallowedDown[vk] = false;
                return true;
            }
            return false;
        }

        if (_session < 0)
        {
            for (int i = 0; i < _bindings.Length; i++)
            {
                var keys = _bindings[i].Keys;
                if (!keys.IsKey(vk, scan) || !ModifiersMatch(keys.Modifiers)) continue;
                _session = i;
                _searchStrokes = 0;
                bool reverse = ReverseHeld(keys.Modifiers);
                int index = i;
                Post(() => Shortcut?.Invoke(index, true, reverse));
                return SwallowDown(vk);
            }
            return false;
        }

        var session = _bindings[_session].Keys;
        if (FamilyOf(vk) != KeyModifiers.None) return false; // modifier presses (and their auto-repeat)

        // The session's own key again (Tab in an Alt+Tab session, with or without Alt): next tile.
        if (session.IsKey(vk, scan))
        {
            bool reverse = ReverseHeld(session.Modifiers);
            int index = _session;
            Post(() => Shortcut?.Invoke(index, false, reverse));
            return SwallowDown(vk);
        }

        // moveHereKey (Shift+Enter by default): like Enter, plus bringing the window to this monitor.
        // Shift or Ctrl count only when the session's own shortcut doesn't already hold them.
        if (MoveHereKey is { } move && vk == move.VirtualKey &&
            ((session.Modifiers & KeyModifiers.Shift) != 0 || IsDown(VK_SHIFT) == move.Shift) &&
            ((session.Modifiers & KeyModifiers.Ctrl) != 0 || IsDown(VK_CONTROL) == move.Ctrl))
        {
            if (BackSelected)
            {
                Post(() => StackBack?.Invoke()); // on Back it just goes back, like Enter
                return SwallowDown(vk);
            }
            EndSession();
            Post(() => MoveHere?.Invoke());
            return SwallowDown(vk);
        }

        switch (vk)
        {
            case VK_ESCAPE:
                if (_searchStrokes > 0)
                {
                    _searchStrokes = 0;
                    Post(() => SearchClear?.Invoke());
                }
                else
                {
                    EndSession();
                    Post(() => Cancel?.Invoke());
                }
                return SwallowDown(vk);
            case VK_RETURN when BackSelected:
                Post(() => StackBack?.Invoke()); // the session goes on, showing every window again
                return SwallowDown(vk);
            case VK_RETURN:
                EndSession();
                Post(() => Commit?.Invoke());
                return SwallowDown(vk);
            case VK_LEFT: Post(() => Move?.Invoke(-1)); return SwallowDown(vk);
            case VK_RIGHT: Post(() => Move?.Invoke(+1)); return SwallowDown(vk);
            case VK_UP: Post(() => MoveRow?.Invoke(-1)); return SwallowDown(vk);
            case VK_DOWN: Post(() => MoveRow?.Invoke(+1)); return SwallowDown(vk);
            case VK_HOME: Post(() => Jump?.Invoke(JumpKey.Home)); return SwallowDown(vk);
            case VK_END: Post(() => Jump?.Invoke(JumpKey.End)); return SwallowDown(vk);
            case VK_PRIOR: Post(() => Jump?.Invoke(JumpKey.PageUp)); return SwallowDown(vk);
            case VK_NEXT: Post(() => Jump?.Invoke(JumpKey.PageDown)); return SwallowDown(vk);
            case VK_DELETE when DeleteCloses:
                Post(() => CloseSelected?.Invoke());
                return SwallowDown(vk);
            case VK_BACK:
                if (TypeToSearch && _searchStrokes > 0)
                {
                    _searchStrokes--;
                    Post(() => SearchBackspace?.Invoke());
                }
                else Post(() => StackBack?.Invoke()); // nothing typed: leave an open stack (else nothing)
                return SwallowDown(vk);
        }

        // The key above Tab opens an app stack (by position, so it works on any layout).
        if (scan == ShortcutKeys.AboveTabScanCode)
        {
            int d = IsDown(VK_SHIFT) ? -1 : +1;
            Post(() => StackKey?.Invoke(d));
            return SwallowDown(vk);
        }

        // Ctrl or Win combinations that aren't part of the session's shortcut belong to Windows and apps.
        if (ExtraModifierDown(session.Modifiers, KeyModifiers.Ctrl, VK_LCONTROL, VK_RCONTROL) ||
            ExtraModifierDown(session.Modifiers, KeyModifiers.Win, VK_LWIN, VK_RWIN))
            return false;

        if (NumberKeys && _searchStrokes == 0 && DigitOf(vk) is int digit and > 0)
        {
            Post(() => Number?.Invoke(digit));
            return SwallowDown(vk);
        }

        if (TypeToSearch && TypedText(vk, scan) is { } text)
        {
            _searchStrokes++;
            Post(() => SearchType?.Invoke(text));
            return SwallowDown(vk);
        }

        return false;
    }

    private bool SwallowDown(int vk)
    {
        _swallowedDown[vk] = true;
        return true;
    }

    /// <summary>Ctrl / Alt / Win are down exactly as the shortcut needs; Shift is free unless required.</summary>
    private static bool ModifiersMatch(KeyModifiers required) =>
        Down(KeyModifiers.Alt, required, VK_LMENU, VK_RMENU) &&
        Down(KeyModifiers.Ctrl, required, VK_LCONTROL, VK_RCONTROL) &&
        Down(KeyModifiers.Win, required, VK_LWIN, VK_RWIN) &&
        ((required & KeyModifiers.Shift) == 0 || IsDown(VK_SHIFT));

    private static bool Down(KeyModifiers family, KeyModifiers required, int left, int right) =>
        (IsDown(left) || IsDown(right)) == ((required & family) != 0);

    /// <summary>Shift reverses direction, unless Shift is part of the shortcut itself.</summary>
    private static bool ReverseHeld(KeyModifiers modifiers) => (modifiers & KeyModifiers.Shift) == 0 && IsDown(VK_SHIFT);

    private static bool ExtraModifierDown(KeyModifiers session, KeyModifiers family, int left, int right) =>
        (session & family) == 0 && (IsDown(left) || IsDown(right));

    private static KeyModifiers FamilyOf(int vk) => vk switch
    {
        VK_MENU or VK_LMENU or VK_RMENU => KeyModifiers.Alt,
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => KeyModifiers.Ctrl,
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => KeyModifiers.Shift,
        VK_LWIN or VK_RWIN => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// Whether any of the shortcut's modifiers is still held, not counting the key being released
    /// (its own state isn't updated until after the hook returns).
    /// </summary>
    private static bool OtherModifiersDown(KeyModifiers modifiers, int releasing)
    {
        foreach (int vk in SidedModifiers)
        {
            if (vk == releasing || (modifiers & FamilyOf(vk)) == 0) continue;
            // A generic VK_MENU/VK_CONTROL/VK_SHIFT release: treat both sides of that family as released.
            if (releasing is VK_MENU or VK_CONTROL or VK_SHIFT && FamilyOf(vk) == FamilyOf(releasing)) continue;
            if (IsDown(vk)) return true;
        }
        return false;
    }

    /// <summary>1–9 on the top row or the number pad; 0 otherwise.</summary>
    private static int DigitOf(int vk) =>
        vk is >= '1' and <= '9' ? vk - '0' :
        vk is >= VK_NUMPAD1 and <= VK_NUMPAD9 ? vk - VK_NUMPAD1 + 1 : 0;

    /// <summary>
    /// The text this key types on the current keyboard layout, ignoring Alt (so Alt+F types "f"),
    /// or null for keys that don't type anything. Doesn't disturb dead-key state.
    /// </summary>
    private string? TypedText(int vk, int scan)
    {
        Array.Clear(_keyState);
        if (IsDown(VK_SHIFT)) _keyState[VK_SHIFT] = 0x80;
        if ((Native.GetKeyState(VK_CAPITAL) & 1) != 0) _keyState[VK_CAPITAL] = 0x01;
        uint thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        IntPtr layout = Native.GetKeyboardLayout(thread);
        int count = Native.ToUnicodeEx((uint)vk, (uint)scan, _keyState, _chars, _chars.Length,
            Native.TOUNICODE_NO_STATE_CHANGE, layout);
        if (count <= 0) return null; // nothing, or a dead key
        var text = new string(_chars, 0, count);
        foreach (char c in text)
            if (char.IsControl(c)) return null;
        return text;
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
