namespace Lumen.Audio;

/// <summary>
/// Finds, block by block, where the recorder's own mic track sits inside our raw mic capture.
/// Both come from the same device, so their cross-correlation peaks sharply even on room noise.
/// Tracking per block absorbs start-up latency, pauses and slow clock drift.
/// </summary>
static class Aligner
{
    public const int Block = AudioTap.RefRate * 2;           // 2 s
    const int NearSearch = AudioTap.RefRate / 4;             // ±0.25 s around the last lag
    const int WideSearch = AudioTap.RefRate * 3;             // ±3 s when tracking is lost
    const double Accept = 0.3, AcceptWide = 0.45;

    /// <summary>Lag per block, in reference samples: video ref[i] ≈ tap ref[i + lag].</summary>
    public static int[] Lags(float[] video, float[] tap, IProgress<double>? progress = null)
    {
        int blocks = (video.Length + Block - 1) / Block;
        var lags = new int[blocks];

        // The tap starts just before the recorder, so the first lag is a small positive number.
        int probe = Math.Min(video.Length, AudioTap.RefRate * 10);
        var (lag, _) = Best(video, 0, probe, tap, -AudioTap.RefRate / 2, AudioTap.RefRate * 4);

        for (int b = 0; b < blocks; b++)
        {
            int start = b * Block;
            int n = Math.Min(Block, video.Length - start);
            if (n >= AudioTap.RefRate / 2 && Energy(video, start, n) > 1e-7 * n)
            {
                var near = Best(video, start, n, tap, lag - NearSearch, lag + NearSearch);
                if (near.Score >= Accept) lag = near.Lag;
                else
                {
                    var wide = Best(video, start, n, tap, lag - WideSearch, lag + WideSearch);
                    if (wide.Score >= AcceptWide) lag = wide.Lag;
                }
            }
            lags[b] = lag;
            if ((b & 15) == 0) progress?.Report((double)b / blocks);
        }
        return lags;
    }

    static double Energy(float[] x, int start, int n)
    {
        double e = 0;
        for (int i = start; i < start + n; i++) e += x[i] * x[i];
        return e;
    }

    /// <summary>Best lag in [lo, hi] by normalized cross-correlation, computed with one FFT pair.</summary>
    static (int Lag, double Score) Best(float[] a, int aStart, int n, float[] b, int lo, int hi)
    {
        int span = hi - lo + n;
        int size = 1;
        while (size < span + n) size <<= 1;

        // Reused across blocks: a fresh set per 2 s block meant ~3,600 large-object allocations per hour.
        var ar = Rent(ref _ar, size); var ai = Rent(ref _ai, size);
        var br = Rent(ref _br, size); var bi = Rent(ref _bi, size);
        for (int i = 0; i < n; i++) ar[i] = a[aStart + i];
        int bStart = aStart + lo;
        for (int i = 0; i < span; i++)
        {
            int j = bStart + i;
            br[i] = j >= 0 && j < b.Length ? b[j] : 0;
        }

        Fft(ar, ai, size, false);
        Fft(br, bi, size, false);
        for (int i = 0; i < size; i++)
        {
            // conj(A) · B
            double re = ar[i] * br[i] + ai[i] * bi[i];
            double im = ar[i] * bi[i] - ai[i] * br[i];
            br[i] = re; bi[i] = im;
        }
        Fft(br, bi, size, true);

        double ea = Energy(a, aStart, n);

        // Sliding energy of the b window for normalization.
        var prefix = Rent(ref _prefix, span + 1);
        for (int i = 0; i < span; i++)
        {
            int j = bStart + i;
            double v = j >= 0 && j < b.Length ? b[j] : 0;
            prefix[i + 1] = prefix[i] + v * v;
        }

        int bestK = 0;
        double best = double.NegativeInfinity;
        for (int k = 0; k <= hi - lo; k++)
        {
            double eb = prefix[k + n] - prefix[k];
            if (eb <= 0 || ea <= 0) continue;
            double score = br[k] / size / Math.Sqrt(ea * eb);
            if (score > best) { best = score; bestK = k; }
        }
        return (lo + bestK, best);
    }

    [ThreadStatic] static double[]? _ar, _ai, _br, _bi, _prefix;

    static double[] Rent(ref double[]? buffer, int size)
    {
        if (buffer is null || buffer.Length < size) buffer = new double[size];
        else Array.Clear(buffer, 0, size);
        return buffer;
    }

    /// <summary>In-place iterative radix-2 FFT over the first <paramref name="n"/> elements.</summary>
    static void Fft(double[] re, double[] im, int n, bool inverse)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < half; k++)
                {
                    int p = i + k, q = p + half;
                    double tr = re[q] * cr - im[q] * ci;
                    double ti = re[q] * ci + im[q] * cr;
                    re[q] = re[p] - tr; im[q] = im[p] - ti;
                    re[p] += tr; im[p] += ti;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }
}
