using System.Runtime.InteropServices;

namespace Ilot.Interop;

/// <summary>Win32 surface used by the assistant to list the windows open, and to ask some to close (see OpenWindows).</summary>
internal static partial class NativeMethods
{
    public const uint WM_CLOSE = 0x0010;
    public const uint GW_OWNER = 4;

    /// <summary>A window DWM keeps out of sight (another virtual desktop, a suspended app, a shell surface).</summary>
    public const int DWMWA_CLOAKED = 14;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    /// <summary>Posted, not sent: the window's app handles it in its own time (and may ask to save first).</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    public const int SW_RESTORE = 9;

    /// <summary>Any process may take the foreground (when Ilot may give it: it is the one in use).</summary>
    public const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    /// <summary>What Alt + Tab does to switch to a window (no key pressed for it).</summary>
    [LibraryImport("user32.dll")]
    public static partial void SwitchToThisWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    /// <summary>A window's title (« » if it has none).</summary>
    public static unsafe string WindowTitle(nint hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        length = Math.Min(length, 1024);
        char* buffer = stackalloc char[length + 1];
        int read = GetWindowTextW(hwnd, buffer, length + 1);
        return read > 0 ? new string(buffer, 0, read) : "";
    }

    /// <summary>The top-level windows, in their order on the screen (the front one first).</summary>
    public static unsafe List<nint> TopLevelWindows()
    {
        var found = new List<nint>();
        var handle = GCHandle.Alloc(found);
        try
        {
            EnumWindows(&Collect, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return found;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }
}
