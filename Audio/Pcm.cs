using NAudio.Dsp;
using NAudio.Wave;

namespace Lumen.Audio;

static class Pcm
{
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Decodes a WASAPI buffer to interleaved floats. Returns the frame count.</summary>
    public static int Decode(byte[] src, int bytes, WaveFormat f, ref float[] dst)
    {
        int frames = bytes / f.BlockAlign;
        int n = frames * f.Channels;
        if (dst.Length < n) dst = new float[n];

        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);

        switch (f.BitsPerSample)
        {
            case 32 when isFloat:
                Buffer.BlockCopy(src, 0, dst, 0, n * 4);
                break;
            case 16:
                for (int i = 0; i < n; i++) dst[i] = BitConverter.ToInt16(src, i * 2) / 32768f;
                break;
            case 24:
                for (int i = 0; i < n; i++)
                    dst[i] = (src[3 * i] | src[3 * i + 1] << 8 | (sbyte)src[3 * i + 2] << 16) / 8388608f;
                break;
            case 32:
                for (int i = 0; i < n; i++) dst[i] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                break;
            default:
                return 0;
        }
        return frames;
    }

    public static short ToShort(float v) => (short)Math.Clamp(v * 32767f, -32768f, 32767f);
}

/// <summary>Streaming resampler (WDL) to a fixed output rate; passes through when rates match.</summary>
sealed class Resampler
{
    readonly WdlResampler? _r;
    readonly int _channels;
    readonly double _ratio;
    float[] _out = Array.Empty<float>();

    public Resampler(int inRate, int outRate, int channels)
    {
        _channels = channels;
        _ratio = (double)outRate / inRate;
        if (inRate == outRate) return;
        _r = new WdlResampler();
        _r.SetMode(true, 2, false);
        _r.SetFilterParms();
        _r.SetFeedMode(true);
        _r.SetRates(inRate, outRate);
    }

    public float[] Process(float[] input, int frames, out int outFrames)
    {
        if (_r is null) { outFrames = frames; return input; }
        int n = _r.ResamplePrepare(frames, _channels, out var inBuf, out int inOffset);
        Array.Copy(input, 0, inBuf, inOffset, n * _channels);
        int max = (int)(frames * _ratio) + 32;
        if (_out.Length < max * _channels) _out = new float[max * _channels];
        outFrames = _r.ResampleOut(_out, 0, n, max, _channels);
        return _out;
    }
}
