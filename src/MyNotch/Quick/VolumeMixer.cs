using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MyNotch.Interop;
using MyNotch.Localization;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MyNotch.Quick;

/// <summary>
/// An app in the volume mixer: its sound sessions on the default output (a browser has several: they go together), its
/// name and its program (for its icon), and its volume and mute, set on all of them.
/// </summary>
public sealed class MixerApp
{
    private readonly List<AudioSessionControl> _sessions;

    internal MixerApp(string name, string? program, bool system, List<AudioSessionControl> sessions)
    {
        Name = name;
        Program = program;
        IsSystem = system;
        _sessions = sessions;
    }

    public string Name { get; }

    /// <summary>The program's path (its icon); <c>null</c> for the system's sounds, or one that could not be read.</summary>
    public string? Program { get; }

    /// <summary>The system's sounds (notifications…), not an app's.</summary>
    public bool IsSystem { get; }

    /// <summary>Playing now (one of its sessions, at least).</summary>
    public bool IsPlaying => _sessions.Any(session => Safe(() => session.State == AudioSessionState.AudioSessionStateActive));

    /// <summary>Its volume (0 to 1): the loudest of its sessions, and the same for all when set.</summary>
    public double Volume
    {
        get => _sessions.Max(session => Safe(() => session.SimpleAudioVolume.Volume));
        set
        {
            foreach (var session in _sessions)
            {
                Safe(() => session.SimpleAudioVolume.Volume = (float)Math.Clamp(value, 0, 1));
            }
        }
    }

    public bool IsMuted
    {
        get => _sessions.All(session => Safe(() => session.SimpleAudioVolume.Mute));
        set
        {
            foreach (var session in _sessions)
            {
                Safe(() => session.SimpleAudioVolume.Mute = value);
            }
        }
    }

    internal void Release()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }
    }

    /// <summary>A session that went away meanwhile (the app quit) answers with an error: it is left alone.</summary>
    private static T Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return default!;
        }
    }

    private static void Safe(Action write)
    {
        try
        {
            write();
        }
        catch (COMException)
        {
        }
    }
}

/// <summary>
/// The apps making sound on the default output, as Windows' volume mixer has them: read when the mixer opens (nothing is
/// followed meanwhile), let go of when it closes.
/// </summary>
public sealed class VolumeMixer : IDisposable
{
    private MMDeviceEnumerator? _devices;
    private MMDevice? _device;
    private List<MixerApp> _apps = [];

    /// <summary>The apps now: the system's sounds first, then those playing, then the others, by name.</summary>
    public IReadOnlyList<MixerApp> Read()
    {
        Release();
        try
        {
            _devices = new MMDeviceEnumerator();
            _device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            int self = Environment.ProcessId;
            var groups = new Dictionary<string, (string Name, string? Program, bool System, List<AudioSessionControl> Sessions)>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired || (int)session.GetProcessID == self)
                {
                    session.Dispose();
                    continue;
                }

                var (key, name, program, system) = Describe(session);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = (name, program, system, []);
                    groups[key] = group;
                }

                group.Sessions.Add(session);
            }

            _apps = [.. groups.Values.Select(group => new MixerApp(group.Name, group.Program, group.System, group.Sessions))];
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Mixer] Not read: {ex.Message}");
        }

        return [.. _apps
            .OrderByDescending(app => app.IsSystem)
            .ThenByDescending(app => app.IsPlaying)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The mixer closed: its sessions and the output are let go of.</summary>
    public void Release()
    {
        foreach (var app in _apps)
        {
            app.Release();
        }

        _apps = [];
        _device?.Dispose();
        _device = null;
        _devices?.Dispose();
        _devices = null;
    }

    public void Dispose() => Release();

    /// <summary>A session's app: what groups it (its program), its name, its program's path, whether it is the system's.</summary>
    private static (string Key, string Name, string? Program, bool System) Describe(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession)
        {
            return ("system", Loc.T("Sons système"), null, true);
        }

        int id = (int)session.GetProcessID;
        string? program = NativeMethods.ProcessPath(id); // Its path only: nothing of the process is read.
        string name = program is not null ? Path.GetFileNameWithoutExtension(program) : "";
        if (name.Length == 0)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Gone meanwhile.
            }
        }

        // Its own name for the session, if it gives one ("@…" is a resource reference, not a name), else its program's.
        string given = session.DisplayName ?? "";
        if (given.Length > 0 && !given.StartsWith('@'))
        {
            name = given;
        }
        else if (program is not null && FileVersionInfo.GetVersionInfo(program).FileDescription is { Length: > 0 } description)
        {
            name = Tidy(description);
        }
        else if (name.Length > 0)
        {
            name = Tidy(name);
        }
        else
        {
            name = Loc.T("Application");
        }

        return (program?.ToLowerInvariant() ?? $"process:{id}", name, program, false);
    }

    // What a program's name says about its build, not about it ("VALORANT-Win64-Shipping").
    private static readonly string[] BuildWords = ["win64", "win32", "x64", "x86", "shipping", "release", "root"];

    /// <summary>
    /// A program's name, readable: "VALORANT-Win64-Shipping" is "VALORANT", "WhatsApp.Root" is "WhatsApp", "spotify" is
    /// "Spotify".
    /// </summary>
    internal static string Tidy(string name)
    {
        if (name.Contains(' '))
        {
            return name; // A real name already ("Google Chrome").
        }

        var words = name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !BuildWords.Contains(word.ToLowerInvariant()))
            .ToList();
        string tidy = words.Count > 0 ? string.Join(' ', words) : name;
        return char.ToUpper(tidy[0]) + tidy[1..];
    }
}
