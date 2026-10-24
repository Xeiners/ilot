using System.Runtime.InteropServices;
using Ilot.Modules.Pomodoro;

namespace Ilot.Modules.Study;

/// <summary>
/// The revisions' little sounds, made here (no file): soft and clean, a round tone (a sine, a breath of its octave
/// that fades first), struck gently (no click) and fading quickly, low in the mix. Two notes rising for a right answer;
/// two low, muffled ones for a wrong one (a nudge, never a buzzer); a calm arpeggio once a session is done; three notes
/// when a course is ready. At the volume chosen (see <see cref="Settings.AppSettings.StudySoundVolume"/>).
/// </summary>
internal static class StudySounds
{
    private const int Rate = 44100;

    // How loud a note is at full volume: quiet, a cue beside what plays.
    private const double Loudest = 0.075;

    /// <summary>E5 then B5: a little yes.</summary>
    public static void Right(double volume) => Play([(659.3, 0), (987.8, 0.075)], 0.55, 7, volume);

    /// <summary>A4 then F4, short and round: missed, go on.</summary>
    public static void Wrong(double volume) => Play([(440.0, 0), (349.2, 0.09)], 0.45, 10, volume * 0.85);

    /// <summary>C5 E5 G5 C6: done, well done.</summary>
    public static void Done(double volume) => Play([(523.3, 0), (659.3, 0.09), (784.0, 0.18), (1046.5, 0.27)], 1.1, 4.5, volume);

    /// <summary>G5 C6 E6: ready.</summary>
    public static void Ready(double volume) => Play([(784.0, 0), (1046.5, 0.1), (1318.5, 0.2)], 0.9, 5, volume);

    /// <summary>Plays <paramref name="sound"/> at the volume chosen, unless the revisions' sounds are off.</summary>
    public static void Play(Settings.AppSettings settings, Action<double> sound)
    {
        if (settings.StudySounds)
        {
            sound(settings.StudySoundVolume);
        }
    }

    private static void Play((double Frequency, double At)[] notes, double ring, double decay, double volume)
    {
        if (volume > 0)
        {
            Chime.Play(Render(notes, ring, decay, Loudest * Math.Clamp(volume, 0, 100) / 100));
        }
    }

    /// <summary>The notes, each starting at its time and ringing <paramref name="ring"/> seconds: 16-bit mono PCM.</summary>
    internal static byte[] Render((double Frequency, double At)[] notes, double ring, double decay, double gain)
    {
        int length = (int)((notes.Max(note => note.At) + ring) * Rate);
        var samples = new float[length];
        foreach (var (frequency, at) in notes)
        {
            int start = (int)(at * Rate);
            int end = Math.Min(length, start + (int)(ring * Rate));
            for (int i = start; i < end; i++)
            {
                double t = (double)(i - start) / Rate;
                double attack = t < 0.012 ? Math.Sin(t / 0.012 * Math.PI / 2) : 1; // A soft strike: no click.
                double release = Math.Min(1, (ring - t) / 0.05); // And no click at its end either.
                double tone = Math.Sin(2 * Math.PI * frequency * t) + 0.18 * Math.Sin(4 * Math.PI * frequency * t) * Math.Exp(-t * decay * 2);
                samples[i] += (float)(tone * attack * release * Math.Exp(-t * decay) * gain);
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
