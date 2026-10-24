using System.Runtime.InteropServices;

namespace Ilot.Interop;

// The taskbar: whether it hides itself (the lyrics may sit on it only when it stays).
internal static partial class NativeMethods
{
    public const uint ABM_GETSTATE = 0x00000004;
    public const nint ABS_AUTOHIDE = 0x0000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct AppBarData
    {
        public int Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public nint Param;
    }

    [LibraryImport("shell32.dll")]
    public static partial nint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    /// <summary>The taskbar hides itself until the pointer comes to the edge of the screen.</summary>
    public static bool TaskbarAutoHides()
    {
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        return (SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }
}
