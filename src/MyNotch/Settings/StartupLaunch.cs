using System.Diagnostics;
using Microsoft.Win32;

namespace MyNotch.Settings;

/// <summary>
/// Starting with Windows: the notch's entry in the user's "Run" key (<c>HKCU\…\CurrentVersion\Run</c>), the place
/// Windows reads at sign-in, and the Task Manager's "Startup apps" list shows. No rights needed. The installer writes the
/// same entry (its "Lancer avec Windows" option), so both always agree.
/// </summary>
public static class StartupLaunch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MyNotch";

    /// <summary>This copy of the notch: the program Windows would start.</summary>
    public static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MyNotch.exe");

    /// <summary>Whether the notch starts with Windows (from wherever it was registered).</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                return run?.GetValue(Name) is string { Length: > 0 };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Debug.WriteLine($"[Startup] Not read: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Starts this copy with Windows, or stops starting the notch at all. <c>false</c> if Windows refused.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                run.SetValue(Name, $"\"{Executable}\"");
            }
            else
            {
                run.DeleteValue(Name, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"[Startup] Not changed: {ex.Message}");
            return false;
        }
    }
}
