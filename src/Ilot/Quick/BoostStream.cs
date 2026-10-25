using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ilot.Quick;

/// <summary>
/// One app's sound made louder (see <see cref="SoundBooster"/>): what its process plays (its children too) is captured as
/// Windows mixes it, that is after its own volume, which the booster sets to <see cref="Floor"/> meanwhile (60 dB down,
/// losslessly: the engine mixes in floats); it is brought up to the level asked, kept under full scale by a limiter, and
/// played again on the default output, in Ilot's own session. Silent until <see cref="Settle"/> says its sessions are down;
/// silent again, at once, when what comes is louder than they could be (one turned up behind the booster's back: it is
/// <see cref="Tripped"/>). About 30 ms later than the app; nothing runs when it is not there.
/// </summary>
internal sealed class BoostStream : IWaveProvider, IDisposable
{
    /// <summary>The volume of the sessions it plays for, meanwhile: 60 dB down, whatever is left is not heard.</summary>
    public const float Floor = 0.001f;

    // What it plays never goes past this (-0.3 dBFS); what it captures is never louder than this, sessions down.
    private const float Ceiling = 0.97f;
    private const float Guard = Floor * 2;

    // Its sessions go down a few milliseconds after being told: what was captured before is not played.
    private static readonly long SettleTicks = TimeSpan.FromMilliseconds(60).Ticks;

    private const int Channels = 2;

    private readonly object _gate = new();
    private readonly float _ramp;
    private readonly float _release;
    private readonly float[] _ring;
    private readonly int _cushion;
    private readonly int _ahead;
    private float[] _scratch = [];
    private int _read;
    private int _count;
    private bool _priming = true;
    private int _starved;
    private int _dropped;

    private volatile float _target;
    private float _gain;
    private float _envelope;
    private long _settledFrom = long.MaxValue;
    private long _trippedAt;

    private WasapiPlayer? _player;
    private WasapiRecorder? _capture;

