using System.IO;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Lumen.Audio;

/// <summary>
/// After a recording: aligns the denoised track to the video and rewrites the MP4 with it.
/// Video is copied untouched (no re-encode). The original file is only replaced on success.
/// </summary>
static class AudioFinisher
{
    const int FirstVideo = unchecked((int)0xFFFFFFFC);
    const int FirstAudio = unchecked((int)0xFFFFFFFD);
    const int AllStreams = unchecked((int)0xFFFFFFFE);
    const int Chunk = AudioTap.Rate / 10;           // 100 ms of audio per sample written
    const int Fade = AudioTap.Rate / 100;           // 10 ms crossfade where the lag shifts
    /// <summary>Receives one line per run with the track lengths and lag map.</summary>
    public static Action<string>? Debug;

    /// <param name="expected">How long the recording ran, if known; a much shorter rewrite is refused.</param>
    public static void Run(string videoPath, AudioTap tap, TimeSpan expected, IProgress<double> progress)
    {
        MediaFoundationApi.Startup();

        // 1 · The recorder's own audio, decoded and reduced to the reference rate.
        var (videoRef, audioFrames, audioStart) = DecodeReference(videoPath);
        if (audioFrames == 0) throw new InvalidOperationException("no audio track");
        progress.Report(0.15);

        // 2 · Our raw mic reference.
        var tapRef = ReadReference(tap.RefPath);

        // 3 · Where does each 2 s block of the video sit in our capture?
        var lags = Aligner.Lags(videoRef, tapRef, new Progress<double>(p => progress.Report(0.15 + p * 0.35)));
        Debug?.Invoke($"audioFrames {audioFrames / (double)AudioTap.Rate:0.00}s start {audioStart / 1e7:0.000}s videoRef {videoRef.Length / 8000.0:0.00}s tapRef {tapRef.Length / 8000.0:0.00}s lags(ms) {string.Join(",", lags.Select(l => l / 8))}");

        // 4 · Rewrite: video copied as-is, audio replaced by the denoised mix.
        string temp = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + ".lumen-tmp.mp4");
        try
        {
            using var mix = new WaveFileReader(tap.MixPath);
            // Any part of the video the clean track cannot cover would come out silent; keep the original instead.
            double uncovered = Uncovered(lags, audioFrames, mix.Length / 4) / (double)AudioTap.Rate;
            if (uncovered > 0.5)
                throw new InvalidOperationException($"clean track misses {uncovered:0.0} s of the video; original kept");
            long videoEnd = Remux(videoPath, temp, mix, lags, audioFrames, audioStart, p => progress.Report(0.5 + p * 0.5));

            // Never trade the original for a shorter copy: if part of it could not be read, keep it as recorded.
            var written = TimeSpan.FromTicks(videoEnd);
            if (expected > TimeSpan.Zero && written < expected * 0.9 - TimeSpan.FromSeconds(2))
                throw new InvalidOperationException($"rewrite read only {written.TotalSeconds:0} s of {expected.TotalSeconds:0} s; original kept");

            // One replace call: there is never a moment with neither file on disk.
            File.Move(temp, videoPath, true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>Frames of the video whose lagged position falls outside the clean track.</summary>
    static long Uncovered(int[] lags, long audioFrames, long mixFrames)
    {
        const long block = (long)Aligner.Block * (AudioTap.Rate / AudioTap.RefRate);
        long missing = 0;
        for (int b = 0; b < lags.Length; b++)
        {
            long from = b * block, to = Math.Min(audioFrames, from + block);
            long lag = (long)lags[b] * (AudioTap.Rate / AudioTap.RefRate);
            long lo = Math.Max(from, -lag), hi = Math.Min(to, mixFrames - lag);
            missing += (to - from) - Math.Max(0, hi - lo);
        }
        return missing;
    }

    /// <summary>Streams the 16-bit reference straight into floats (no full-size byte copy).</summary>
    static float[] ReadReference(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var result = new float[fs.Length / 2];
        var buf = new byte[1 << 16];
        int at = 0, n;
        while (at < result.Length && (n = fs.Read(buf, 0, Math.Min(buf.Length, (result.Length - at) * 2))) > 1)
            for (int i = 0; i + 1 < n; i += 2) result[at++] = BitConverter.ToInt16(buf, i) / 32768f;
        return result;
    }

    static (float[] Ref, long Frames, long Start) DecodeReference(string path)
    {
        MediaFoundationInterop.MFCreateSourceReaderFromURL(path, null, out var reader);
        try
        {
            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstAudio, true);

            var pcm = MediaFoundationApi.CreateMediaType();
            pcm.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Audio);
            pcm.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, AudioSubtypes.MFAudioFormat_Float);
            reader.SetCurrentMediaType(FirstAudio, IntPtr.Zero, pcm);
            reader.GetCurrentMediaType(FirstAudio, out var current);
            var type = new MediaType(current);
            int channels = type.ChannelCount;
            if (type.SampleRate != AudioTap.Rate) throw new InvalidOperationException("audio track is not 48 kHz");

            const int decimate = AudioTap.Rate / AudioTap.RefRate;
            var refs = new List<float>(1 << 20);
            long frames = 0, start = -1;
            float acc = 0;
            int accN = 0;
            var tmp = Array.Empty<float>();

            void Push(float mono)
            {
                acc += mono;
                if (++accN == decimate) { refs.Add(acc / decimate); acc = 0; accN = 0; }
                frames++;
            }

            while (true)
            {
                if (!TryRead(reader, FirstAudio, out var flags, out ulong ts, out var sample)) break;
                if (sample is null)
                {
                    if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                    continue;
                }
                if (start < 0) start = (long)ts;

                // Keep the timeline continuous if the recorder left a gap.
                long expected = ((long)ts - start) * AudioTap.Rate / 10_000_000;
                while (frames < expected - Fade) Push(0);

                sample.ConvertToContiguousBuffer(out var buffer);
                buffer.Lock(out var ptr, out _, out int len);
                int n = len / 4;
                if (tmp.Length < n) tmp = new float[n];
                Marshal.Copy(ptr, tmp, 0, n);
                buffer.Unlock();
                for (int i = 0; i + channels <= n; i += channels)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += tmp[i + c];
                    Push(sum / channels);
                }
                Marshal.ReleaseComObject(buffer);
                Marshal.ReleaseComObject(sample);
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
            }
            return (refs.ToArray(), frames, Math.Max(0, start));
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <returns>End time of the last video sample written, in 100 ns units.</returns>
    static long Remux(string src, string dst, WaveFileReader mix, int[] lags, long audioFrames, long audioStart, Action<double> progress)
    {
        MediaFoundationInterop.MFCreateSourceReaderFromURL(src, null, out var reader);
        MediaFoundationInterop.MFCreateSinkWriterFromURL(dst, null, null, out var writer);
        try
        {
            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstVideo, true);
            reader.GetNativeMediaType(FirstVideo, 0, out var videoType);
            writer.AddStream(videoType, out int videoStream);
            writer.SetInputMediaType(videoStream, videoType, null);

            var pcmFormat = new WaveFormat(AudioTap.Rate, 16, 2);
            var aac = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_AAC, pcmFormat, 192000)
                      ?? throw new InvalidOperationException("no AAC encoder");
            writer.AddStream(aac.MediaFoundationObject, out int audioStream);
            writer.SetInputMediaType(audioStream, MediaFoundationApi.CreateMediaTypeFromWaveFormat(pcmFormat), null);
            writer.BeginWriting();

