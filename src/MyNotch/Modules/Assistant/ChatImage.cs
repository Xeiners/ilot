using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MyNotch.Modules.Assistant;

/// <summary>
/// An image joined to a question (pasted): ready to send (PNG, or JPEG when that would be too heavy, at most
/// <see cref="LargestSide"/> pixels on its longest side), with its size, and a preview for the bar and the conversation.
/// </summary>
public sealed class ChatImage
{
    /// <summary>Bigger images are scaled down: Gemini sees no more, and they would only be slower to send.</summary>
    public const int LargestSide = 1600;

    /// <summary>A PNG heavier than this is sent as a JPEG instead (photos).</summary>
    private const int HeaviestPng = 2_500_000;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic"];

    private BitmapImage? _preview;

    private ChatImage(GeminiImage image, int width, int height)
    {
        Image = image;
        Width = width;
        Height = height;
    }

    public GeminiImage Image { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The image to show (created on the UI thread the first time, then kept).</summary>
    public BitmapImage Preview
    {
        get
        {
            if (_preview is null)
            {
                _preview = new BitmapImage { DecodePixelHeight = 240 };
                _ = Load(_preview, Image.Data);
            }

            return _preview;

            static async Task Load(BitmapImage preview, byte[] data)
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(data.AsBuffer());
                stream.Seek(0);
                await preview.SetSourceAsync(stream);
            }
        }
    }

    /// <summary>
    /// Whether pasting <paramref name="clipboard"/> means images: a picture (a screenshot…), or image files copied in
    /// the Explorer. Text wins: an app copying text often joins a picture of it (Word, Excel), which is not meant.
    /// </summary>
    public static bool IsImage(DataPackageView clipboard) =>
        !clipboard.Contains(StandardDataFormats.Text)
        && (clipboard.Contains(StandardDataFormats.Bitmap) || clipboard.Contains(StandardDataFormats.StorageItems));

    /// <summary>The images on <paramref name="clipboard"/>, ready to send (none if it holds none that can be read).</summary>
    public static async Task<IReadOnlyList<ChatImage>> FromClipboardAsync(DataPackageView clipboard)
    {
        var images = new List<ChatImage>();
        try
        {
            if (clipboard.Contains(StandardDataFormats.Bitmap))
            {
                var bitmap = await clipboard.GetBitmapAsync();
                using var stream = await bitmap.OpenReadAsync();
                images.Add(await PrepareAsync(stream));
            }
            else if (clipboard.Contains(StandardDataFormats.StorageItems))
            {
                foreach (var item in await clipboard.GetStorageItemsAsync())
                {
                    if (item is StorageFile file && Extensions.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))
                    {
                        using var stream = await file.OpenReadAsync();
                        images.Add(await PrepareAsync(stream));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // The clipboard changed meanwhile, or an image that cannot be read: what could be is kept.
        }

        return images;
    }

    /// <summary>Decodes <paramref name="source"/>, scales it down if it is big, and encodes it to send.</summary>
    private static async Task<ChatImage> PrepareAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        double scale = Math.Min(1, (double)LargestSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
        uint height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        byte[] data = pixels.DetachPixelData();

        byte[] png = await EncodeAsync(BitmapEncoder.PngEncoderId, data, width, height);
        return png.Length <= HeaviestPng
            ? new ChatImage(new GeminiImage("image/png", png), (int)width, (int)height)
            : new ChatImage(new GeminiImage("image/jpeg", await EncodeAsync(BitmapEncoder.JpegEncoderId, data, width, height)), (int)width, (int)height);
    }

    private static async Task<byte[]> EncodeAsync(Guid codec, byte[] pixels, uint width, uint height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = codec == BitmapEncoder.JpegEncoderId
            ? await BitmapEncoder.CreateAsync(codec, stream, new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.88f, Windows.Foundation.PropertyType.Single) })
            : await BitmapEncoder.CreateAsync(codec, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
