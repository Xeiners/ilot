using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MyNotch.Modules.Pomodoro;

/// <summary>
/// A soft chime at the end of a phase, made here (no sound file): bell notes, a little apart, each struck then fading
/// (a few harmonics, a fast attack, a long decay): a quick rising arpeggio for the end of a focus (a little victory,
/// time to play), two notes rising for the end of a break (back to work). Played once on the default output; nothing stays open after.
/// </summary>
public static class Chime
{
    private const int Rate = 44100;

    // How loud each note is struck: soft, a cue beside what plays, not over it (about half what it was at first).
    private const double Gain = 0.09;

    /// <summary>Plays the chime for the end of a focus (<paramref name="rest"/>) or of a break.</summary>
    public static void Play(bool rest)
    {
        // C6 E6 G6 C7 to rest, C6 then G6 to work.
        double[] notes = rest ? [1046.5, 1318.5, 1568.0, 2093.0] : [1046.5, 1568.0];
        Play(Render(notes, TimeSpan.FromMilliseconds(rest ? 105 : 170), TimeSpan.FromSeconds(1.3)));
    }

    /// <summary>Plays <paramref name="pcm"/> (16-bit mono, see <see cref="Render"/>) once on the default output; let go after.</summary>
    internal static void Play(byte[] pcm)
    {
        try
        {
            var player = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(80).Build();
            var buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, 1), TimeSpan.FromSeconds(4)) { ReadFully = false };
            buffer.AddSamples(pcm);
            player.Init(buffer);
            player.Play();

            // Let go once it has rung (not from its own end event: that comes on its playing thread).
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => player.Dispose(), TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Debug.WriteLine($"[Chime] Not played: {ex.Message}");
        }
    }

    /// <summary>The notes, one after the other <paramref name="gap"/> apart, each ringing <paramref name="ring"/>: 16-bit mono PCM.</summary>
    internal static byte[] Render(IReadOnlyList<double> notes, TimeSpan gap, TimeSpan ring)
    {
        int length = (int)((gap.TotalSeconds * (notes.Count - 1) + ring.TotalSeconds) * Rate);
        var samples = new float[length];
        for (int n = 0; n < notes.Count; n++)
        {
            int start = (int)(gap.TotalSeconds * n * Rate);
            int end = Math.Min(length, start + (int)(ring.TotalSeconds * Rate));
            for (int i = start; i < end; i++)
            {
                double t = (double)(i - start) / Rate;
                double attack = Math.Min(t / 0.006, 1); // No click.
                double decay = Math.Exp(-t * 3.2);
                double f = notes[n];
                // A bell: the note, an octave and a fifth above (fainter, dying sooner).
                double tone = Math.Sin(2 * Math.PI * f * t)
                    + 0.32 * Math.Sin(2 * Math.PI * f * 2 * t) * Math.Exp(-t * 4)
                    + 0.12 * Math.Sin(2 * Math.PI * f * 3 * t) * Math.Exp(-t * 6);
                samples[i] += (float)(tone * attack * decay * Gain);
            }
        }

        var pcm = new short[length];
        for (int i = 0; i < length; i++)
        {
            pcm[i] = (short)(Math.Clamp(samples[i], -1, 1) * short.MaxValue);
        }

        return MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
    }
}
