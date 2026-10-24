using System.Diagnostics;

namespace Ilot;

/// <summary>
/// What went wrong, kept in <c>%LOCALAPPDATA%\Ilot\errors.log</c> (the last 256 Ko): unhandled exceptions, and the
/// failures worth reading again (a course Notch could not prepare, with the model asked).
/// </summary>
public static class ErrorLog
{
    private const long Largest = 256 * 1024;
    private static readonly object Writing = new();

    public static void Write(Exception exception) => Write(exception.ToString());

    public static void Write(string what)
    {
        Debug.WriteLine(what);
        try
        {
            string folder = AppData.Folder;
            string path = Path.Combine(folder, "errors.log");
            lock (Writing)
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(path) && new FileInfo(path).Length > Largest)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write: the debugger output has it.
        }
    }
}
