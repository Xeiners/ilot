using System.Runtime.InteropServices;

namespace Ilot.Interop;

/// <summary>Win32 surface used by the windowing layer.</summary>
internal static partial class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_WNDPROC = -4;

    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;

    public const int WS_EX_DLGMODALFRAME = 0x00000001;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_WINDOWEDGE = 0x00000100;
    public const int WS_EX_CLIENTEDGE = 0x00000200;
    public const int WS_EX_STATICEDGE = 0x00020000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const nint SIZE_MINIMIZED = 1;
    public const nint SC_MINIMIZE = 0xF020;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const nint MA_NOACTIVATE = 3;
    public const nint SPI_SETWORKAREA = 0x002F;

    public const nint HWND_TOPMOST = -1;
    public const nint HWND_NOTOPMOST = -2;
    public const nint HWND_MESSAGE = -3;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const uint DWMWCP_DONOTROUND = 1;
    public const uint DWMWCP_ROUND = 2;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    public const uint DWMSBT_NONE = 1;

    private const int MDT_EFFECTIVE_DPI = 0;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref Point point);

    /// <summary>Enough to read a process's path, and nothing else (as the Task Manager does).</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary>A process's program (its path), read with the least access there is; <c>null</c> if it is not given.</summary>
    public static unsafe string? ProcessPath(int processId)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            char* name = stackalloc char[1024];
            int size = 1024;
            return QueryFullProcessImageName(process, 0, name, ref size) ? new string(name, 0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>The screen (monitor handle) the mouse pointer is on.</summary>
    public static nint CursorMonitor() =>
        GetCursorPos(out var point) ? MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) : 0;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_MINIMIZE = 6;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static unsafe string GetClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassNameW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, nint callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* buffer, int maxCount);

    // Clipboard: change notifications without polling, delivered to a message-only window.
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(
        int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    // Keyboard: a low-level hook to take over a shortcut Windows keeps for itself (Win+V), and synthetic keys.
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_V = 0x56;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_ESCAPE = 0x1B;
    public const uint MAPVK_VK_TO_VSC = 0;

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll", EntryPoint = "GetKeyNameTextW")]
    private static unsafe partial int GetKeyNameText(int keyData, char* buffer, int size);

    /// <summary>The key's name on this keyboard, in the user's language ("Ù", "Verr. maj"…); empty if unknown.</summary>
    public static unsafe string KeyName(ushort virtualKey, bool extended)
    {
        uint scanCode = MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
        char* buffer = stackalloc char[64];
        int length = GetKeyNameText((int)(scanCode << 16 | (extended ? 1u << 24 : 0)), buffer, 64);
        return length > 0 ? new string(buffer, 0, length) : "";
    }
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    // Mouse: a low-level hook, to follow the pointer where the notch lets the clicks through (see PointerWatcher).
    public const int WH_MOUSE_LL = 14;
    public const uint WM_MOUSEMOVE = 0x0200;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    // A click outside the open notch, seen by the same hook, and the window it lands on (see ClickWatcher).
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    // Power: the source in use, and the built-in screen's brightness, a setting of the active power plan (see
    // BrightnessService).
    [StructLayout(LayoutKind.Sequential)]
    public struct PowerStatus
    {
        public byte ACLineStatus; // 0 on battery, 1 plugged in.
        public byte BatteryFlag; // 128: no battery; 255: unknown.
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint root, out nint scheme);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint root, ref Guid scheme);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    // The user's last key or mouse input, kept by Windows (see UserActivity).
    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint Size;
        public uint Time; // Tick count, as Environment.TickCount.
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseHookData
    {
        public int X; // Physical screen pixels.
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>The mouse member is only there to give the union its native size.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(int hookId, nint callback, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    public static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);

    /// <summary>Synthetic key presses: each key goes down, then everything comes back up in reverse order.</summary>
    public static unsafe void PressKeys(params ushort[] keys)
    {
        var inputs = stackalloc Input[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i] = new Input { Type = INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = keys[i] } } };
            inputs[keys.Length * 2 - 1 - i] = new Input
            {
                Type = INPUT_KEYBOARD,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = keys[i], Flags = KEYEVENTF_KEYUP } },
            };
        }

        SendInput((uint)(keys.Length * 2), inputs, sizeof(Input));
    }

    /// <summary>Payload of <see cref="WM_WINDOWPOSCHANGING"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPos
    {
        public nint Hwnd;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    public static nint GetWindowLongPtr(nint hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    public static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, (int)value);

    /// <summary>Adds and removes window style bits (<see cref="GWL_STYLE"/> or <see cref="GWL_EXSTYLE"/>).</summary>
    public static void UpdateStyle(nint hwnd, int index, int add, int remove)
    {
        // Styles are 32-bit: mask the sign extension of WS_POPUP when widening to nint.
        long style = GetWindowLongPtr(hwnd, index) & 0xFFFFFFFFL;
        style = (style | (uint)add) & ~(long)(uint)remove;
        SetWindowLongPtr(hwnd, index, (nint)(int)(uint)style);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetDwmAttribute(nint hwnd, int attribute, uint value) =>
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(uint));

    /// <summary>
    /// Makes DWM honour the window's alpha channel, so everything the window does not paint is see-through.
    /// The blur region is deliberately empty: nothing gets blurred, only per-pixel transparency is enabled.
    /// </summary>
    public static void EnablePerPixelTransparency(nint hwnd)
    {
        nint emptyRegion = CreateRectRgn(-2, -2, -1, -1);
        var blurBehind = new DwmBlurBehind
        {
            Flags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            Enable = 1,
            BlurRegion = emptyRegion,
        };
        _ = DwmEnableBlurBehindWindow(hwnd, ref blurBehind);
        DeleteObject(emptyRegion);
    }

    public static double GetScaleForMonitor(nint monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr64(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(nint hwnd, int index, int value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static partial nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    public const int RGN_OR = 2;

    [LibraryImport("gdi32.dll")]
    public static partial int CombineRgn(nint destination, nint first, nint second, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmEnableBlurBehindWindow(nint hwnd, ref DwmBlurBehind blurBehind);

    private const uint DWM_BB_ENABLE = 0x1;
    private const uint DWM_BB_BLURREGION = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        public int Enable;
        public nint BlurRegion;
        public int TransitionOnMaximized;
    }

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
