using System.Runtime.InteropServices;
using Ilot.Interop;

namespace Ilot.Quick;

/// <summary>
/// The built-in screen's brightness (a laptop's): the active power plan's « display brightness » for the current power
/// source, the very setting Windows' keys and slider move, read and written through powrprof (no WMI, no package).
/// Without a battery there is no built-in screen, and no brightness here.
/// </summary>
public sealed class BrightnessService
{
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid BrightnessSetting = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    private readonly Lock _gate = new();
    private double? _pending;
    private bool _writing;

    /// <summary>A battery, hence a built-in screen whose brightness Windows sets.</summary>
    public bool IsAvailable { get; } = NativeMethods.GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

    /// <summary>The brightness now, 0 to 1; <c>null</c> if it cannot be read.</summary>
    public double? Read()
    {
        if (!IsAvailable || ActiveScheme() is not { } scheme)
        {
            return null;
        }

        var subgroup = VideoSubgroup;
        var setting = BrightnessSetting;
        uint result = OnBattery()
            ? NativeMethods.PowerReadDCValueIndex(0, ref scheme, ref subgroup, ref setting, out uint value)
            : NativeMethods.PowerReadACValueIndex(0, ref scheme, ref subgroup, ref setting, out value);
        return result == 0 ? Math.Clamp(value / 100.0, 0, 1) : null;
    }

    /// <summary>
    /// Sets the brightness (0 to 1), off the UI thread: a drag asks for many in a row, only the last one waiting is
    /// written.
    /// </summary>
    public void Set(double level)
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_gate)
        {
            _pending = Math.Clamp(level, 0, 1);
            if (_writing)
            {
                return;
            }

            _writing = true;
        }

        Task.Run(WriteWaiting);
    }

    private void WriteWaiting()
    {
        while (true)
        {
            double level;
            lock (_gate)
            {
                if (_pending is not { } waiting)
                {
                    _writing = false;
                    return;
                }

                level = waiting;
                _pending = null;
            }

            Write(level);
        }
    }

    /// <summary>The setting of the plan in use, for the power source in use; then the plan applied again, which shows it.</summary>
    private static void Write(double level)
    {
        if (ActiveScheme() is not { } scheme)
        {
            return;
        }

        var subgroup = VideoSubgroup;
        var setting = BrightnessSetting;
        uint value = (uint)Math.Round(level * 100);
        if (OnBattery())
        {
            NativeMethods.PowerWriteDCValueIndex(0, ref scheme, ref subgroup, ref setting, value);
        }
        else
        {
            NativeMethods.PowerWriteACValueIndex(0, ref scheme, ref subgroup, ref setting, value);
        }

        NativeMethods.PowerSetActiveScheme(0, ref scheme);
    }

    private static Guid? ActiveScheme()
    {
        if (NativeMethods.PowerGetActiveScheme(0, out nint pointer) != 0 || pointer == 0)
        {
            return null;
        }

        var scheme = Marshal.PtrToStructure<Guid>(pointer);
        NativeMethods.LocalFree(pointer);
        return scheme;
    }

    private static bool OnBattery() => NativeMethods.GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
}
