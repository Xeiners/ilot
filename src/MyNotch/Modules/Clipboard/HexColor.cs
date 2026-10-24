using System.Globalization;
using Windows.UI;

namespace MyNotch.Modules.Clipboard;

/// <summary>CSS hexadecimal colours: <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> and <c>#RRGGBBAA</c>.</summary>
public static class HexColor
{
    public static bool TryParse(string text, out Color color)
    {
        color = default;
        var hex = text.AsSpan().Trim();
        if (hex.Length < 4 || hex[0] != '#')
        {
            return false;
        }

        hex = hex[1..];
        if (hex.Length is not (3 or 4 or 6 or 8))
        {
            return false;
        }

        Span<char> full = stackalloc char[8];
        if (hex.Length <= 4)
        {
            // Short form: every digit is doubled (#F80 is #FF8800).
            for (int i = 0; i < hex.Length; i++)
            {
                full[2 * i] = full[2 * i + 1] = hex[i];
            }

            full = full[..(hex.Length * 2)];
        }
        else
        {
            hex.CopyTo(full);
            full = full[..hex.Length];
        }

        if (!uint.TryParse(full, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        color = full.Length == 8
            ? Color.FromArgb((byte)value, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8))
            : Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}
