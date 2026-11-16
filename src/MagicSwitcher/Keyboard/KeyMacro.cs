namespace MagicSwitcher.Keyboard;

/// <summary>
/// A macro of the Apple keyboard: a key (alone or with modifiers) types a text.
/// <para>
/// In the text <c>{TAB}</c>, <c>{ENTER}</c>, <c>{ESC}</c> and <c>{BACKSPACE}</c> stand for those keys; everything else
/// is typed as it is, whatever the keyboard layout. The text is stored as plain text, so it is for things that are
/// not secret: a user name, an e-mail address, a phrase. There is deliberately no way to store a password in a macro
/// that works on the sign-in screen.
/// </para>
/// </summary>
public sealed record KeyMacro
{
    public const int MaxTextLength = 256;
    public const int MaxCount = 16;

    /// <summary>Virtual-key code of the trigger key: F3 or F6 (alone or with modifiers), A–Z or 0–9 (with Ctrl, Alt or Win).</summary>
    public int Key { get; init; }

    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }

    public string Text { get; init; } = "";

    /// <summary>Also works on the Windows sign-in and lock screens (the service runs a helper there).</summary>
    public bool OnSignInScreen { get; init; }

    /// <summary>
    /// The function keys that reach Windows as plain keys from an Apple keyboard in either top row mode (F1/F2 are kept
    /// by Apple's driver; F4, F5 and F7–F12 are media keys or shortcuts unless Fn Lock turns them into F-keys).
    /// </summary>
    public static IReadOnlyList<int> FunctionKeys { get; } = [0x72, 0x75]; // F3, F6

    public static bool IsLetterOrDigit(int key) => key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39;

    /// <summary>
    /// A function key may be used alone. A letter or a digit needs Ctrl, Alt or Win: alone (or with Shift) it would
    /// stop typing — also in the PIN on the sign-in screen.
    /// </summary>
    public bool IsValid =>
        Text.Length is > 0 and <= MaxTextLength
        && (FunctionKeys.Contains(Key) || (IsLetterOrDigit(Key) && (Ctrl || Alt || Win)));

    public string DescribeTrigger()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Control");
        if (Alt) parts.Add("Option");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Command");
        parts.Add(Key is >= 0x70 and <= 0x7B ? $"F{Key - 0x6F}" : ((char)Key).ToString());
        return string.Join("+", parts);
    }
}
