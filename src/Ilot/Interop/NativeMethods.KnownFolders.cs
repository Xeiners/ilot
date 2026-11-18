using System.Runtime.InteropServices;

namespace Ilot.Interop;

/// <summary>Win32 surface used by the assistant to find the user's folders (Downloads has no Environment.SpecialFolder).</summary>
internal static partial class NativeMethods
{
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);

    /// <summary>A known folder's path (where the user moved it, if they did); <c>null</c> if Windows does not say.</summary>
    public static string? KnownFolder(Guid folder)
    {
        if (SHGetKnownFolderPath(folder, 0, 0, out nint path) != 0)
        {
            Marshal.FreeCoTaskMem(path);
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
