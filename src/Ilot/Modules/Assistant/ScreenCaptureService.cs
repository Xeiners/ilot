using System.Diagnostics;
using System.Runtime.InteropServices;
using Ilot.Interop;

namespace Ilot.Modules.Assistant;

/// <summary>
/// The screen as the user sees it, for the assistant to look at (the bar's button, Ctrl + Shift + S, or Gemini asking
/// with its « capture_screen » tool): the screen the notch is on, copied at once (GDI, no picker, no border), without the
/// notch itself, which would only hide the top of it. Ready to send as a pasted image is (see <see cref="ChatImage"/>).
/// </summary>
/// <remarks>
/// The notch is left out by Windows' display affinity (WDA_EXCLUDEFROMCAPTURE), set for the moment of the copy only:
/// screenshots and screen sharing the user makes still show it. The other windows of the app (a note, a course) are
/// part of the screen like any other, and captured.
/// </remarks>
public sealed class ScreenCaptureService
{
    /// <summary>Windows takes this long to compose the screen without the notch, once it is told to leave it out.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(60);

    /// <summary>The notch's window (set by the bar as it opens): its screen is the one captured, and it is left out.</summary>
    public nint Window { get; set; }

    /// <summary>The screen, ready to send; <c>null</c> if it could not be copied.</summary>
    public async Task<ChatImage?> CaptureAsync()
    {
        nint window = Window;
        nint monitor = window != 0 ? NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST) : NativeMethods.CursorMonitor();
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var screen = info.Monitor; // Physical pixels: the app is aware of each screen's scaling.
        int width = screen.Right - screen.Left, height = screen.Bottom - screen.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        bool hidden = window != 0 && NativeMethods.SetWindowDisplayAffinity(window, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        byte[]? pixels;
        try
        {
            if (hidden)
            {
                await Task.Delay(Settle);
            }

            pixels = await Task.Run(() => Copy(screen.Left, screen.Top, width, height));
        }
        finally
        {
            if (hidden)
            {
                NativeMethods.SetWindowDisplayAffinity(window, NativeMethods.WDA_NONE);
            }
        }

        if (pixels is null)
        {
            return null;
        }

        try
        {
            return await ChatImage.FromPixelsAsync(pixels, width, height);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or OutOfMemoryException)
        {
            Debug.WriteLine($"[Capture] Not encoded: {ex.Message}");
            return null;
        }
    }

    /// <summary>The screen's pixels from (<paramref name="left"/>, <paramref name="top"/>): BGRA, opaque, top row first.</summary>
    private static unsafe byte[]? Copy(int left, int top, int width, int height)
    {
        nint screen = NativeMethods.GetDC(0);
        if (screen == 0)
        {
            return null;
        }

        nint memory = NativeMethods.CreateCompatibleDC(screen);
        nint bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
        try
        {
            if (memory == 0 || bitmap == 0)
            {
                return null;
            }

            nint before = NativeMethods.SelectObject(memory, bitmap);
            bool copied = NativeMethods.BitBlt(memory, 0, 0, width, height, screen, left, top, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            NativeMethods.SelectObject(memory, before);
            if (!copied)
            {
                return null;
            }

            var header = new NativeMethods.BitmapHeader
            {
                Size = (uint)sizeof(NativeMethods.BitmapHeader),
                Width = width,
                Height = -height, // Top-down rows.
                Planes = 1,
                BitCount = 32,
            };
            byte[] pixels = new byte[width * height * 4];
            fixed (byte* bits = pixels)
            {
                if (NativeMethods.GetDIBits(memory, bitmap, 0, (uint)height, bits, &header, NativeMethods.DIB_RGB_COLORS) == 0)
                {
                    return null;
                }
            }

            // GDI leaves the fourth byte as it likes: the screen is opaque.
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return pixels;
        }
        finally
        {
            if (bitmap != 0)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                NativeMethods.DeleteDC(memory);
            }

            NativeMethods.ReleaseDC(0, screen);
        }
    }
}
