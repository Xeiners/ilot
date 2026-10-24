using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyNotch.Interop;

namespace MyNotch.Modules.Search;

/// <summary>
/// Items' icons as the Explorer draws them (an app's, a file type's, a folder's), from what the shell knows the item by:
/// a path, or "shell:AppsFolder\…". Drawn by the shell on a thread of its own (some icon handlers need one), then kept:
/// alike items share one (every PDF…).
/// </summary>
public sealed class ShellIcons : IDisposable
{
    /// <summary>Pixels asked of the shell: sharp at 32 logical pixels up to 200 % scaling.</summary>
    private const int Pixels = 64;

    // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY: the icon (a picture's thumbnail would take longer), any size above.
    private const int Flags = 0x1 | 0x4;
    private const int MaxKept = 400;

    private readonly BlockingCollection<Request> _requests = [];
    private readonly Dictionary<string, ImageSource> _kept = [];
    private readonly Dictionary<string, Task<ImageSource?>> _drawing = [];

    public ShellIcons()
    {
        // Waits for work and costs nothing meanwhile.
        var thread = new Thread(Draw) { IsBackground = true, Name = "Search icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The icon of <paramref name="source"/>, kept under <paramref name="key"/>; <c>null</c> if the shell has none.</summary>
    public Task<ImageSource?> GetAsync(string source, string key)
    {
        if (_kept.TryGetValue(key, out var kept))
        {
            return Task.FromResult<ImageSource?>(kept);
        }

        if (!_drawing.TryGetValue(key, out var drawing))
        {
            drawing = Load(source, key);
            _drawing[key] = drawing;
        }

        return drawing;
    }

    public void Dispose() => _requests.CompleteAdding();

    private async Task<ImageSource?> Load(string source, string key)
    {
        var request = new Request(source, new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _requests.Add(request);
        var bitmap = await request.Done.Task; // Back on the UI thread: the image is made here.
        _drawing.Remove(key);
        if (bitmap is null)
        {
            return null;
        }

        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using (var pixels = image.PixelBuffer.AsStream())
        {
            pixels.Write(bitmap.Bgra);
        }

        image.Invalidate();
        if (_kept.Count >= MaxKept)
        {
            _kept.Clear();
        }

        _kept[key] = image;
        return image;
    }

    private void Draw()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            try
            {
                request.Done.TrySetResult(DrawIcon(request.Source));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                Debug.WriteLine($"[Search] No icon for {request.Source}: {ex.Message}");
                request.Done.TrySetResult(null);
            }
        }
    }

    private static unsafe Bitmap? DrawIcon(string source)
    {
        if (NativeMethods.SHCreateItemFromParsingName(source, 0, NativeMethods.IID_IShellItemImageFactory, out nint unknown) < 0 || unknown == 0)
        {
            return null;
        }

        nint handle = 0;
        try
        {
            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(unknown);
            if (factory.GetImage(new NativeSize(Pixels, Pixels), Flags, out handle) < 0 || handle == 0)
            {
                return null;
            }

            NativeMethods.BitmapInfo info;
            if (NativeMethods.GetObject(handle, sizeof(NativeMethods.BitmapInfo), &info) == 0 || info.Width <= 0 || info.Height == 0)
            {
                return null;
            }

            int width = info.Width;
            int height = Math.Abs(info.Height);
            var header = new NativeMethods.BitmapHeader
            {
                Size = (uint)sizeof(NativeMethods.BitmapHeader),
                Width = width,
                Height = -height, // Top-down rows.
                Planes = 1,
                BitCount = 32,
            };
            byte[] bgra = new byte[width * height * 4];
            nint screen = NativeMethods.GetDC(0);
            try
            {
                fixed (byte* bits = bgra)
                {
                    if (NativeMethods.GetDIBits(screen, handle, 0, (uint)height, bits, &header, 0) == 0)
                    {
                        return null;
                    }
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(0, screen);
            }

            Premultiply(bgra);
            return new Bitmap(width, height, bgra);
        }
        finally
        {
            if (handle != 0)
            {
                NativeMethods.DeleteObject(handle);
            }

            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// The image wants premultiplied alpha. The shell's is, mostly; some icons come straight (a colour brighter than its
    /// alpha gives it away), and old ones have no alpha at all (opaque).
    /// </summary>
    private static void Premultiply(byte[] bgra)
    {
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte alpha = bgra[i + 3];
            anyAlpha |= alpha != 0;
            straight |= bgra[i] > alpha || bgra[i + 1] > alpha || bgra[i + 2] > alpha;
        }

        if (!anyAlpha)
        {
            for (int i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }

            return;
        }

        if (!straight)
        {
            return;
        }

        for (int i = 0; i < bgra.Length; i += 4)
        {
            int alpha = bgra[i + 3];
            bgra[i] = (byte)(bgra[i] * alpha / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * alpha / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * alpha / 255);
        }
    }

    private sealed record Request(string Source, TaskCompletionSource<Bitmap?> Done);

    private sealed record Bitmap(int Width, int Height, byte[] Bgra);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out nint bitmap);
    }
}
