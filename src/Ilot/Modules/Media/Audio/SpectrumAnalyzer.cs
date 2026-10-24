namespace Ilot.Modules.Media.Audio;

/// <summary>
/// Turns a stream of mono samples into perceptual band levels between 0 and 1.
/// </summary>
/// <remarks>
/// Hann-windowed 4096-point FFT, fine enough to keep two dozen bands distinct down in the bass; bands spaced
/// logarithmically, like hearing; a +3 dB per octave tilt so a typical mix (whose energy falls off with frequency)
/// lights every band evenly; and an automatic gain that follows the loudness of the track, so quiet and loud songs
/// both fill the bars.
/// </remarks>
internal sealed class SpectrumAnalyzer
{
    public const int FftSize = 4096;

    private const int HopSize = FftSize / 4;
    private const double LowestFrequency = 55;
    private const double HighestFrequency = 12000;
    private const double DynamicRangeDb = 38;
    private const double PeakFloorDb = -72;
    private const double PeakReleaseDbPerSecond = 9;

    // A full-scale sine peaks at N/4 once Hann-windowed: normalise power so that it reads 0 dB.
    private const double PowerNormalisation = 1.0 / (FftSize / 4.0 * (FftSize / 4.0));

    private readonly Fft _fft = new(FftSize);
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _history = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imaginary = new float[FftSize];
    private readonly double[] _decibels;
    private readonly int[] _bandStart;
    private readonly int[] _bandEnd;
    private readonly double[] _bandTilt;
    private readonly double _hopSeconds;
    private int _next;
    private int _sinceAnalysis;
    private double _peakDb = PeakFloorDb;

    public SpectrumAnalyzer(int sampleRate, int bandCount)
    {
        BandCount = bandCount;
        _hopSeconds = (double)HopSize / sampleRate;
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        }

        _decibels = new double[bandCount];
        _bandStart = new int[bandCount];
        _bandEnd = new int[bandCount];
        _bandTilt = new double[bandCount];
        // Contiguous bands: each one starts exactly where the previous one ends, at least one bin wide.
        double binWidth = (double)sampleRate / FftSize;
        double ratio = Math.Pow(HighestFrequency / LowestFrequency, 1.0 / bandCount);
        int lastBin = FftSize / 2 - 1;
        int start = Math.Clamp((int)Math.Round(LowestFrequency / binWidth), 1, lastBin);
        for (int band = 0; band < bandCount; band++)
        {
            double low = LowestFrequency * Math.Pow(ratio, band);
            double high = low * ratio;
            int end = Math.Clamp((int)Math.Round(high / binWidth), start + 1, lastBin + 1);
            _bandStart[band] = start;
            _bandEnd[band] = end;
            _bandTilt[band] = 3 * Math.Log2(Math.Sqrt(low * high) / LowestFrequency);
            start = Math.Min(end, lastBin);
        }
    }

    public int BandCount { get; }

    /// <summary>Appends samples; returns true (with fresh <paramref name="levels"/>) whenever a new analysis ran.</summary>
    public bool Push(ReadOnlySpan<float> samples, Span<float> levels)
    {
        bool analysed = false;
        foreach (float sample in samples)
        {
            _history[_next] = sample;
            _next = (_next + 1) % FftSize;
            if (++_sinceAnalysis >= HopSize)
            {
                _sinceAnalysis = 0;
                Analyse(levels);
                analysed = true;
            }
        }

        return analysed;
    }

    private void Analyse(Span<float> levels)
    {
        // Oldest sample first, windowed.
        for (int i = 0; i < FftSize; i++)
        {
            _real[i] = _history[(_next + i) % FftSize] * _window[i];
            _imaginary[i] = 0;
        }

        _fft.Forward(_real, _imaginary);

        double loudest = double.NegativeInfinity;
        for (int band = 0; band < BandCount; band++)
        {
            double power = 0;
            for (int bin = _bandStart[band]; bin < _bandEnd[band]; bin++)
            {
                power += _real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin];
            }

            power *= PowerNormalisation / (_bandEnd[band] - _bandStart[band]);
            _decibels[band] = 10 * Math.Log10(power + 1e-14) + _bandTilt[band];
            loudest = Math.Max(loudest, _decibels[band]);
        }

        // Automatic gain: jump to new peaks at once, ease down slowly, never boost beyond the floor.
        _peakDb = Math.Max(Math.Max(loudest, _peakDb - PeakReleaseDbPerSecond * _hopSeconds), PeakFloorDb);
        double floor = _peakDb - DynamicRangeDb;
        for (int band = 0; band < BandCount; band++)
        {
            levels[band] = (float)Math.Clamp((_decibels[band] - floor) / DynamicRangeDb, 0, 1);
        }
    }
}
