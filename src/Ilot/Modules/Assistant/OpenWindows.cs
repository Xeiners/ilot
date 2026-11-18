using System.Diagnostics;
using Ilot.Interop;
using Ilot.Modules.Search;

namespace Ilot.Modules.Assistant;

/// <summary>
/// A window open on the screen, as the assistant sees it: its handle (its id for Gemini), its title, its app (the
/// program's name, « explorer », « Code », and how it calls itself, « Visual Studio Code »), its process.
/// </summary>
public sealed record OpenWindow(nint Handle, string Title, string Program, string App, int ProcessId, string Class)
{
    public string Id => Handle.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The windows the assistant may see and close (its « list_windows » and « close_windows » tools): the apps' own
/// windows on the screen (shown, titled, not owned by another, not hidden by Windows on another desktop), never
/// Windows' own surfaces (the desktop, the taskbar, the Start menu, the lock screen…), Explorer's only for its folder
/// windows, and never Ilot's. Closing one is asking it to (as its cross does): its app may ask to save first, or say
/// no; nothing is ever forced, no process is ended. A process is only read about, with the least access there is.
/// </summary>
public static class OpenWindows
{
    /// <summary>Windows' own processes: their windows are never listed (the shell, the sign-in, the input).</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "winlogon", "dwm", "lsass", "services", "smss", "wininit", "LogonUI", "LockApp", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "ShellHost", "Widgets", "WidgetBoard",
        "SystemSettingsBroker", "ctfmon", "sihost", "taskhostw", "RuntimeBroker", "consent", "CredentialUIBroker",
    };

