using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using Ilot.Localization;

namespace Ilot.Modules.Media.Audio;

public enum AudioDeviceKind
{
    Headphones,
    Headset,
    Speakers,

    /// <summary>A screen or TV over HDMI / DisplayPort.</summary>
    Display,
    Other,
}

/// <summary>Windows moved the sound from one output to another.</summary>
/// <param name="From">The output before, if known (and not virtual).</param>
/// <param name="To">The output now, if known (and not virtual).</param>
public sealed record AudioSwitch(AudioDevice? From, AudioDevice? To)
{
    /// <summary>An accessory just arrived (AirPods connected…).</summary>
    public bool IsConnection => To is { IsAccessory: true };

    /// <summary>An accessory just left (AirPods put away…): the sound fell back to another output.</summary>
    public bool IsDisconnection => !IsConnection && From is { IsAccessory: true };
}

/// <summary>An audio output that just became the one in use.</summary>
/// <param name="Name">"AirPods Pro", "Casque", "Haut-parleurs"…</param>
/// <param name="Connection">"Bluetooth", "USB", or <c>null</c> for one built in.</param>
public sealed record AudioDevice(string Name, AudioDeviceKind Kind, string? Connection)
{
    /// <summary>Plugged in or paired just now (headphones, a Bluetooth speaker…), rather than built in.</summary>
    public bool IsAccessory => Connection is not null || Kind is AudioDeviceKind.Headphones or AudioDeviceKind.Headset;

    /// <summary>Segoe Fluent Icons glyph: headphones, speakers or a screen.</summary>
    public string Glyph => Kind switch
    {
        AudioDeviceKind.Speakers => "",
        AudioDeviceKind.Display => "",
        _ => "",
    };
}

/// <summary>
/// Tells when Windows switches to another audio output (AirPods connecting, a headset plugged in…), through Core
/// Audio's device notifications (<c>IMMNotificationClient</c>, via NAudio): nothing is polled.
/// </summary>
/// <remarks>
/// Windows reports a switch once per role (console, multimedia, communications) and sometimes twice in a row: only
/// the multimedia one counts, once. The first seconds after launch (Windows settling its devices at logon) and
/// virtual devices (VB-Audio, Voicemeeter, Steam Streaming…) are ignored.
/// </remarks>
public sealed class AudioDeviceMonitor : IDisposable
{
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(2);

    // The endpoint's device node enumerator (DEVPKEY_Device_EnumeratorName): BTHENUM / BTHLE, USB, HDAUDIO…
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    private static readonly string[] Virtual =
    [
        "virtual", "vb-audio", "voicemeeter", "cable", "steam streaming", "nvidia broadcast", "obs", "sonar",
    ];

    private readonly DispatcherQueue _dispatcher;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private AudioDevice? _current;
    private MMDeviceEnumerator? _devices;
    private MMDeviceNotificationClient? _notifications;
    private string? _lastId;
    private long _lastAt;

    public AudioDeviceMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            _devices = new MMDeviceEnumerator();
            _current = DescribeDefault(); // Read once: then, events only.
            _notifications = _devices.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                // Called on a system thread: the rest happens on the UI thread.
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia && !string.IsNullOrEmpty(e.DeviceId))
                {
                    string id = e.DeviceId;
                    _dispatcher.TryEnqueue(() => OnDefaultChanged(id));
                }
            };
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Audio] Device notifications unavailable: {ex.Message}");
        }
    }

    /// <summary>Raised on the UI thread when Windows moves the sound to another output.</summary>
    public event EventHandler<AudioSwitch>? Switched;

    public void Dispose()
    {
        _notifications?.Dispose();
        _notifications = null;
        _devices?.Dispose();
        _devices = null;
    }

    private void OnDefaultChanged(string id)
    {
        bool settling = Stopwatch.GetElapsedTime(_startedAt) < Settling;
        bool repeated = id == _lastId && Stopwatch.GetElapsedTime(_lastAt) < Repeat;
        _lastId = id;
        _lastAt = Stopwatch.GetTimestamp();
        if (repeated)
        {
            return;
        }

        // Always kept up to date (even while settling), so a disconnection knows what left.
        var previous = _current;
        _current = Describe(id);
        if (!settling && (previous is not null || _current is not null))
        {
            Switched?.Invoke(this, new AudioSwitch(previous, _current));
        }
    }

    private AudioDevice? DescribeDefault()
    {
        try
        {
            using var endpoint = _devices!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Describe(endpoint.ID);
        }
        catch (COMException)
        {
            return null; // No output at all, for now.
        }
    }

    /// <summary>What the output is, from Windows' own description; <c>null</c> for a virtual one.</summary>
    private AudioDevice? Describe(string id)
    {
        try
        {
            using var endpoint = _devices!.GetDevice(id);
            string adapter = endpoint.DeviceFriendlyName; // "AirPods Pro", "Realtek(R) Audio"…
            string full = endpoint.FriendlyName;          // "Écouteurs (AirPods Pro)"…
            if (Virtual.Any(word => full.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var properties = endpoint.Properties;
            uint formFactor = properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor)
                && properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value is uint factor ? factor : 10;
            string bus = properties.Contains(EnumeratorName) ? properties[EnumeratorName].Value as string ?? "" : "";

            var kind = formFactor switch
            {
                1 => AudioDeviceKind.Speakers,
                3 => AudioDeviceKind.Headphones,
                5 or 6 => AudioDeviceKind.Headset,
                9 => AudioDeviceKind.Display,
                _ => AudioDeviceKind.Other,
            };
            string? connection = bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ? "Bluetooth"
                : bus.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? "USB"
                : null;

            // A Bluetooth or USB device is known by its own name; a built-in output by what it is.
            string name = connection is not null && adapter.Length > 0 ? adapter
                : kind switch
                {
                    AudioDeviceKind.Headphones => Loc.T("Écouteurs"),
                    AudioDeviceKind.Headset => Loc.T("Casque"),
                    AudioDeviceKind.Speakers => Loc.T("Haut-parleurs"),
                    AudioDeviceKind.Display => adapter.Length > 0 ? adapter : Loc.T("Écran"),
                    _ => full,
                };
            return new AudioDevice(name, kind, connection);
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Audio] Device not described: {ex.Message}");
            return null;
        }
    }
}
