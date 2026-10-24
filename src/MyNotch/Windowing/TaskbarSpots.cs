using System.Diagnostics;
using System.Runtime.InteropServices;
using MyNotch.Interop;
using Windows.Graphics;

namespace MyNotch.Windowing;

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
    /// The main taskbar (in the screen's pixels), its widest free stretch (none: no room) and all of them, from left to
    /// right (the last ones run up to the tray's icons).
    /// </summary>
    public sealed record Taskbar(RectInt32 Bounds, (int Left, int Right)? Free, IReadOnlyList<(int Left, int Right)> Gaps);

    /// <summary>The main taskbar as it is now; <c>null</c> when there is none to sit on (hidden, auto-hiding).</summary>
    public static Task<Taskbar?> FindAsync() => Task.Run(Find);

    private static Taskbar? Find()
    {
        nint tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == 0 || !NativeMethods.IsWindowVisible(tray) || NativeMethods.TaskbarAutoHides()
            || !NativeMethods.GetWindowRect(tray, out var rect) || rect.Right <= rect.Left)
        {
            return null;
        }

        var bounds = new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
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
            return new Taskbar(bounds, null, []); // Not knowing what it shows, nothing is put on it.
        }
        finally
        {
            if (automation is not null)
            {
                Marshal.ReleaseComObject(automation);
            }
        }

        var gaps = Gaps(rect.Left, rect.Right, taken);
        return new Taskbar(bounds, Widest(gaps), gaps);
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
