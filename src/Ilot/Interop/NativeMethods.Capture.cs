using System.Runtime.InteropServices;

namespace Ilot.Interop;

/// <summary>Win32 surface used by the assistant: the screen copied as it shows (see ScreenCaptureService), the console's code page.</summary>
internal static partial class NativeMethods
{
    // BitBlt: the pixels as they are, layered windows (other apps' overlays) included.
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;

    public const uint DIB_RGB_COLORS = 0;

    // A window shown on the screen, but left out of what is captured of it (Windows 10 2004 and later).
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    /// <summary>The OEM code page: what console programs (ipconfig, ping…) write in when their output is redirected.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetOEMCP();
}
