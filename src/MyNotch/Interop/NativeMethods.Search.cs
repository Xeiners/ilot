using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>Win32 surface used by the search: Everything's window messages, the shell's icons, the system commands.</summary>
internal static partial class NativeMethods
{
    // Everything: queries go to its hidden window as WM_COPYDATA, the results come back the same way.
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_USER = 0x0400;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct CopyData
    {
        public nint Data;
        public uint Size;
        public nint Pointer;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static partial nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    // The Everything service (full speed: it reads the disks' file tables for Everything).
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_RUNNING = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    /// <summary>Whether the service <paramref name="name"/> is installed and running (no rights needed to ask).</summary>
    public static bool IsServiceRunning(string name)
    {
        nint manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == 0)
        {
            return false;
        }

        try
        {
            nint service = OpenService(manager, name, SERVICE_QUERY_STATUS);
            if (service == 0)
            {
                return false;
            }

            try
            {
                return QueryServiceStatus(service, out var status) && status.CurrentState == SERVICE_RUNNING;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    // Shell icons: an item's image, as the Explorer draws it, for any path or "shell:AppsFolder\…" entry.
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out nint item);

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static unsafe partial int GetObject(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    public static unsafe partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapHeader* info, uint usage);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hwnd, nint dc);

    // System commands.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    public const uint SHERB_NOCONFIRMATION = 0x1;
    public const uint SHERB_NOPROGRESSUI = 0x2;
    public const uint SHERB_NOSOUND = 0x4;

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);

    // The Explorer's context menu of a result (see ShellMenu).
    public const uint CMF_NORMAL = 0x0;
    public const uint CMF_EXTENDEDVERBS = 0x100;
    public const uint CMIC_MASK_UNICODE = 0x4000;
    public const uint CMIC_MASK_PTINVOKE = 0x20000000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const int SW_SHOWNORMAL = 1;
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_MEASUREITEM = 0x002C;
    public const uint WM_INITMENUPOPUP = 0x0117;
    public const uint WM_MENUCHAR = 0x0120;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    public static partial int SHBindToParent(nint pidl, ref Guid riid, out nint folder, out nint child);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    public const uint GCS_VERBW = 0x4;

    [LibraryImport("user32.dll")]
    public static partial int GetMenuItemCount(nint menu);

    [LibraryImport("user32.dll")]
    public static partial uint GetMenuItemID(nint menu, int position);

    // « Open with », for a file.
    public const int OAIF_ALLOW_REGISTRATION = 0x1;
    public const int OAIF_REGISTER_EXT = 0x2;
    public const int OAIF_EXEC = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenAsInfo
    {
        public nint File;
        public nint Class;
        public int Flags;
    }

    [LibraryImport("shell32.dll")]
    public static partial int SHOpenWithDialog(nint hwnd, ref OpenAsInfo info);
}
