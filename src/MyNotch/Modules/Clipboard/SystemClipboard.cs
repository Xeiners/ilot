using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MyNotch.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace MyNotch.Modules.Clipboard;

/// <summary>What was found on the clipboard.</summary>
internal abstract record ClipboardCapture;

internal sealed record CapturedText(string Text) : ClipboardCapture;

/// <summary>Decoded image: BGRA, premultiplied, and the hash of its pixels.</summary>
internal sealed record CapturedImage(byte[] Pixels, int Width, int Height, string Hash) : ClipboardCapture;

/// <summary>Reads and writes the system clipboard (asynchronously, on the UI thread).</summary>
internal static class SystemClipboard
{
    // Larger copies are not kept: a history is for things worth pasting again, not for whole databases.
    private const int MaxTextLength = 512 * 1024;
    private const long MaxImagePixels = 40_000_000;
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(60);

    // The app getting the keyboard back needs a moment before it takes keys.
    private static readonly TimeSpan PasteDelay = TimeSpan.FromMilliseconds(120);

    // Formats set by password managers and other apps on sensitive copies: such content is never recorded.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>The clipboard's content, or <c>null</c> when there is nothing worth keeping (or it is private).</summary>
    public static async Task<ClipboardCapture?> ReadAsync()
    {
        // Another app may hold the clipboard open for a moment: try again shortly.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadOnceAsync();
            }
            catch (COMException ex) when (attempt < Attempts)
            {
                Debug.WriteLine($"[Clipboard] Busy, retrying: {ex.Message}");
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Clipboard] Unreadable: {ex.Message}");
                return null;
            }
        }
    }

    public static Task<bool> WriteTextAsync(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        return WriteAsync(package);
    }

    /// <summary>Formatted text (RTF, for Word and the like) with its plain version, for everything else.</summary>
    public static Task<bool> WriteRichTextAsync(string rtf, string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetRtf(rtf);
        package.SetText(text);
        return WriteAsync(package);
    }

    public static async Task<bool> WriteImageAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            return await WriteAsync(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Debug.WriteLine($"[Clipboard] Image not copied: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pastes into the app that has the keyboard (Ctrl+V on its behalf), once it is back in front. The keys are
    /// marked as synthetic: the Win+V shortcut never mistakes them for the user's.
    /// </summary>
    public static async Task PasteAsync()
    {
        await Task.Delay(PasteDelay);
        NativeMethods.PressKeys(NativeMethods.VK_CONTROL, NativeMethods.VK_V);
    }

    private static async Task<ClipboardCapture?> ReadOnceAsync()
    {
        var view = WinClipboard.GetContent();
        if (await IsPrivateAsync(view))
        {
            return null;
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length <= MaxTextLength ? new CapturedText(text) : null;
            }
        }

        if (view.Contains(StandardDataFormats.WebLink))
        {
            return new CapturedText((await view.GetWebLinkAsync()).AbsoluteUri);
        }

        return view.Contains(StandardDataFormats.Bitmap) ? await ReadImageAsync(view) : null;
    }

    private static async Task<bool> IsPrivateAsync(DataPackageView view)
    {
        if (view.Contains(ExcludeFormat) || view.Contains(ViewerIgnoreFormat))
        {
            return true;
        }

        if (!view.Contains(HistoryFormat))
        {
            return false;
        }

        // A DWORD: 0 asks clipboard history (and so this one) not to keep the content.
        try
        {
            if (await view.GetDataAsync(HistoryFormat) is IRandomAccessStream data)
            {
                using var stream = data.AsStreamForRead();
                var value = new byte[4];
                return await stream.ReadAtLeastAsync(value, 4, throwOnEndOfStream: false) < 4 || BitConverter.ToInt32(value) == 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipboard] Privacy flag unreadable: {ex.Message}");
        }

        // The app did say something about privacy: when in doubt, keep nothing.
        return true;
    }

    private static async Task<CapturedImage?> ReadImageAsync(DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if ((long)decoder.PixelWidth * decoder.PixelHeight is 0 or > MaxImagePixels)
        {
            return null;
        }

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        string hash = await Task.Run(() => Convert.ToHexString(SHA256.HashData(pixels)));
        return new CapturedImage(pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight, hash);
    }

    private static async Task<bool> WriteAsync(DataPackage package)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                WinClipboard.SetContent(package);
                WinClipboard.Flush(); // The copy outlives the notch.
                return true;
            }
            catch (COMException ex) when (attempt < Attempts)
            {
                Debug.WriteLine($"[Clipboard] Busy, retrying: {ex.Message}");
                await Task.Delay(RetryDelay);
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"[Clipboard] Not copied: {ex.Message}");
                return false;
            }
        }
    }
}
