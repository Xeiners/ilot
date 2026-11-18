using System.Runtime;
using Microsoft.UI.Dispatching;
using Ilot.Interop;

namespace Ilot.Windowing;

/// <summary>
/// Gives back what the notch no longer uses, once it has been left alone for a while (closed, its other windows closed):
/// what opening it, a course's import, the settings… allocated and let go is collected (the large objects compacted too,
/// so the heap does not stay as big as it once had to be), then the pages not touched since leave its working set. They
/// stay in Windows' standby list, read back at once if the notch needs them again (nothing is lost; a first opening after
/// may take a few milliseconds more), and are free for the others meanwhile (a game). Once per quiet moment, never
/// repeated: nothing runs at rest.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>How long the notch is left alone before: long enough not to come during a use.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    private static readonly uint OwnProcess = (uint)Environment.ProcessId;
    private static DispatcherQueueTimer? _timer;
    private static volatile bool _trimming;

    /// <summary>Something that used memory just ended (the notch closed, a window too): trimmed after a quiet moment.</summary>
    public static void Schedule()
    {
        if (_timer is null)
        {
            if (DispatcherQueue.GetForCurrentThread() is not { } dispatcher)
            {
                return; // Off the UI thread: not here.
            }

            _timer = dispatcher.CreateTimer();
            _timer.Interval = Quiet;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Trim();
        }

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>The notch is in use again: not now.</summary>
    public static void Cancel() => _timer?.Stop();

    private static void Trim()
    {
        // One of its windows in front (the settings, a sheet set aside): in use, later.
        if (NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint process) != 0 && process == OwnProcess)
        {
            Schedule();
            return;
        }

        if (_trimming)
        {
            return;
        }

        // Off the UI thread: waiting for the finalizers there could wait for itself (a WinRT object let go is released on
        // the thread it was made on). The collection pauses the notch a few milliseconds, the closed one only.
        _trimming = true;
        _ = Task.Run(() =>
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers(); // What they let go (images, views) goes with this second pass.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
            }
            finally
            {
                _trimming = false;
            }
        });
    }
}
