using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace MyNotch.Modules.Clipboard;

/// <summary>
/// An image being edited in the clipboard's preview: its pixels (BGRA, premultiplied, as the clipboard gives them),
/// drawn on with a pen and cropped here, without any graphics library: what is shown is exactly what is copied.
/// </summary>
public sealed class ImagePixels
{
    public ImagePixels(byte[] pixels, int width, int height)
    {
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException("Not BGRA pixels of that size.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public ImagePixels Clone() => new((byte[])Pixels.Clone(), Width, Height);

    /// <summary>Reads a PNG (the history keeps its images so).</summary>
    public static async Task<ImagePixels> LoadAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return new ImagePixels(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>Writes it as a PNG at <paramref name="path"/> (replaced if there).</summary>
    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var stream = file.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)Width, (uint)Height, 96, 96, Pixels);
            await encoder.FlushAsync();
        }
    }

    /// <summary>
    /// A pen stroke through <paramref name="points"/> (in pixels), <paramref name="radius"/> pixels from its middle line
    /// to its edge, round at its ends and turns, its edge smoothed. The stroke is laid at once (where it crosses itself it
    /// is not darker).
    /// </summary>
    public void DrawStroke(IReadOnlyList<Vector2> points, float radius, Color color)
    {
        if (points.Count == 0 || radius <= 0)
        {
            return;
        }

        // Where it may reach, inside the image: its coverage is gathered there first.
        float reach = radius + 1;
        int left = Math.Max(0, (int)Math.Floor(points.Min(p => p.X) - reach));
        int top = Math.Max(0, (int)Math.Floor(points.Min(p => p.Y) - reach));
        int right = Math.Min(Width - 1, (int)Math.Ceiling(points.Max(p => p.X) + reach));
        int bottom = Math.Min(Height - 1, (int)Math.Ceiling(points.Max(p => p.Y) + reach));
        if (left > right || top > bottom)
        {
            return;
        }

        int across = right - left + 1;
        var coverage = new float[across * (bottom - top + 1)];
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[Math.Min(i + 1, points.Count - 1)];
            int x0 = Math.Max(left, (int)Math.Floor(Math.Min(a.X, b.X) - reach));
            int y0 = Math.Max(top, (int)Math.Floor(Math.Min(a.Y, b.Y) - reach));
            int x1 = Math.Min(right, (int)Math.Ceiling(Math.Max(a.X, b.X) + reach));
            int y1 = Math.Min(bottom, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + reach));
            var along = b - a;
            float length = along.LengthSquared();
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float t = length > 0 ? Math.Clamp(Vector2.Dot(p - a, along) / length, 0, 1) : 0;
                    float distance = Vector2.Distance(p, a + along * t);
                    float covered = Math.Clamp(radius + 0.5f - distance, 0, 1);
                    ref float cell = ref coverage[(y - top) * across + x - left];
                    cell = Math.Max(cell, covered);
                }
            }
        }

        // Laid over the image, premultiplied: what it covers takes its colour, the rest shows through.
        float alpha = color.A / 255f;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                float a = coverage[(y - top) * across + x - left] * alpha;
                if (a <= 0)
                {
                    continue;
                }

                int at = (y * Width + x) * 4;
                Pixels[at] = (byte)Math.Round(color.B * a + Pixels[at] * (1 - a));
                Pixels[at + 1] = (byte)Math.Round(color.G * a + Pixels[at + 1] * (1 - a));
                Pixels[at + 2] = (byte)Math.Round(color.R * a + Pixels[at + 2] * (1 - a));
                Pixels[at + 3] = (byte)Math.Round(255 * a + Pixels[at + 3] * (1 - a));
            }
        }
    }

    /// <summary>The part of it inside <paramref name="left"/>, <paramref name="top"/>, <paramref name="width"/> × <paramref name="height"/> (clamped to it).</summary>
    public ImagePixels Crop(int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Width - 1);
        top = Math.Clamp(top, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - left);
        height = Math.Clamp(height, 1, Height - top);
        var cropped = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(Pixels, ((top + y) * Width + left) * 4, cropped, y * width * 4, width * 4);
        }

        return new ImagePixels(cropped, width, height);
    }

    /// <summary>It turned a quarter, clockwise (its width and height swap).</summary>
    public ImagePixels Rotate()
    {
        var turned = new byte[Pixels.Length];
        var from = MemoryMarshal.Cast<byte, uint>(Pixels);
        var to = MemoryMarshal.Cast<byte, uint>(turned.AsSpan());
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                to[x * Height + Height - 1 - y] = from[y * Width + x]; // (x, y) goes to (Height - 1 - y, x), Height wide.
            }
        }

        return new ImagePixels(turned, Height, Width);
    }

    /// <summary>It as in a mirror: left and right swapped.</summary>
    public ImagePixels Mirror()
    {
        var mirrored = new byte[Pixels.Length];
        var from = MemoryMarshal.Cast<byte, uint>(Pixels);
        var to = MemoryMarshal.Cast<byte, uint>(mirrored.AsSpan());
        for (int y = 0; y < Height; y++)
        {
            from.Slice(y * Width, Width).CopyTo(to.Slice(y * Width, Width));
            to.Slice(y * Width, Width).Reverse();
        }

        return new ImagePixels(mirrored, Width, Height);
    }

    /// <summary>Copies the pixels into <paramref name="buffer"/> (a WriteableBitmap's, of its size).</summary>
    public void CopyTo(IBuffer buffer) => Pixels.CopyTo(0, buffer, 0, Pixels.Length);
}