    /// <summary>A stream that is not playing (its sound worked out alone: a test's).</summary>
    internal BoostStream(int rate, double level)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, Channels);
        _ramp = 1 - MathF.Exp(-1f / (0.015f * rate));      // A change of level: 15 ms.
        _release = MathF.Exp(-1f / (0.2f * rate));         // The limiter lets go in 200 ms.
        _ring = new float[rate / 4 * Channels];            // A quarter of a second, at most.
        _cushion = rate / 400 * Channels;                  // 2.5 ms ahead, against the capture's jitter;
        _ahead = rate / 25 * Channels;                     // 40 ms at most (a capture running faster than the output).
        Level = level;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>How many times the player asked before the capture had given enough (a short silence each time).</summary>
    internal int Starved => Volatile.Read(ref _starved);

    /// <summary>How many times the capture had run too far ahead (what waited past 40 ms was dropped).</summary>
    internal int Dropped => Volatile.Read(ref _dropped);

    /// <summary>The process (and its children) it plays for.</summary>
    public int ProcessId { get; private init; }

    /// <summary>How loud, 1 being the app's own level (up to <see cref="SoundBooster.Most"/>).</summary>
    public double Level
    {
        get => _target * Floor;
        set => _target = (float)(Math.Clamp(value, 0, SoundBooster.Most) / Floor);
    }

    /// <summary>
    /// What came was louder than its sessions, down, could give: one of them is up again (Windows' mixer, the app). It
    /// played nothing of it; the booster looks (at most every 250 ms).
    /// </summary>
    public event EventHandler? Tripped;

    /// <summary>
    /// The capture of <paramref name="processId"/>, played again at <paramref name="level"/> once settled; <c>null</c>
    /// when Windows does not give it (before Windows 11, or the process gone).
    /// </summary>
    public static async Task<BoostStream?> StartAsync(int processId, double level)
    {
        WasapiPlayer? player = null;
        try
        {
            player = await new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(10).WithLowLatency().BuildAsync();
            var stream = new BoostStream(player.DeviceMixFormat.SampleRate, level) { ProcessId = processId, _player = player };
            player = null;
            try
            {
                await stream.OpenAsync();
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            player?.Dispose();
            ErrorLog.Write($"[Booster] Process {processId} not captured: {ex.Message}");
            return null;
        }
    }

    private async Task OpenAsync()
    {
        _capture = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat)
            .BuildAsync();
        _capture.DataAvailable += OnCaptured;

        // Ilot's session is not lowered when a call starts (Windows' « reduce other sounds »): it is the call, louder.
        using (var devices = new MMDeviceEnumerator())
        using (var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            device.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
        }

        _player!.Init(this);
        _capture.StartRecording();
        _player.Play();
    }

    /// <summary>Its sessions are down now: what is captured from a little later on is played.</summary>
    public void Settle() => Volatile.Write(ref _settledFrom, Now + SettleTicks);

    /// <summary>The captured sound's clock (the performance counter, in 100 ns).</summary>
    private static long Now => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    private void OnCaptured(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var input = MemoryMarshal.Cast<byte, float>(buffer);
        if (_scratch.Length < input.Length)
        {
            _scratch = new float[input.Length];
        }

        var output = _scratch.AsSpan(0, input.Length);
        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
        if (silent)
        {
            output.Clear(); // Nothing playing: the level stays where it is, for when it plays again.
        }
        else if (!Process(input, output, open: qpcPosition >= Volatile.Read(ref _settledFrom)))
        {
            long now = Environment.TickCount64;
            if (now - _trippedAt >= 250)
            {
                _trippedAt = now;
                Tripped?.Invoke(this, EventArgs.Empty);
            }
        }

        Push(output);
    }

    /// <summary>
    /// <paramref name="input"/> (stereo), made louder into <paramref name="output"/> — or silence when not
    /// <paramref name="open"/>, or when it is louder than sessions down could be (then <c>false</c>, and silence until the
    /// level has come back up from nothing). The level glides; the limiter holds peaks under <see cref="Ceiling"/>.
    /// </summary>
    internal bool Process(ReadOnlySpan<float> input, Span<float> output, bool open)
    {
        float peak = 0;
        foreach (float sample in input)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        if (peak > Guard)
        {
            _gain = 0;
            output.Clear();
            return false;
        }

        float target = open ? _target : 0;
        for (int i = 0; i + 1 < input.Length; i += Channels)
        {
            _gain += (target - _gain) * _ramp;
            float left = input[i] * _gain;
            float right = input[i + 1] * _gain;
            float level = MathF.Max(MathF.Abs(left), MathF.Abs(right));
            _envelope = MathF.Max(level, _envelope * _release);
            float limit = _envelope > Ceiling ? Ceiling / _envelope : 1;
            output[i] = Math.Clamp(left * limit, -Ceiling, Ceiling);
            output[i + 1] = Math.Clamp(right * limit, -Ceiling, Ceiling);
        }

        return true;
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            foreach (float sample in samples)
            {
                if (_count == _ring.Length)
                {
                    _read = (_read + 1) % _ring.Length; // A quarter of a second behind: the oldest goes.
                    _count--;
                }

                _ring[(_read + _count) % _ring.Length] = sample;
                _count++;
            }
        }
    }

    /// <summary>
    /// The player asks for what comes next. It is kept a little ahead (<see cref="_cushion"/>) without falling behind: what
    /// waits past that is dropped, so the delay stays short; short of it, silence until it is there again.
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        var output = MemoryMarshal.Cast<byte, float>(buffer);
        lock (_gate)
        {
            int extra = _count - output.Length - _ahead;
            if (extra > 0)
            {
                _dropped++;
                extra -= extra % Channels;
                _read = (_read + extra) % _ring.Length;
                _count -= extra;
            }

            if (_priming && _count >= output.Length + _cushion)
            {
                _priming = false;
            }

            int taken = 0;
            if (!_priming)
            {
                taken = Math.Min(_count, output.Length);
                taken -= taken % Channels;
                for (int i = 0; i < taken; i++)
                {
                    output[i] = _ring[(_read + i) % _ring.Length];
                }

                _read = (_read + taken) % _ring.Length;
                _count -= taken;
                if (taken < output.Length)
                {
                    _priming = true;
                    _starved++;
                }
            }

            output[taken..].Clear();
        }

        return buffer.Length;
    }

    public void Dispose()
    {
        _target = 0;
        if (_capture is { } capture)
        {
            capture.DataAvailable -= OnCaptured;
            try
            {
                capture.StopRecording();
            }
            catch (COMException)
            {
                // The process is gone: so is its capture.
            }

            capture.Dispose();
            _capture = null;
        }

        if (_player is { } player)
        {
            try
            {
                player.Stop();
            }
            catch (COMException)
            {
            }

            player.Dispose();
            _player = null;
        }
    }
}