            var audio = new AudioCursor(mix, lags);
            long written = 0, lastVideo = -1;
            var pcm = new byte[Chunk * 4];

            void WriteAudioUntil(long hns)
            {
                while (written < audioFrames)
                {
                    long t = audioStart + written * 10_000_000 / AudioTap.Rate;
                    if (t > hns) break;
                    int n = (int)Math.Min(Chunk, audioFrames - written);
                    audio.Read(written, n, pcm);
                    WriteSample(writer, audioStream, pcm, n * 4, t, (long)n * 10_000_000 / AudioTap.Rate);
                    written += n;
                }
            }

            while (true)
            {
                if (!TryRead(reader, FirstVideo, out var flags, out ulong ts, out var sample)) break;
                if (sample is not null)
                {
                    // Variable-framerate output occasionally repeats a timestamp; the MP4 sink needs them increasing.
                    sample.GetSampleTime(out long t);
                    if (t <= lastVideo) sample.SetSampleTime(t = lastVideo + 1);
                    lastVideo = t;
                    WriteAudioUntil(t + 5_000_000);
                    writer.WriteSample(videoStream, sample);
                    Marshal.ReleaseComObject(sample);
                    progress((double)written / audioFrames);
                }
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
            }
            WriteAudioUntil(long.MaxValue);
            writer.DoFinalize();
            return Math.Max(0, lastVideo);
        }
        finally
        {
            Marshal.ReleaseComObject(writer);
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>
    /// A recording cut short (recorder killed, power loss) ends in a partial fragment.
    /// Treat a read error there as the end of the stream instead of failing the whole file.
    /// </summary>
    static bool TryRead(IMFSourceReader reader, int stream, out MF_SOURCE_READER_FLAG flags, out ulong ts, out IMFSample? sample)
    {
        try
        {
            reader.ReadSample(stream, 0, out _, out flags, out ts, out sample);
            return true;
        }
        catch (COMException)
        {
            flags = MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM;
            ts = 0;
            sample = null;
            return false;
        }
    }

    static void WriteSample(IMFSinkWriter writer, int stream, byte[] data, int length, long time, long duration)
    {
        var buffer = MediaFoundationApi.CreateMemoryBuffer(length);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(data, 0, ptr, length);
        buffer.Unlock();
        buffer.SetCurrentLength(length);
        var sample = MediaFoundationApi.CreateSample();
        sample.AddBuffer(buffer);
        sample.SetSampleTime(time);
        sample.SetSampleDuration(duration);
        writer.WriteSample(stream, sample);
        Marshal.ReleaseComObject(sample);
        Marshal.ReleaseComObject(buffer);
    }

    /// <summary>Reads the denoised mix through the per-block lag map, crossfading where it shifts.</summary>
    sealed class AudioCursor(WaveFileReader mix, int[] lags)
    {
        const int Ratio = AudioTap.Rate / AudioTap.RefRate;
        const int BlockFrames = Aligner.Block * Ratio;
        readonly long _total = mix.Length / 4;
        byte[] _a = Array.Empty<byte>(), _b = Array.Empty<byte>();

        long Lag(long frame)
        {
            long block = Math.Min(frame / BlockFrames, lags.Length - 1);
            return (long)lags[Math.Max(0, block)] * Ratio;
        }

        public void Read(long frame, int count, byte[] dst)
        {
            if (_a.Length < count * 4) { _a = new byte[count * 4]; _b = new byte[count * 4]; }
            long lag = Lag(frame);
            Fetch(frame + lag, count, _a);
            Buffer.BlockCopy(_a, 0, dst, 0, count * 4);

            long blockStart = frame / BlockFrames * BlockFrames;
            if (blockStart == frame && frame > 0)
            {
                long prev = Lag(frame - 1);
                if (prev != lag)
                {
                    int n = Math.Min(Fade, count);
                    Fetch(frame + prev, n, _b);
                    for (int i = 0; i < n * 2; i++)
                    {
                        float w = (float)(i / 2) / n;
                        short x = BitConverter.ToInt16(_b, i * 2), y = BitConverter.ToInt16(_a, i * 2);
                        short v = (short)(x * (1 - w) + y * w);
                        dst[i * 2] = (byte)v; dst[i * 2 + 1] = (byte)(v >> 8);
                    }
                }
            }
        }

        void Fetch(long frame, int count, byte[] dst)
        {
            Array.Clear(dst, 0, count * 4);
            long from = Math.Max(0, frame), to = Math.Min(_total, frame + count);
            if (to <= from) return;
            mix.Position = from * 4;
            int offset = (int)(from - frame) * 4;
            int need = (int)(to - from) * 4;
            int got = 0;
            while (got < need)
            {
                int r = mix.Read(dst, offset + got, need - got);
                if (r <= 0) break;
                got += r;
            }
        }
    }
}
