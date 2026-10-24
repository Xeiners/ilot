using System.Diagnostics;

namespace Ilot;

/// <summary>
/// Where Ilot keeps what it saves (settings, notes, courses, the clipboard's history…): <c>%LOCALAPPDATA%\Ilot</c>. The app
/// was called MyNotch: the first launch after the rename moves <c>%LOCALAPPDATA%\MyNotch</c> there, all of it at once.
/// Should that fail (a file still in use: MyNotch running), this run keeps the old folder, and the next one tries again.
/// </summary>
public static class AppData
{
    private const string Name = "Ilot";
    private const string FormerName = "MyNotch";

    /// <summary>The folder, the old one moved into it if need be (the first time it is asked for).</summary>
    public static string Folder { get; } = Resolve();

    private static string Resolve()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(local, Name);
        string former = Path.Combine(local, FormerName);
        if (Directory.Exists(folder) || !Directory.Exists(former))
        {
            return folder;
        }

        try
        {
            Directory.Move(former, folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[AppData] {former} not moved yet: {ex.Message}");
            return former;
        }
    }
}
