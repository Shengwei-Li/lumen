using System.Runtime.InteropServices;

namespace Lumen.Audio;

/// <summary>RNNoise (xiph.org): recurrent-network noise suppression, 48 kHz mono, 10 ms frames.</summary>
sealed class Denoiser : IDisposable
{
    public const int FrameSize = 480;

    [DllImport("rnnoise.dll")] static extern IntPtr rnnoise_create(IntPtr model);
    [DllImport("rnnoise.dll")] static extern void rnnoise_destroy(IntPtr state);
    [DllImport("rnnoise.dll")] static extern float rnnoise_process_frame(IntPtr state, float[] output, float[] input);

    IntPtr _state = rnnoise_create(IntPtr.Zero);

    /// <summary>Denoises one frame in place. Samples are in 16-bit range (±32768), as RNNoise expects.</summary>
    public void Process(float[] frame) => rnnoise_process_frame(_state, frame, frame);

    public void Dispose()
    {
        if (_state == IntPtr.Zero) return;
        rnnoise_destroy(_state);
        _state = IntPtr.Zero;
    }
}
