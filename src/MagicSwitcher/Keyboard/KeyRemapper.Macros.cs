using System.Runtime.InteropServices;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Keyboard;

// Macros: a key of the Apple keyboard types a text (see KeyMacro).
//
// The hook sees the trigger key before raw input says which keyboard it came from. But raw input has reported every
// earlier key, so "the keyboard that typed last" is known: if that was the Apple keyboard, the trigger is swallowed
// and the macro runs at once. Otherwise the key is let through, and the macro runs a millisecond later if raw input
// then says the key did come from the Apple keyboard (the first key after a pause, or after another keyboard typed).
internal sealed partial class KeyRemapper
{
    private const uint KeyeventfKeyUp = 0x0002, KeyeventfUnicode = 0x0004;

    private readonly List<KeyMacro> _macros;
    private bool? _lastKeyboardApple;                                   // null: nothing typed yet
    private readonly HashSet<uint> _macroKeysDown = [];                 // swallowed trigger keys: swallow repeats and the release too
    private readonly Dictionary<uint, (KeyMacro Macro, long At)> _macroPending = [];

    /// <summary>In the hook. Returns true if the key belongs to a macro and must be swallowed.</summary>
    private bool HandleMacroKey(KBDLLHOOKSTRUCT k)
    {
        bool up = (k.flags & LlkhfUp) != 0;
        if (_macroKeysDown.Contains(k.vkCode))
        {
            if (up)
            {
                _macroKeysDown.Remove(k.vkCode);
            }

            return true;
        }

        if (up)
        {
            return false;
        }

        var macro = FindMacro(k.vkCode);
        if (macro is null)
        {
            return false;
        }

        if (_lastKeyboardApple == true)
        {
            _macroKeysDown.Add(k.vkCode);
            RunMacro(macro);
            return true;
        }

        _macroPending[k.vkCode] = (macro, Environment.TickCount64);
        return false;
    }

    /// <summary>From raw input, for every real key: which keyboard typed it. Runs a macro whose trigger was let through.</summary>
    private void NoteKeyboard(IntPtr device, uint virtualKey, bool up)
    {
        if (_macros.Count == 0 || device == IntPtr.Zero)
        {
            return;
        }

        bool apple = IsApple(device);
        _lastKeyboardApple = apple;
        if (!up && _macroPending.Remove(virtualKey, out var pending) && apple && Environment.TickCount64 - pending.At <= 500)
        {
            RunMacro(pending.Macro);
        }
    }

    private KeyMacro? FindMacro(uint virtualKey)
    {
        KeyMacro? found = null;
        foreach (var macro in _macros)
        {
            if (macro.Key != virtualKey)
            {
                continue;
            }

            found ??= macro.Ctrl == IsDown(0x11) && macro.Alt == IsDown(0x12) && macro.Shift == IsDown(0x10)
                                 && macro.Win == (IsDown(0x5B) || IsDown(0x5C))
                ? macro
                : null;
        }

        return found;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Types the text of the macro. The modifiers of the trigger are released first (Ctrl+letter would be a shortcut,
    /// not a letter). The typing runs on another thread, a few characters at a time: a hook must return quickly, and
    /// some windows — the sign-in screen among them — lose characters that arrive all at once.
    /// </summary>
    private void RunMacro(KeyMacro macro)
    {
        var release = new List<INPUT>();
        foreach (ushort modifier in new ushort[] { 0xA2, 0xA3, 0xA4, 0xA5, 0xA0, 0xA1, 0x5B, 0x5C })
        {
            if (!IsDown(modifier))
            {
                continue;
            }

            if (modifier is 0xA4 or 0xA5 or 0x5B or 0x5C)
            {
                // A lone Alt or Win release would open a menu: mask it.
                release.Add(VirtualKey(MaskKey, up: false));
                release.Add(VirtualKey(MaskKey, up: true));
            }

            release.Add(VirtualKey(modifier, up: true, extended: modifier is 0xA3 or 0xA5 or 0x5B or 0x5C));
        }

        var steps = ParseMacroText(macro.Text);
        Log.Info($"Macro {macro.DescribeTrigger()}: typing {macro.Text.Length} characters");
        _ = Task.Run(() =>
        {
            try
            {
                if (release.Count > 0)
                {
                    Send(release);
                    Thread.Sleep(20);
                }

                foreach (var step in steps)
                {
                    Send(step);
                    Thread.Sleep(8);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Macro failed", ex);
            }
        });
    }

    /// <summary>One list (key down + key up) per character or {KEY} token.</summary>
    private static List<List<INPUT>> ParseMacroText(string text)
    {
        var tokens = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["{TAB}"] = 0x09, ["{ENTER}"] = 0x0D, ["{ESC}"] = 0x1B, ["{BACKSPACE}"] = 0x08,
        };
        var steps = new List<List<INPUT>>();
        for (int i = 0; i < text.Length; i++)
        {
            ushort key = 0;
            if (text[i] == '{')
            {
                int end = text.IndexOf('}', i);
                if (end > i && tokens.TryGetValue(text[i..(end + 1)], out key))
                {
                    i = end;
                }
            }

            steps.Add(key != 0
                ? [VirtualKey(key, up: false), VirtualKey(key, up: true)]
                : [Character(text[i], up: false), Character(text[i], up: true)]);
        }

        return steps;
    }

    private static INPUT VirtualKey(ushort virtualKey, bool up, bool extended = false) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = (up ? KeyeventfKeyUp : 0) | (extended ? 0x0001u : 0), dwExtraInfo = Marker },
    };

    /// <summary>A character as it is (KEYEVENTF_UNICODE): independent of the keyboard layout.</summary>
    private static INPUT Character(char character, bool up) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wScan = character, dwFlags = KeyeventfUnicode | (up ? KeyeventfKeyUp : 0), dwExtraInfo = Marker },
    };

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
