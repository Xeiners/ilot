using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MyNotch.Modules.Media.Audio;

/// <summary>
/// Listens to the default playback device in loopback (exactly what the speakers play, whichever app it comes
/// from) and analyses it into frequency bands on the capture thread.
/// </summary>
/// <remarks>
/// WASAPI delivers nothing while the device is idle, so silence simply reads as "no fresh levels". The capture
/// follows default device changes (headphones plugged in…) and recovers when its device disappears. Starting and
/// stopping happen off the UI thread.
/// </remarks>
public sealed class AudioSpectrumService : IAudioSpectrumService, IDisposable
{
    private const int Bands = 24;
    private static readonly TimeSpan Staleness = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(600);

    private readonly object _lifecycle = new();
    private readonly object _levelsLock = new();
    private readonly float[] _published = new float[Bands];
    private readonly float[] _analysed = new float[Bands];
    private float[] _mono = new float[4800];
    private long _publishedAt;
    private bool _enabled;
    private bool _disposed;
    private volatile bool _listening;
    private volatile SpectrumAnalyzer? _analyzer;
    private volatile string? _deviceId;
    private WasapiRecorder? _recorder;
    private SampleFormat _format;
    private int _channels;
    private MMDeviceEnumerator? _devices;
    private MMDeviceNotificationClient? _deviceNotifications;

    private enum SampleFormat
    {
        Unsupported,
        Float32,
        Pcm16,
    }

    public int BandCount => Bands;

    public bool IsListening => _listening;

    public void SetEnabled(bool enabled)
    {
        lock (_lifecycle)
        {
            if (_disposed || enabled == _enabled)
            {
                return;
            }

            _enabled = enabled;
        }

        Reconcile(restart: false);
    }

    public bool TryGetLevels(Span<float> levels)
    {
        lock (_levelsLock)
        {
            if (_publishedAt == 0 || Stopwatch.GetElapsedTime(_publishedAt) > Staleness)
            {
                return false;
            }

            _published.AsSpan(0, Math.Min(levels.Length, Bands)).CopyTo(levels);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            StopCore();
            _deviceNotifications?.Dispose();
            _devices?.Dispose();
        }
    }

    /// <summary>Brings the capture in line with the requested state, on the thread pool.</summary>
    private void Reconcile(bool restart) => Task.Run(() =>
    {
        lock (_lifecycle)
        {
            if (restart || !_enabled || _disposed)
            {
                StopCore();
            }

            if (_enabled && !_disposed)
            {
                WatchDevices();
                StartCore();
            }
        }
    });

    private void StartCore()
    {
        if (_recorder is not null)
        {
            return;
        }

        WasapiRecorder? recorder = null;
        try
        {
            recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
            _format = Classify(recorder.WaveFormat);
            if (_format == SampleFormat.Unsupported)
            {
                Debug.WriteLine($"[Audio] Unsupported mix format: {recorder.WaveFormat}");
                recorder.Dispose();
                return;
            }

            _channels = recorder.WaveFormat.Channels;
            _analyzer = new SpectrumAnalyzer(recorder.WaveFormat.SampleRate, Bands);
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;
            recorder.StartRecording();
            _recorder = recorder;
            _deviceId = recorder.DeviceId;
            _listening = true;
        }
        catch (Exception ex)
        {
            // No playback device, or it is held in exclusive mode: the visualizer falls back to its idle motion.
            Debug.WriteLine($"[Audio] Loopback capture unavailable: {ex.Message}");
            recorder?.Dispose();
            _analyzer = null;
        }
    }

    private void StopCore()
    {
        _listening = false;
        _analyzer = null;
        if (_recorder is not { } recorder)
        {
            return;
        }

        _recorder = null;
        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        try
        {
            recorder.StopRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Stop failed: {ex.Message}");
        }

        recorder.Dispose();
        lock (_levelsLock)
        {
            _publishedAt = 0;
        }
    }

    /// <summary>Restarts the capture on the new default playback device (headphones plugged in, etc.).</summary>
    private void WatchDevices()
    {
        if (_deviceNotifications is not null)
        {
            return;
        }

        try
        {
            _devices = new MMDeviceEnumerator();
            _deviceNotifications = _devices.CreateNotificationClient(useSynchronizationContext: false);
            _deviceNotifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role != Role.Communications && e.DeviceId != _deviceId)
                {
                    Reconcile(restart: true);
                }
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Device notifications unavailable: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        // The device went away (unplugged, driver reset…): try again on whatever is the default now.
        Debug.WriteLine($"[Audio] Capture lost: {e.Exception.Message}");
        _listening = false;
        _ = Task.Delay(RecoveryDelay).ContinueWith(_ => Reconcile(restart: true), TaskScheduler.Default);
    }

    /// <summary>Capture thread: downmix to mono and feed the analyzer.</summary>
    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var analyzer = _analyzer;
        if (analyzer is null || buffer.IsEmpty)
        {
            return;
        }

        int bytesPerSample = _format == SampleFormat.Float32 ? 4 : 2;
        int frames = buffer.Length / (bytesPerSample * _channels);
        if (_mono.Length < frames)
        {
            _mono = new float[frames];
        }

        var mono = _mono.AsSpan(0, frames);
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            mono.Clear();
        }
        else
        {
            Downmix(buffer, mono);
        }

        if (analyzer.Push(mono, _analysed))
        {
            lock (_levelsLock)
            {
                _analysed.CopyTo(_published, 0);
                _publishedAt = Stopwatch.GetTimestamp();
            }
        }
    }

    private void Downmix(ReadOnlySpan<byte> buffer, Span<float> mono)
    {
        int channels = _channels;
        float scale = 1f / channels;
        if (_format == SampleFormat.Float32)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (int frame = 0, i = 0; frame < mono.Length; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += samples[i++];
                }

                mono[frame] = sum * scale;
            }
        }
        else
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            for (int frame = 0, i = 0; frame < mono.Length; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += samples[i++];
                }

                mono[frame] = sum * scale / 32768f;
            }
        }
    }

    private static SampleFormat Classify(WaveFormat format)
    {
        var subFormat = (format as WaveFormatExtensible)?.SubFormat;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || subFormat == AudioSubtypes.MFAudioFormat_Float
            || (format.Encoding == WaveFormatEncoding.Extensible && subFormat is null && format.BitsPerSample == 32);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm || subFormat == AudioSubtypes.MFAudioFormat_PCM;

        return isFloat && format.BitsPerSample == 32 ? SampleFormat.Float32
             : isPcm && format.BitsPerSample == 16 ? SampleFormat.Pcm16
             : SampleFormat.Unsupported;
    }
}