    /// <summary>Window classes that are the shell's, whatever process shows them.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "NotifyIconOverflowWindow",
    };

    /// <summary>Explorer's folder windows (its other windows are the desktop and the taskbar).</summary>
    private const string FolderWindow = "CabinetWClass";

    /// <summary>A window asked to close still there after this long: its app is asking something, or says no.</summary>
    private static readonly TimeSpan Waited = TimeSpan.FromSeconds(2);

    private static readonly Dictionary<string, string> Described = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The apps' windows open now, the front one first.</summary>
    public static IReadOnlyList<OpenWindow> List()
    {
        var windows = new List<OpenWindow>();
        foreach (nint hwnd in NativeMethods.TopLevelWindows())
        {
            if (Read(hwnd) is { } window)
            {
                windows.Add(window);
            }
        }

        return windows;
    }

    /// <summary><paramref name="hwnd"/> as the assistant sees it, if it is an app's window it may close; <c>null</c> otherwise.</summary>
    private static OpenWindow? Read(nint hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0
            || (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return null;
        }

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        string title = NativeMethods.WindowTitle(hwnd);
        string type = NativeMethods.GetClassName(hwnd);
        if (title.Trim().Length == 0 || ShellClasses.Contains(type))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return null; // Ilot's own: never.
        }

        string? path = NativeMethods.ProcessPath((int)processId);
        string program = path is null ? "" : Path.GetFileNameWithoutExtension(path);
        if (Shell.Contains(program) || (program.Equals("explorer", StringComparison.OrdinalIgnoreCase) && type != FolderWindow))
        {
            return null;
        }

        return new OpenWindow(hwnd, title, program, Describe(path, program, title), (int)processId, type);
    }

    /// <summary>How the program calls itself (« Visual Studio Code », « Explorateur Windows »), read once from its file.</summary>
    private static string Describe(string? path, string program, string title)
    {
        if (path is null)
        {
            return program.Length > 0 ? program : title;
        }

        lock (Described)
        {
            if (!Described.TryGetValue(path, out string? name))
            {
                try
                {
                    name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    name = null;
                }

                name = string.IsNullOrEmpty(name) ? program : name;
                Described[path] = name;
            }

            return name;
        }
    }

    /// <summary>
    /// Those of <paramref name="windows"/> that are <paramref name="ids"/>, or of <paramref name="app"/> (the program,
    /// or what it calls itself: « explorer » or « explorateur » for the folders, « code », « chrome »), and whose title
    /// holds <paramref name="title"/>; all that is given must match.
    /// </summary>
    public static List<OpenWindow> Match(IReadOnlyList<OpenWindow> windows, IReadOnlyCollection<string> ids, string app, string title)
    {
        string wanted = app.Trim();
        if (wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            wanted = wanted[..^4];
        }

        bool folders = wanted.Length > 0 && FuzzyMatch.Fold(wanted) is "explorateur" or "explorateur de fichiers" or "explorateur windows"
            or "file explorer" or "windows explorer" or "dossiers" or "folders";
        return windows.Where(window =>
                (ids.Count == 0 || ids.Contains(window.Id))
                && (wanted.Length == 0
                    || window.Program.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || (folders && window.Class == FolderWindow)
                    || FuzzyMatch.Fold(window.App).Contains(FuzzyMatch.Fold(wanted), StringComparison.Ordinal))
                && (title.Length == 0 || FuzzyMatch.Fold(window.Title).Contains(FuzzyMatch.Fold(title), StringComparison.Ordinal)))
            .ToList();
    }

    // ── To the front ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A launched app's window looked for this long at most (VS Code, a big app, takes a few seconds).</summary>
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(8);

    /// <summary>No new window by then: an app already open may have been woken instead (its window, found by its name).</summary>
    private static readonly TimeSpan NewWindowFirst = TimeSpan.FromMilliseconds(1500);

    /// <summary>The windows open now, by handle: what is new after a launch is told from them.</summary>
    public static HashSet<nint> Snapshot() => [.. List().Select(window => window.Handle)];

    /// <summary>
    /// After a launch: the window it opened (one not in <paramref name="before"/>, of <paramref name="processId"/> if
    /// known), or, when none comes, the one already open it woke (its title or app holding <paramref name="hint"/>;
    /// else the front window of <paramref name="program"/>: the browser a page opened in, as a tab), brought to the
    /// front, as the user would by clicking it. <c>null</c> if none showed in time. Looked for every 100 ms, for a few
    /// seconds at most, then nothing more runs.
    /// </summary>
    public static async Task<OpenWindow?> FrontAfterLaunchAsync(HashSet<nint> before, int? processId, string hint, CancellationToken cancellation, string? program = null)
    {
        var since = Stopwatch.StartNew();
        string wanted = FuzzyMatch.Fold(hint);
        while (since.Elapsed < LaunchWait)
        {
            await Task.Delay(100, cancellation);
            var now = List();
            var fresh = now.Where(window => !before.Contains(window.Handle)).ToList();
            var found = fresh.FirstOrDefault(window => processId is int id && window.ProcessId == id) ?? fresh.FirstOrDefault();
            if (found is null && since.Elapsed >= NewWindowFirst)
            {
                found = now.FirstOrDefault(window => wanted.Length > 0 && (FuzzyMatch.Fold(window.Title).Contains(wanted, StringComparison.Ordinal)
                        || FuzzyMatch.Fold(window.App).Contains(wanted, StringComparison.Ordinal)))
                    ?? now.FirstOrDefault(window => program is { Length: > 0 } && window.Program.Equals(program, StringComparison.OrdinalIgnoreCase));
            }

            if (found is not null)
            {
                BringToFront(found.Handle);
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="hwnd"/> to the front, restored if it was minimized, as a click on it would: Windows only lets the
    /// app in use give the foreground, so the request is made with the input queue of the window in front shared for a
    /// moment (as the notch takes the keyboard), else as Alt + Tab switches (no key pressed). <c>true</c> if it is in front.
    /// </summary>
    public static bool BringToFront(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.SetForegroundWindow(hwnd) && NativeMethods.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        uint target = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint self = NativeMethods.GetCurrentThreadId();
        if (target != 0 && target != self && NativeMethods.AttachThreadInput(self, target, true))
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.AttachThreadInput(self, target, false);
        }

        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            NativeMethods.SwitchToThisWindow(hwnd, true);
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// <paramref name="windows"/> asked to close, as their cross would (those still open, and still the same window: a
    /// handle given again to another is left alone). The ones still there a moment later are given back: their app
    /// asks something (save?), or said no.
    /// </summary>
    public static async Task<IReadOnlyList<OpenWindow>> CloseAsync(IReadOnlyList<OpenWindow> windows, CancellationToken cancellation)
    {
        var asked = new List<OpenWindow>();
        foreach (var window in windows)
        {
            if (Read(window.Handle) is { } now && now.ProcessId == window.ProcessId && NativeMethods.PostMessage(window.Handle, NativeMethods.WM_CLOSE, 0, 0))
            {
                asked.Add(window);
            }
        }

        var left = asked;
        var since = Stopwatch.StartNew();
        while (left.Count > 0 && since.Elapsed < Waited)
        {
            await Task.Delay(100, cancellation);
            left = left.Where(window => NativeMethods.IsWindow(window.Handle) && NativeMethods.IsWindowVisible(window.Handle)).ToList();
        }

        // Not asked at all (gone meanwhile, or above Ilot's rights: an app run as administrator) count as left too, if there.
        var refused = windows.Except(asked).Where(window => NativeMethods.IsWindow(window.Handle) && NativeMethods.IsWindowVisible(window.Handle));
        return [.. left, .. refused];
    }
}
