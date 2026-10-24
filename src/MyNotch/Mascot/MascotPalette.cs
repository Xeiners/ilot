using System.Globalization;
using Windows.UI;

namespace MyNotch.Mascot;

/// <summary>
/// A colour the mascot can wear (see the settings): its id, as saved (a preset's name, or "#RRGGBB" for one picked
/// freely), its name, and its tone.
/// </summary>
public sealed record MascotColor(string Id, string Name, Color Tone)
{
    /// <summary>The body's lower half: the same tone, a little darker, which gives it volume.</summary>
    public Color Shade => Color.FromArgb(255, (byte)(Tone.R * 0.84), (byte)(Tone.G * 0.85), (byte)(Tone.B * 0.9));

    /// <summary>Picked freely, rather than one of the palette's.</summary>
    public bool IsCustom => Id.StartsWith('#');

    /// <summary>The eyes: the notch's black, or a light grey on a dark body, where black ones would not read.</summary>
    public Color Eyes => Luminance(Tone) < 0.4
        ? Color.FromArgb(255, 0xF2, 0xF2, 0xF7)
        : Color.FromArgb(255, 0, 0, 0);

    /// <summary>
    /// The mascot's colour where it must show on the black notch (the quick controls' rings, the volume mixer): its own,
    /// or its light when it is too dark to read there (never black on black).
    /// </summary>
    public Color Accent => Luminance(Tone) < 0.12 ? Light(0.72) : Tone;

    /// <summary>
    /// The assistant's light in this colour (the notch's glow, the drop, the orb): its hue, vivid enough to glow on the
    /// black notch. A colour with hardly any hue (white, grey, black) gives a cool silver: never black, it must show.
    /// </summary>
    public Color Glow => Light(0.62);

    /// <summary>The light at its brightest, nearly white: an edge catching it.</summary>
    public Color GlowBright => Light(0.86);

    /// <summary>The light sunk in the dark: the drop's depth, the orb's heart.</summary>
    public Color GlowDeep => Light(0.11);

    /// <summary>The light's hue at <paramref name="lightness"/> (HSL, 0 to 1).</summary>
    public Color Light(double lightness)
    {
        var (hue, saturation) = HueOf(Tone);
        if (saturation < 0.12)
        {
            // Silver: a touch of cool blue, and brighter where it glows (grey reads dull on black).
            return FromHsl(220, 0.22, lightness > 0.4 ? Math.Min(lightness + 0.14, 0.95) : lightness);
        }

        return FromHsl(hue, lightness < 0.3 ? Math.Min(saturation, 0.7) : Math.Max(saturation, 0.6), lightness);
    }

    /// <summary>A colour's hue (degrees) and saturation (HSL, 0 to 1).</summary>
    private static (double Hue, double Saturation) HueOf(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;
        if (chroma < 1e-6)
        {
            return (0, 0);
        }

        double lightness = (max + min) / 2;
        double saturation = chroma / (1 - Math.Abs(2 * lightness - 1));
        double hue = max == r ? (g - b) / chroma % 6 : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        return ((hue * 60 + 360) % 360, Math.Clamp(saturation, 0, 1));
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - chroma / 2;
        var (r, g, b) = (hue / 60) switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromArgb(255, Channel(r + m), Channel(g + m), Channel(b + m));

        static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }

    /// <summary>Relative luminance (sRGB, as WCAG has it), 0 for black to 1 for white.</summary>
    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}

/// <summary>The mascot's colours: soft, light tones, on which its black eyes always read, or any one picked freely.</summary>
public static class MascotPalette
{
    public const string DefaultId = "white";

    public static IReadOnlyList<MascotColor> Colors { get; } =
    [
        new(DefaultId, "Blanc", Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
        new("lavender", "Lavande", Color.FromArgb(255, 0xB7, 0xA0, 0xFB)),
        new("sky", "Ciel", Color.FromArgb(255, 0x5C, 0xC9, 0xF9)),
        new("mint", "Menthe", Color.FromArgb(255, 0x58, 0xDB, 0xAB)),
        new("lemon", "Citron", Color.FromArgb(255, 0xFB, 0xD5, 0x3F)),
        new("peach", "Pêche", Color.FromArgb(255, 0xFB, 0xA6, 0x5F)),
        new("pink", "Rose", Color.FromArgb(255, 0xF6, 0x8B, 0xC3)),
        new("coral", "Corail", Color.FromArgb(255, 0xF9, 0x8B, 0x8B)),
    ];

    /// <summary>The colour saved as <paramref name="id"/>: a preset, one picked freely ("#RRGGBB"), or white.</summary>
    public static MascotColor Find(string? id)
    {
        if (id is { Length: 7 } && id[0] == '#'
            && uint.TryParse(id.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            var tone = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return new(Id(tone), "Personnalisée", tone);
        }

        return Colors.FirstOrDefault(color => color.Id == id) ?? Colors[0];
    }

    /// <summary>The id a colour picked freely is saved as: "#RRGGBB".</summary>
    public static string Id(Color tone) => $"#{tone.R:X2}{tone.G:X2}{tone.B:X2}";
}
