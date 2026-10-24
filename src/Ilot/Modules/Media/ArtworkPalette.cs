using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Ilot.Modules.Media;

/// <summary>
/// Finds "the colour of a cover": the hue the eye remembers, tuned to glow nicely on a black notch.
/// </summary>
public static class ArtworkPalette
{
    private const uint SampleSize = 24;

    /// <summary>Used for monochrome covers and when nothing is playing.</summary>
    public static readonly Color Neutral = Color.FromArgb(255, 226, 226, 232);

    public static async Task<Color> ExtractAccentAsync(byte[] encodedImage)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(encodedImage.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = SampleSize,
                ScaledHeight = SampleSize,
                InterpolationMode = BitmapInterpolationMode.Linear,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return PickAccent(pixels.DetachPixelData());
        }
        catch (Exception)
        {
            return Neutral;
        }
    }

    private static Color PickAccent(byte[] bgra)
    {
        double red = 0, green = 0, blue = 0, total = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0;
            double g = bgra[i + 1] / 255.0;
            double r = bgra[i + 2] / 255.0;
            double alpha = bgra[i + 3] / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double chroma = max - min;
            double lightness = (max + min) / 2;

            // Vivid mid-tones dominate; greys, blacks and blown-out highlights barely count.
            double weight = alpha * (chroma * chroma * (1 - Math.Abs(lightness - 0.5) * 1.4) + 1e-4);
            red += r * weight;
            green += g * weight;
            blue += b * weight;
            total += weight;
        }

        if (total <= 0)
        {
            return Neutral;
        }

        var (hue, saturation, light) = ToHsl(red / total, green / total, blue / total);
        if (saturation < 0.12)
        {
            return Neutral;
        }

        // Luminous and saturated enough to glow on pure black without turning neon.
        return FromHsl(hue, Math.Clamp(saturation * 1.25, 0.45, 0.9), Math.Clamp(light, 0.6, 0.72));
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2;
        double delta = max - min;
        if (delta < 1e-6)
        {
            return (0, 0, lightness);
        }

        double saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue = max == r ? (g - b) / delta % 6
                   : max == g ? (b - r) / delta + 2
                   : (r - g) / delta + 4;
        return ((hue * 60 + 360) % 360, Math.Clamp(saturation, 0, 1), lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - chroma / 2;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromArgb(255, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
