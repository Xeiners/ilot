using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MyNotch.Modules.Assistant.Voice;

/// <summary>
/// The default microphone, while the voice is on: what it hears, as Gemini takes it (16 kHz, 16-bit, mono PCM, in pieces
/// of <see cref="PieceLength"/>), and how loud (for the mascot, and to tell the user's voice from Gemini's own coming back).
/// </summary>
/// <remarks>
/// Opened for communications where Windows has it: its echo cancellation takes Gemini's voice, played on the speakers,
/// out of what the microphone hears (<see cref="CancelsEcho"/>). The format is asked for; whatever comes is converted.
/// </remarks>
internal sealed class Microphone : IDisposable
{
    public const int Rate = 16000;

    /// <summary>A piece sent this long: short enough to be heard at once, long enough not to flood the connection.</summary>
    private static readonly TimeSpan PieceLength = TimeSpan.FromMilliseconds(60);

    private readonly short[] _piece = new short[(int)(Rate * PieceLength.TotalSeconds)];
    private WasapiRecorder _recorder;
    private int _channels;
    private bool _float;
    private double _step;
    private bool _known;
    private int _filled;
    private double _phase;
    private double _sum;
    private int _count;
    private double _energy;

    /// <summary>Opens the default microphone (not yet listening). Throws if there is none, or Windows keeps it from the app.</summary>
    public Microphone() => _recorder = Build(communications: true);

    /// <summary>A piece heard (16 kHz PCM), and how loud it was (RMS, 0 to 1). Raised on the microphone's thread.</summary>
    public event Action<byte[], float>? Heard;

    /// <summary>How loud the microphone is now (RMS of the last piece, 0 to 1).</summary>
    public float Level { get; private set; }

    /// <summary>Windows takes what the speakers play out of what the microphone hears (known once started).</summary>
    public bool CancelsEcho { get; private set; }

    /// <summary>Starts listening: for communications (echo cancellation) where the microphone takes it, else plainly.</summary>
    public void Start()
    {
        try
        {
            _recorder.StartRecording();
            CancelsEcho = _recorder.AcousticEchoCancellationControl is not null;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _recorder.Dispose();
            _recorder = Build(communications: false);
            _recorder.StartRecording();
            CancelsEcho = false;
        }
    }

    public void Dispose()
    {
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        _recorder.Dispose();
    }

    private WasapiRecorder Build(bool communications)
    {
        var builder = new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(40).WithFormat(new WaveFormat(Rate, 16, 1));
        if (communications)
        {
            builder = builder.WithCommunicationsMode();
        }

        var recorder = builder.Build();
        recorder.DataAvailable += (buffer, _, _, _) => Take(buffer);
        _known = false;
        return recorder;
    }

    /// <summary>
    /// What the device gives, to 16 kHz mono (when Windows did not convert it already): the channels averaged, then every
    /// sample of the output the mean of the input ones it covers (a box filter: enough against aliasing for a voice).
    /// </summary>
    private void Take(ReadOnlySpan<byte> bytes)
    {
        if (!_known)
        {
            var format = _recorder.WaveFormat;
            _channels = Math.Max(1, format.Channels);
            _float = format.BitsPerSample == 32 && format.Encoding != WaveFormatEncoding.Pcm;
            _step = (double)Rate / format.SampleRate;
            _known = true;
        }

        int frameBytes = _channels * (_float ? 4 : 2);
        for (int at = 0; at + frameBytes <= bytes.Length; at += frameBytes)
        {
            double mono = 0;
            for (int channel = 0; channel < _channels; channel++)
            {
                mono += _float
                    ? MemoryMarshal.Read<float>(bytes.Slice(at + channel * 4, 4))
                    : MemoryMarshal.Read<short>(bytes.Slice(at + channel * 2, 2)) / 32768.0;
            }

            _sum += mono / _channels;
            _count++;
            _phase += _step;
            if (_phase < 1)
            {
                continue;
            }

            _phase -= 1;
            double sample = Math.Clamp(_sum / _count, -1, 1);
            _sum = 0;
            _count = 0;
            _energy += sample * sample;
            _piece[_filled++] = (short)(sample * 32767);
            if (_filled == _piece.Length)
            {
                Level = (float)Math.Sqrt(_energy / _filled);
                Heard?.Invoke(MemoryMarshal.AsBytes(_piece.AsSpan()).ToArray(), Level);
                _filled = 0;
                _energy = 0;
            }
        }
    }
}

/// <summary>
/// Gemini's voice on the default output, played as it comes (24 kHz, 16-bit, mono PCM; Windows converts it to the
/// device's own), with how loud it is now, for the mascot.
/// </summary>
internal sealed class Speaker : IDisposable
{
    public const int Rate = 24000;

    private readonly BufferedWaveProvider _buffer;
    private readonly Loudness _loudness;
    private readonly WasapiPlayer _player;

    /// <summary>Opens the default output, and plays (silence, until there is a voice).</summary>
    public Speaker()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, 1), TimeSpan.FromMinutes(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _loudness = new Loudness(_buffer);
        _player = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(80).Build();
        _player.Init(_loudness);
        _player.Play();
    }

    /// <summary>A voice is still to be heard (some of it is waiting to play).</summary>
    public bool IsSpeaking => _buffer.BufferedBytes > 0;

    /// <summary>How loud the voice playing now is (RMS, 0 to 1).</summary>
    public float Level => IsSpeaking ? _loudness.Level : 0;

    public void Play(byte[] pcm) => _buffer.AddSamples(pcm);

    /// <summary>How loud the voice plays (0 to 1), at once, even mid-sentence.</summary>
    public float Volume
    {
        get => _loudness.Volume;
        set => _loudness.Volume = Math.Clamp(value, 0, 1);
    }

    /// <summary>Talked over: what is still to play is dropped.</summary>
    public void Hush() => _buffer.ClearBuffer();

    public void Dispose()
    {
        try
        {
            _player.Stop();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        _player.Dispose();
    }

    /// <summary>
    /// Passes the sound on at its <see cref="Volume"/>, noting how loud the voice itself is (before the volume: the mascot
    /// talks as much, however low it is turned).
    /// </summary>
    private sealed class Loudness(IWaveProvider source) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public float Level { get; private set; }

        public volatile float Volume = 1;

        public int Read(Span<byte> buffer)
        {
            int read = source.Read(buffer);
            var samples = MemoryMarshal.Cast<byte, short>(buffer[..(read - read % 2)]);
            float volume = Volume;
            double energy = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                energy += (double)samples[i] * samples[i];
                if (volume < 1)
                {
                    samples[i] = (short)(samples[i] * volume);
                }
            }

            Level = samples.Length > 0 ? (float)(Math.Sqrt(energy / samples.Length) / 32768) : 0;
            return read;
        }
    }
}
