using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIHelperApp.Services
{
    public class AudioCaptureService : IDisposable
    {
        public const int TargetSampleRate = 16000;

        private WasapiLoopbackCapture _loopbackCapture;
        private WasapiCapture _micCapture;
        private MMDevice _loopbackDevice;
        private MMDevice _micDevice;

        public event Action<float[]> LoopbackSamplesAvailable;
        public event Action<float[]> MicSamplesAvailable;

        public float LoopbackRmsLevel { get; private set; }
        public float MicRmsLevel { get; private set; }
        public bool IsCapturing { get; private set; }

        private RNNoiseReducer _micDenoiser;

        public bool NoiseReductionEnabled { get; set; } = true;


        // ═══ Перечисление устройств ═══

        public List<Models.AudioDeviceInfo> GetLoopbackDevices()
        {
            var result = new List<Models.AudioDeviceInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice defaultDevice = null;
                try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
                catch { }

                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    result.Add(new Models.AudioDeviceInfo
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        IsDefault = defaultDevice != null && device.ID == defaultDevice.ID
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] Error enumerating loopback: {ex.Message}");
            }
            return result;
        }

        public List<Models.AudioDeviceInfo> GetMicDevices()
        {
            var result = new List<Models.AudioDeviceInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice defaultDevice = null;
                try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
                catch { }

                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in devices)
                {
                    result.Add(new Models.AudioDeviceInfo
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        IsDefault = defaultDevice != null && device.ID == defaultDevice.ID
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] Error enumerating mic: {ex.Message}");
            }
            return result;
        }

        // ═══ Управление захватом ═══

        public void Start(string loopbackDeviceId = null, string micDeviceId = null)
        {
            Stop();

            try
            {
                StartLoopback(loopbackDeviceId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] Loopback start failed: {ex.Message}");
            }

            try
            {
                StartMic(micDeviceId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] Mic start failed: {ex.Message}");
            }

            IsCapturing = true;
        }

        public void Stop()
        {
            IsCapturing = false;

            try
            {
                if (_loopbackCapture != null)
                {
                    _loopbackCapture.StopRecording();
                    _loopbackCapture.DataAvailable -= OnLoopbackData;
                    _loopbackCapture.Dispose();
                    _loopbackCapture = null;
                }
            }
            catch { }

            try
            {
                if (_micCapture != null)
                {
                    _micCapture.StopRecording();
                    _micCapture.DataAvailable -= OnMicData;
                    _micCapture.Dispose();
                    _micCapture = null;
                }
            }
            catch { }

            LoopbackRmsLevel = 0;
            MicRmsLevel = 0;
        }

        // ═══ Запуск отдельных источников ═══

        private void StartLoopback(string deviceId)
        {
            var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrEmpty(deviceId))
                _loopbackDevice = enumerator.GetDevice(deviceId);
            else
                _loopbackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _loopbackCapture = new WasapiLoopbackCapture(_loopbackDevice);
            _loopbackCapture.DataAvailable += OnLoopbackData;
            _loopbackCapture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    System.Diagnostics.Debug.WriteLine($"[Audio] Loopback error: {e.Exception.Message}");
            };
            _loopbackCapture.StartRecording();

            System.Diagnostics.Debug.WriteLine(
                $"[Audio] Loopback started: {_loopbackDevice.FriendlyName} " +
                $"({_loopbackCapture.WaveFormat.SampleRate}Hz, {_loopbackCapture.WaveFormat.Channels}ch, " +
                $"{_loopbackCapture.WaveFormat.BitsPerSample}bit)");
        }

        private void StartMic(string deviceId)
        {
            var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrEmpty(deviceId))
                _micDevice = enumerator.GetDevice(deviceId);
            else
                _micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            _micCapture = new WasapiCapture(_micDevice);
            _micCapture.DataAvailable += OnMicData;
            _micCapture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    System.Diagnostics.Debug.WriteLine($"[Audio] Mic error: {e.Exception.Message}");
            };
            _micCapture.StartRecording();

            System.Diagnostics.Debug.WriteLine(
                $"[Audio] Mic started: {_micDevice.FriendlyName} " +
                $"({_micCapture.WaveFormat.SampleRate}Hz, {_micCapture.WaveFormat.Channels}ch, " +
                $"{_micCapture.WaveFormat.BitsPerSample}bit)");
        }

        // ═══ Обработка данных ═══

        private void OnLoopbackData(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            var converted = ConvertToMono16k(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat);
            if (converted == null || converted.Length == 0) return;

                  // ══ Шумоподавление ══
        if (NoiseReductionEnabled && _micDenoiser != null)
            converted = _micDenoiser.Process(converted);

            LoopbackRmsLevel = CalculateRms(converted);
            LoopbackSamplesAvailable?.Invoke(converted);
        }

        private void OnMicData(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            var converted = ConvertToMono16k(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);
            if (converted == null || converted.Length == 0) return;

            // ══ Шумоподавление ══
            if (NoiseReductionEnabled && _micDenoiser != null)
                converted = _micDenoiser.Process(converted);

            MicRmsLevel = CalculateRms(converted);
            MicSamplesAvailable?.Invoke(converted);
        }

        // ═══ Конвертация аудио ═══

        private static float[] ConvertToMono16k(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            float[] rawFloats;

            // Bytes → float[]
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                int count = bytesRecorded / 4;
                rawFloats = new float[count];
                Buffer.BlockCopy(buffer, 0, rawFloats, 0, bytesRecorded);
            }
            else if (format.BitsPerSample == 16)
            {
                int count = bytesRecorded / 2;
                rawFloats = new float[count];
                for (int i = 0; i < count; i++)
                    rawFloats[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            }
            else if (format.BitsPerSample == 24)
            {
                int count = bytesRecorded / 3;
                rawFloats = new float[count];
                for (int i = 0; i < count; i++)
                {
                    int sample = (buffer[i * 3] << 8) | (buffer[i * 3 + 1] << 16) | (buffer[i * 3 + 2] << 24);
                    rawFloats[i] = sample / 2147483648f;
                }
            }
            else
            {
                return Array.Empty<float>();
            }

            // Stereo/multi → mono
            float[] mono;
            int channels = format.Channels;
            if (channels > 1)
            {
                int monoLen = rawFloats.Length / channels;
                mono = new float[monoLen];
                for (int i = 0; i < monoLen; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                        sum += rawFloats[i * channels + ch];
                    mono[i] = sum / channels;
                }
            }
            else
            {
                mono = rawFloats;
            }

            // Resample → 16kHz
            int srcRate = format.SampleRate;
            if (srcRate == TargetSampleRate)
                return mono;

            double ratio = (double)TargetSampleRate / srcRate;
            int targetLen = (int)(mono.Length * ratio);
            if (targetLen == 0) return Array.Empty<float>();

            var resampled = new float[targetLen];
            for (int i = 0; i < targetLen; i++)
            {
                double srcPos = i / ratio;
                int idx = (int)srcPos;
                double frac = srcPos - idx;

                if (idx + 1 < mono.Length)
                    resampled[i] = (float)(mono[idx] * (1.0 - frac) + mono[idx + 1] * frac);
                else if (idx < mono.Length)
                    resampled[i] = mono[idx];
            }

            return resampled;
        }

        public static float CalculateRms(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
                sum += samples[i] * (double)samples[i];
            return (float)Math.Sqrt(sum / samples.Length);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}