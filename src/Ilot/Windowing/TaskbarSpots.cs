using System.Diagnostics;
using System.Runtime.InteropServices;
using Ilot.Interop;
using Windows.Graphics;

namespace Ilot.Windowing;

/// <summary>
/// Where the taskbar has room: what its buttons take (Start, search, the apps, the tray and the clock: read with UI
/// Automation, the taskbar of Windows 11 being XAML, its icons no windows), and the stretches left between them.
/// Read only, off the UI thread (a taskbar that does not answer never holds the notch).
/// </summary>
internal static class TaskbarSpots
{
    private const int Descendants = 4; // TreeScope_Descendants: every element under the taskbar.
    private const int Button = 50000; // UIA_ButtonControlTypeId.

    /// <summary>
    /// A taskbar (in the screen's pixels), its widest free stretch (none: no room) and all of them, from left to right (the
    /// last ones run up to the tray's icons, or the clock); and its whole screen.
    /// </summary>
    public sealed record Taskbar(RectInt32 Bounds, (int Left, int Right)? Free, IReadOnlyList<(int Left, int Right)> Gaps, RectInt32 Screen = default);

    /// <summary>
    /// The taskbar of <paramref name="screen"/> (a device name, see <see cref="NativeMethods.MonitorName"/>; empty: the main
    /// screen's) as it is now: the main one when that screen has none or is gone; <c>null</c> when there is none to sit on
    /// (hidden, auto-hiding).
    /// </summary>
    public static Task<Taskbar?> FindAsync(string? screen = null) => Task.Run(() => Find(screen));

    /// <summary>Whether <paramref name="monitor"/> shows a taskbar (Windows may show it on the main screen only).</summary>
    public static bool HasTaskbar(nint monitor) => All().Any(tray => NativeMethods.MonitorFromWindow(tray, NativeMethods.MONITOR_DEFAULTTONEAREST) == monitor);

    /// <summary>Every visible taskbar: the main one, then the other screens' ones.</summary>
    private static List<nint> All()
    {
        var trays = new List<nint>();
        nint main = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (main != 0 && NativeMethods.IsWindowVisible(main))
        {
            trays.Add(main);
        }

        for (nint other = NativeMethods.FindWindowEx(0, 0, "Shell_SecondaryTrayWnd", null);
             other != 0 && trays.Count < 16;
             other = NativeMethods.FindWindowEx(0, other, "Shell_SecondaryTrayWnd", null))
        {
            if (NativeMethods.IsWindowVisible(other))
            {
                trays.Add(other);
            }
        }

        return trays;
    }

    private static Taskbar? Find(string? screen)
    {
        var trays = All();
        nint tray = (string.IsNullOrEmpty(screen) ? 0
                : trays.FirstOrDefault(each => NativeMethods.MonitorName(NativeMethods.MonitorFromWindow(each, NativeMethods.MONITOR_DEFAULTTONEAREST)) == screen))
            is not 0 and var found ? found : trays.FirstOrDefault();
        if (tray == 0 || NativeMethods.TaskbarAutoHides()
            || !NativeMethods.GetWindowRect(tray, out var rect) || rect.Right <= rect.Left)
        {
            return null;
        }

        var bounds = new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var whole = NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(tray, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)
            ? new RectInt32(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top)
            : bounds;
        var taken = new List<(int Left, int Right)>();
        IUIAutomation? automation = null;
        try
        {
            automation = (IUIAutomation)new CUIAutomation();
            var all = automation.ElementFromHandle(tray).FindAll(Descendants, automation.CreateTrueCondition());
            for (int i = 0, count = all.GetLength(); i < count; i++)
            {
                var element = all.GetElement(i);
                if (element.CurrentControlType() == Button && element.CurrentIsOffscreen() == 0)
                {
                    var box = element.CurrentBoundingRectangle();
                    if (box.Right > box.Left && box.Bottom > rect.Top && box.Top < rect.Bottom)
                    {
                        taken.Add((box.Left, box.Right));
                    }
                }

                Marshal.ReleaseComObject(element);
            }

            Marshal.ReleaseComObject(all);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"[Taskbar] Not read: {ex.Message}");
            return new Taskbar(bounds, null, [], whole); // Not knowing what it shows, nothing is put on it.
        }
        finally
        {
            if (automation is not null)
            {
                Marshal.ReleaseComObject(automation);
            }
        }

        var gaps = Gaps(rect.Left, rect.Right, taken);
        return new Taskbar(bounds, Widest(gaps), gaps, whole);
    }

    /// <summary>
    /// The widest stretch between <paramref name="left"/> and <paramref name="right"/> that none of <paramref name="taken"/>
    /// covers (what overlaps or touches is merged); <c>null</c> when all is taken.
    /// </summary>
    public static (int Left, int Right)? Widest(int left, int right, IEnumerable<(int Left, int Right)> taken) =>
        Widest(Gaps(left, right, taken));

    /// <summary>The widest of <paramref name="gaps"/> (the first of equals); <c>null</c> when there is none.</summary>
    private static (int Left, int Right)? Widest(IReadOnlyList<(int Left, int Right)> gaps) =>
        gaps.Count == 0 ? null : gaps.MaxBy(gap => gap.Right - gap.Left);

    /// <summary>
    /// The stretches between <paramref name="left"/> and <paramref name="right"/> that none of <paramref name="taken"/>
    /// covers (what overlaps or touches is merged), from left to right.
    /// </summary>
    public static List<(int Left, int Right)> Gaps(int left, int right, IEnumerable<(int Left, int Right)> taken)
    {
        var gaps = new List<(int Left, int Right)>();
        int from = left;
        foreach (var (start, end) in taken.Where(each => each.Right > left && each.Left < right).OrderBy(each => each.Left))
        {
            Add(from, Math.Min(start, right));
            from = Math.Max(from, end);
        }

        Add(from, right);
        return gaps;

        void Add(int start, int end)
        {
            if (end > start)
            {
                gaps.Add((start, end));
            }
        }
    }
}
