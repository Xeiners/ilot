using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyNotch.Modules.Search;

/// <summary>An app the Start menu lists: a desktop program or one from the Store.</summary>
/// <param name="Id">What the shell knows it by in <c>shell:AppsFolder</c> (an app id, or a program's path).</param>
public sealed record InstalledApp(string Name, string Id)
{
    public string ShellPath => $"shell:AppsFolder\\{Id}";
}

/// <summary>
/// The apps installed, exactly as the Start menu's "All apps" lists them (desktop programs and Store apps alike), read
/// from the shell's <c>shell:AppsFolder</c>. Read when the search first opens, then again when it opens a few minutes
/// later, in the background: the list in hand answers meanwhile.
/// </summary>
public sealed class AppCatalog
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    private IReadOnlyList<InstalledApp> _apps = [];
    private long _readAt;
    private bool _reading;

    public IReadOnlyList<InstalledApp> Apps => _apps;

    /// <summary>Raised on the UI thread when a new reading of the list is in.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the list again, in the background, unless it is fresh.</summary>
    public async void Refresh()
    {
        if (_reading || (_readAt != 0 && Stopwatch.GetElapsedTime(_readAt) < FreshFor))
        {
            return;
        }

        _reading = true;
        try
        {
            _apps = await ReadAsync();
            _readAt = Stopwatch.GetTimestamp();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Debug.WriteLine($"[Search] Apps not read: {ex.Message}");
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>The shell's objects want a single-threaded apartment: a short-lived thread of their own.</summary>
    private static Task<IReadOnlyList<InstalledApp>> ReadAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<InstalledApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Read());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Search apps",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<InstalledApp> Read()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new COMException("No Shell.Application.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic items = shell.NameSpace("shell:AppsFolder").Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || IsNoise(name) || !seen.Add(name))
                {
                    continue;
                }

                apps.Add(new InstalledApp(name, id));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return apps;
    }

    /// <summary>Uninstallers and the like, which the Start menu also lists: never what one looks for.</summary>
    private static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("désinstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("desinstall", StringComparison.OrdinalIgnoreCase);
}
