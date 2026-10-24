using System.Globalization;
using MyNotch.Interop;
using MyNotch.Localization;

namespace MyNotch.Modules.Clipboard;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A keyboard shortcut: modifiers and one key (a virtual key code). Saved as text, "Win+V" or "Ctrl+Shift+0xC0",
/// shown as the keys printed on the keyboard.
/// </summary>
public readonly record struct KeyGesture(KeyModifiers Modifiers, ushort Key)
{
    public static KeyGesture Default { get; } = new(KeyModifiers.Win, NativeMethods.VK_V);

    /// <summary>
    /// A shortcut the notch may take: with Ctrl, Alt or Windows (Shift alone would steal a capital letter), or a
    /// function key on its own.
    /// </summary>
    public bool IsValid => Key != 0 && ((Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0 || IsFunctionKey(Key));

    /// <summary>Windows and Alt, once released, would open the Start menu or a menu bar: they need covering up.</summary>
    public bool NeedsCover => (Modifiers & (KeyModifiers.Win | KeyModifiers.Alt)) != 0;

    /// <summary>The keys to show, in the usual order: "Win", "Ctrl", "Alt", "Maj", then the key.</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(KeyModifiers.Win))
            {
                parts.Add("Win");
            }

            if (Modifiers.HasFlag(KeyModifiers.Ctrl))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(KeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(KeyModifiers.Shift))
            {
                parts.Add(Loc.T("Maj"));
            }

            parts.Add(KeyName(Key));
            return parts;
        }
    }

    /// <summary>As people write it: "Windows + V".</summary>
    public string DisplayText => string.Join(" + ", Parts).Replace("Win", "Windows", StringComparison.Ordinal);

    public static bool IsModifierKey(ushort key) =>
        key is NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN
            or >= 0xA0 and <= 0xA5; // Left / right Shift, Ctrl, Alt.

    public override string ToString()
    {
        string key = Key switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Key).ToString(),
            _ when IsFunctionKey(Key) => $"F{Key - 0x6F}",
            _ => $"0x{Key:X2}",
        };
        return string.Join('+', Modifiers.ToString().Split(", ").Where(name => name != nameof(KeyModifiers.None)).Append(key));
    }

    /// <summary>Reads a saved shortcut; <c>null</c> when it is not one.</summary>
    public static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        foreach (string part in parts[..^1])
        {
            if (!Enum.TryParse(part, ignoreCase: true, out KeyModifiers modifier))
            {
                return null;
            }

            modifiers |= modifier;
        }

        string last = parts[^1];
        ushort? key = last switch
        {
            { Length: 1 } when char.IsAsciiLetterOrDigit(last[0]) => (ushort)char.ToUpperInvariant(last[0]),
            ['F' or 'f', ..] when int.TryParse(last[1..], out int number) && number is >= 1 and <= 24 => (ushort)(0x6F + number),
            ['0', 'x' or 'X', ..] when ushort.TryParse(last[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code) => code,
            _ => null,
        };

        return key is { } value ? new KeyGesture(modifiers, value) is { IsValid: true } gesture ? gesture : null : null;
    }

    private static bool IsFunctionKey(ushort key) => key is >= 0x70 and <= 0x87;

    private static string KeyName(ushort key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        _ when IsFunctionKey(key) => $"F{key - 0x6F}",
        0x20 => Loc.T("Espace"),
        0x09 => "Tab",
        0x0D => Loc.T("Entrée"),
        0x08 => Loc.T("Retour"),
        0x2D => Loc.T("Inser"),
        0x2E => Loc.T("Suppr"),
        0x24 => Loc.T("Début"),
        0x23 => Loc.T("Fin"),
        0x21 => Loc.T("Pg préc"),
        0x22 => Loc.T("Pg suiv"),
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        _ => NativeMethods.KeyName(key, extended: false) is { Length: > 0 } name ? name : $"0x{key:X2}",
    };
}
