using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MyNotch.Modules.Media.Audio;

/// <summary>
/// The master volume through Core Audio (<c>IAudioEndpointVolume</c> of the default output device, via NAudio).
/// The device is looked up at each call: it follows headphones plugged in or the output switched in Windows, without
/// listening for any of it.
/// </summary>
public sealed class AudioService : IAudioService, IDisposable
{
    private const double StepSize = 0.05;

    private readonly MMDeviceEnumerator _devices = new();

    public VolumeState Read() => Use(volume => State(volume));

    public VolumeState Step(int direction) => Use(volume =>
    {
        // Land on multiples of 5 %, like Windows' own keys: 37 % then up gives 40 %, not 42 %.
        double level = Math.Round(volume.MasterVolumeLevelScalar / StepSize) * StepSize + Math.Sign(direction) * StepSize;
        volume.MasterVolumeLevelScalar = (float)Math.Clamp(level, 0, 1);
        if (direction > 0 && volume.Mute)
        {
            volume.Mute = false;
        }

        return State(volume);
    });

    public VolumeState SetLevel(double level) => Use(volume =>
    {
        volume.MasterVolumeLevelScalar = (float)Math.Clamp(level, 0, 1);
        if (level > 0 && volume.Mute)
        {
            volume.Mute = false;
        }

        return State(volume);
    });

    public VolumeState ToggleMute() => Use(volume =>
    {
        volume.Mute = !volume.Mute;
        return State(volume);
    });

    public void Dispose() => _devices.Dispose();

    private static VolumeState State(AudioEndpointVolume volume) => new(volume.MasterVolumeLevelScalar, volume.Mute, true);

    /// <summary>The default output device's volume for one operation; none (no device, device gone) is no volume.</summary>
    private VolumeState Use(Func<AudioEndpointVolume, VolumeState> operation)
    {
        try
        {
            using var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return operation(device.AudioEndpointVolume);
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Volume] No output device: {ex.Message}");
            return VolumeState.Unavailable;
        }
    }
}
