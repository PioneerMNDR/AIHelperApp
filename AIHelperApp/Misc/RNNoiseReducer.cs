using RNNoise.NET;

public class RNNoiseReducer : IDisposable
{
    private readonly Denoiser _denoiser;
    private const int FrameSize = 480;  // RNNoise работает фреймами по 480 @ 48kHz
    private readonly float[] _frameBuffer = new float[FrameSize];
    private int _bufferPos;
    private readonly List<float> _outputBuffer = new();

    // RNNoise ожидает 48kHz, поэтому нужен ресэмплинг 16k→48k→16k
    public RNNoiseReducer()
    {
        _denoiser = new Denoiser();
    }

    public float[] Process(float[] input16k)
    {
        // Upsample 16k → 48k
        var samples48k = Resample(input16k, 16000, 48000);

        _outputBuffer.Clear();

        for (int i = 0; i < samples48k.Length; i++)
        {
            _frameBuffer[_bufferPos++] = samples48k[i] * short.MaxValue; // RNNoise ожидает int16 диапазон

            if (_bufferPos >= FrameSize)
            {
                _denoiser.Denoise(_frameBuffer);

                for (int j = 0; j < FrameSize; j++)
                    _outputBuffer.Add(_frameBuffer[j] / short.MaxValue);

                _bufferPos = 0;
            }
        }

        // Downsample 48k → 16k
        return Resample(_outputBuffer.ToArray(), 48000, 16000);
    }

    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        double ratio = (double)toRate / fromRate;
        int outLen = (int)(input.Length * ratio);
        var output = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i / ratio;
            int idx = (int)srcPos;
            double frac = srcPos - idx;
            output[i] = idx + 1 < input.Length
                ? (float)(input[idx] * (1 - frac) + input[idx + 1] * frac)
                : input[Math.Min(idx, input.Length - 1)];
        }
        return output;
    }

    public void Dispose() => _denoiser?.Dispose();
}