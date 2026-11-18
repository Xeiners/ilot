using System.Runtime.InteropServices;

namespace Ilot.Interop;

/// <summary>Win32 surface used by the assistant's « Annuler »: what it made, put in the Recycle Bin (never destroyed silently).</summary>
internal static partial class NativeMethods
{
    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>Asks the user first if it would be destroyed rather than recycled (too big for the bin, or no bin there).</summary>
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom; // Paths, each ending with a null, the list with a second one.
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    public static partial int SHFileOperationW(ref SHFILEOPSTRUCTW operation);

    /// <summary>
    /// <paramref name="path"/> (a file or a folder) into the Recycle Bin: <c>null</c> once it is, else why not. Should it
    /// not fit there, Windows asks the user before destroying it; they may say no.
    /// </summary>
    public static string? Recycle(string path)
    {
        nint from = Marshal.StringToHGlobalUni(path + "\0"); // And the one the string gets: the list's end.
        try
        {
            var operation = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
            };
            int result = SHFileOperationW(ref operation);
            return operation.fAnyOperationsAborted != 0 ? Ilot.Localization.Loc.T("Laissé en place.")
                : result != 0 ? Ilot.Localization.Loc.F("Windows n'a pas pu le mettre à la Corbeille (code {0}).", result)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
