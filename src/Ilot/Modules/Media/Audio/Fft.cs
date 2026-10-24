namespace Ilot.Modules.Media.Audio;

/// <summary>In-place radix-2 FFT of a fixed power-of-two size, with precomputed twiddle factors.</summary>
internal sealed class Fft
{
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("The size must be a power of two.", nameof(size));
        }

        Size = size;
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double angle = -2 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(angle);
            _sin[k] = (float)Math.Sin(angle);
        }
    }

    public int Size { get; }

    /// <summary>Forward transform, unscaled: X[k] = Σ x[n]·e^(−2πikn/N).</summary>
    public void Forward(Span<float> real, Span<float> imaginary)
    {
        int n = Size;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        // Butterflies.
        for (int length = 2; length <= n; length <<= 1)
        {
            int half = length / 2;
            int step = n / length;
            for (int start = 0; start < n; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step];
                    float wi = _sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float tr = real[b] * wr - imaginary[b] * wi;
                    float ti = real[b] * wi + imaginary[b] * wr;
                    real[b] = real[a] - tr;
                    imaginary[b] = imaginary[a] - ti;
                    real[a] += tr;
                    imaginary[a] += ti;
                }
            }
        }
    }
}
