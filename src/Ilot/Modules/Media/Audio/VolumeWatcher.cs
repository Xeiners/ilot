using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;

namespace Ilot.Modules.Media.Audio;

/// <summary>
/// The system's master volume as it changes, however it is changed (the keyboard's keys, the taskbar's slider, an app):
/// Core Audio tells (the default output's <c>IAudioEndpointVolume</c> notifications, via NAudio), and the default output
/// is followed as Windows switches it. Nothing is polled.
/// </summary>
public sealed class VolumeWatcher : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private MMDeviceEnumerator? _devices;
    private MMDeviceNotificationClient? _notifications;
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;
    private VolumeState _last;

    public VolumeWatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            _devices = new MMDeviceEnumerator();
            Watch();
            _notifications = _devices.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                // Called on a system thread: the device is taken again on the UI thread.
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
                {
                    _dispatcher.TryEnqueue(Watch);
                }
            };
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Volume] Not watched: {ex.Message}");
        }
    }

    /// <summary>Raised on the UI thread when the level or the mute changes.</summary>
    public event EventHandler<VolumeState>? Changed;

    public void Dispose()
    {
        Unwatch();
        _notifications?.Dispose();
        _notifications = null;
        _devices?.Dispose();
        _devices = null;
    }

    /// <summary>Listens to the default output's volume (and no longer to the previous one's).</summary>
    private void Watch()
    {
        Unwatch();
        if (_devices is null)
        {
            return;
        }

        try
        {
            _device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _volume = _device.AudioEndpointVolume;
            _last = new VolumeState(_volume.MasterVolumeLevelScalar, _volume.Mute, true);
            _volume.OnVolumeNotification += OnNotification;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Volume] No output device: {ex.Message}");
            Unwatch();
        }
    }

    private void Unwatch()
    {
        if (_volume is not null)
        {
            _volume.OnVolumeNotification -= OnNotification;
            _volume = null;
        }

        _device?.Dispose();
        _device = null;
    }

    /// <summary>Called on a system thread: on to the UI thread, for a real change only (not one channel's balance alone).</summary>
    private void OnNotification(AudioVolumeNotificationData data)
    {
        var state = new VolumeState(data.MasterVolume, data.Muted, true);
        _dispatcher.TryEnqueue(() =>
        {
            if (Math.Abs(state.Level - _last.Level) < 0.001 && state.IsMuted == _last.IsMuted)
            {
                return;
            }

            _last = state;
            Changed?.Invoke(this, state);
        });
    }
}
