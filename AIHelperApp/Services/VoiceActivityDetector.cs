using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace AIHelperApp.Services
{
    public class VoiceActivityDetector
    {
        private const int SampleRate = AudioCaptureService.TargetSampleRate;

        // === Настройки ===
        public float SpeechThreshold { get; set; } = 0.015f;
        public float SpeechThresholdLow { get; set; } = 0.008f;
        public int SilenceDurationMs { get; set; } = 600;         // 🔧 Увеличил (было 500)
        public int MinSegmentDurationMs { get; set; } = 300;
        public int MaxSegmentDurationMs { get; set; } = 30000;

        public int MinSpeechOnsetMs { get; set; } = 80;
        public float MinZcr { get; set; } = 0.02f;
        public float MaxZcr { get; set; } = 0.45f;
        public int EnergyHistoryFrames { get; set; } = 4;

        // 🆕 Настройки для плавного окончания
        public int TrailingSilenceMs { get; set; } = 150;         // Сколько тишины ОСТАВИТЬ в конце
        public int HangoverMs { get; set; } = 200;                // "Инерция" - продолжаем считать речью
        public bool ApplyFadeOut { get; set; } = true;            // Плавное затухание
        public int FadeOutMs { get; set; } = 50;                  // Длительность затухания
        public int LeadingPaddingMs { get; set; } = 50;           // Добавить тишины В НАЧАЛО

        // DEBUG
        public bool DebugPlaybackEnabled { get; set; } = false;
        public bool DebugSaveToFile { get; set; } = false;
        public string DebugOutputFolder { get; set; } = Path.Combine(Path.GetTempPath(), "VAD_Debug");

        private int _debugSegmentCounter = 0;
        private WaveOutEvent _waveOut;

        // === Состояние ===
        private readonly List<float> _segmentBuffer = new();
        private readonly List<float> _preBuffer = new();
        private readonly Queue<float> _energyHistory = new();
        private readonly Queue<float[]> _leadingBuffer = new();   // 🆕 Буфер для начала

        private bool _isSpeaking;
        private bool _speechConfirmed;
        private int _silenceSampleCount;
        private int _speechOnsetSamples;
        private int _hangoverSamples;                             // 🆕 Счётчик инерции
        private long _totalSamplesProcessed;
        private long _segmentStartSample;
        private long _lastSpeechSample;

        private readonly object _lock = new();

        public event Action<float[], TimeSpan, TimeSpan> SpeechSegmentCompleted;
        public event Action<string> DebugLog;

        public bool IsSpeaking
        {
            get { lock (_lock) return _speechConfirmed; }
        }

        public long LastSpeechSample
        {
            get { lock (_lock) return _lastSpeechSample; }
        }

        private int LeadingPaddingSamples => LeadingPaddingMs * SampleRate / 1000;
        private int HangoverSamples => HangoverMs * SampleRate / 1000;

        public void Reset()
        {
            lock (_lock)
            {
                _segmentBuffer.Clear();
                _preBuffer.Clear();
                _energyHistory.Clear();
                _leadingBuffer.Clear();
                _isSpeaking = false;
                _speechConfirmed = false;
                _silenceSampleCount = 0;
                _speechOnsetSamples = 0;
                _hangoverSamples = 0;
                _totalSamplesProcessed = 0;
                _segmentStartSample = 0;
                _lastSpeechSample = 0;
            }

            StopDebugPlayback();
        }

        public void ProcessSamples(float[] samples)
        {
            if (samples == null || samples.Length == 0) return;

            float rms = CalculateRms(samples);
            float zcr = CalculateZeroCrossingRate(samples);

            lock (_lock)
            {
                // 🆕 Сохраняем последние N мс для добавления в начало
                UpdateLeadingBuffer(samples);

                _energyHistory.Enqueue(rms);
                while (_energyHistory.Count > EnergyHistoryFrames)
                    _energyHistory.Dequeue();

                bool isSpeechLike = IsSpeechLikeSignal(rms, zcr);

                // 🆕 Hangover логика - инерция после речи
                if (isSpeechLike)
                {
                    _hangoverSamples = HangoverSamples;
                }
                else if (_hangoverSamples > 0)
                {
                    _hangoverSamples -= samples.Length;
                    if (_hangoverSamples > 0)
                    {
                        // Ещё в режиме инерции - считаем как речь
                        isSpeechLike = true;
                    }
                }

                if (isSpeechLike)
                {
                    _lastSpeechSample = _totalSamplesProcessed;

                    if (!_isSpeaking)
                    {
                        _isSpeaking = true;
                        _speechOnsetSamples = 0;
                        _segmentStartSample = _totalSamplesProcessed;
                        _preBuffer.Clear();

                        // 🆕 Добавляем leading padding из буфера
                        AddLeadingPadding();

                        Log($"[VAD] Potential speech start | RMS={rms:F4} ZCR={zcr:F3}");
                    }

                    _silenceSampleCount = 0;
                    _speechOnsetSamples += samples.Length;

                    if (!_speechConfirmed)
                    {
                        _preBuffer.AddRange(samples);

                        int onsetMs = _speechOnsetSamples * 1000 / SampleRate;

                        if (onsetMs >= MinSpeechOnsetMs && IsEnergyStable())
                        {
                            _speechConfirmed = true;
                            _segmentBuffer.AddRange(_preBuffer);
                            _preBuffer.Clear();

                            Log($"[VAD] Speech CONFIRMED after {onsetMs}ms");
                        }
                    }
                    else
                    {
                        _segmentBuffer.AddRange(samples);
                    }
                }
                else
                {
                    if (_isSpeaking && !_speechConfirmed)
                    {
                        int onsetMs = _speechOnsetSamples * 1000 / SampleRate;
                        Log($"[VAD] REJECTED as click/noise after {onsetMs}ms | RMS={rms:F4} ZCR={zcr:F3}");

                        if (DebugPlaybackEnabled && _preBuffer.Count > 0)
                        {
                            var rejected = _preBuffer.ToArray();
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                                DebugPlayAudio(rejected, "REJECTED"));
                        }

                        _isSpeaking = false;
                        _preBuffer.Clear();
                        _speechOnsetSamples = 0;
                    }
                    else if (_speechConfirmed)
                    {
                        _segmentBuffer.AddRange(samples);
                        _silenceSampleCount += samples.Length;

                        int silenceMs = _silenceSampleCount * 1000 / SampleRate;
                        int segmentMs = _segmentBuffer.Count * 1000 / SampleRate;

                        if (silenceMs >= SilenceDurationMs || segmentMs >= MaxSegmentDurationMs)
                        {
                            FinalizeSegment();
                        }
                    }
                }

                _totalSamplesProcessed += samples.Length;
            }
        }

        /// <summary>
        /// Обновляет кольцевой буфер для leading padding
        /// </summary>
        private void UpdateLeadingBuffer(float[] samples)
        {
            _leadingBuffer.Enqueue((float[])samples.Clone());

            int totalSamples = 0;
            foreach (var chunk in _leadingBuffer)
                totalSamples += chunk.Length;

            while (totalSamples > LeadingPaddingSamples && _leadingBuffer.Count > 1)
            {
                var removed = _leadingBuffer.Dequeue();
                totalSamples -= removed.Length;
            }
        }

        /// <summary>
        /// Добавляет аудио ДО начала речи (чтобы не терять первый звук)
        /// </summary>
        private void AddLeadingPadding()
        {
            foreach (var chunk in _leadingBuffer)
            {
                _segmentBuffer.AddRange(chunk);
            }
        }

        private bool IsSpeechLikeSignal(float rms, float zcr)
        {
            float threshold = _speechConfirmed ? SpeechThresholdLow : SpeechThreshold;

            if (rms < threshold)
                return false;

            if (zcr < MinZcr || zcr > MaxZcr)
                return false;

            return true;
        }

        private bool IsEnergyStable()
        {
            if (_energyHistory.Count < 2)
                return true;

            var energies = new List<float>(_energyHistory);

            float mean = 0;
            foreach (var e in energies) mean += e;
            mean /= energies.Count;

            float variance = 0;
            foreach (var e in energies)
                variance += (e - mean) * (e - mean);
            variance /= energies.Count;

            float cv = mean > 0.001f ? (float)Math.Sqrt(variance) / mean : 0;

            return cv < 1.5f;
        }

        private static float CalculateZeroCrossingRate(float[] samples)
        {
            if (samples.Length < 2) return 0;

            int crossings = 0;
            for (int i = 1; i < samples.Length; i++)
            {
                if ((samples[i] >= 0 && samples[i - 1] < 0) ||
                    (samples[i] < 0 && samples[i - 1] >= 0))
                {
                    crossings++;
                }
            }

            return (float)crossings / (samples.Length - 1);
        }

        private static float CalculateRms(float[] samples)
        {
            if (samples.Length == 0) return 0;

            double sum = 0;
            foreach (var s in samples)
                sum += s * s;

            return (float)Math.Sqrt(sum / samples.Length);
        }

        public void Flush()
        {
            lock (_lock)
            {
                if (_speechConfirmed && _segmentBuffer.Count > 0)
                    FinalizeSegment();

                _preBuffer.Clear();
                _leadingBuffer.Clear();
                _isSpeaking = false;
                _speechConfirmed = false;
            }
        }

        private void FinalizeSegment()
        {
            _isSpeaking = false;
            _speechConfirmed = false;
            _silenceSampleCount = 0;
            _speechOnsetSamples = 0;
            _hangoverSamples = 0;

            int segmentMs = _segmentBuffer.Count * 1000 / SampleRate;

            if (segmentMs < MinSegmentDurationMs || _segmentBuffer.Count == 0)
            {
                Log($"[VAD] Segment too short: {segmentMs}ms, discarding");
                _segmentBuffer.Clear();
                return;
            }

            // 🔧 Изменённая логика обрезки
            // Оставляем TrailingSilenceMs тишины в конце вместо полного удаления
            int totalSilenceSamples = SilenceDurationMs * SampleRate / 1000;
            int keepSilenceSamples = TrailingSilenceMs * SampleRate / 1000;
            int trimSamples = Math.Max(0, totalSilenceSamples - keepSilenceSamples);

            int trimmedLength = Math.Max(_segmentBuffer.Count - trimSamples, SampleRate / 10);
            var audio = new float[trimmedLength];
            _segmentBuffer.CopyTo(0, audio, 0, trimmedLength);
            _segmentBuffer.Clear();

            // 🆕 Применяем fade out
            if (ApplyFadeOut)
            {
                ApplyFadeOutEffect(audio);
            }

            var startTime = TimeSpan.FromSeconds((double)_segmentStartSample / SampleRate);
            var duration = TimeSpan.FromSeconds((double)audio.Length / SampleRate);

            Log($"[VAD] Segment COMPLETE: {duration.TotalMilliseconds:F0}ms (kept {TrailingSilenceMs}ms trailing)");

            if (DebugPlaybackEnabled || DebugSaveToFile)
            {
                var audioCopy = (float[])audio.Clone();
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    DebugPlayAudio(audioCopy, "ACCEPTED"));
            }

            var handler = SpeechSegmentCompleted;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                handler?.Invoke(audio, startTime, duration);
            });
        }

        /// <summary>
        /// Плавное затухание в конце
        /// </summary>
        private void ApplyFadeOutEffect(float[] audio)
        {
            int fadeSamples = Math.Min(FadeOutMs * SampleRate / 1000, audio.Length / 2);
            int fadeStart = audio.Length - fadeSamples;

            for (int i = 0; i < fadeSamples; i++)
            {
                // Косинусное затухание (плавнее линейного)
                float t = (float)i / fadeSamples;
                float multiplier = (float)(Math.Cos(t * Math.PI / 2));
                // Или линейное: float multiplier = 1f - t;

                audio[fadeStart + i] *= multiplier;
            }
        }

        public double GetSilenceDurationSeconds()
        {
            lock (_lock)
            {
                if (_speechConfirmed) return 0;
                long silenceSamples = _totalSamplesProcessed - _lastSpeechSample;
                return (double)silenceSamples / SampleRate;
            }
        }

        // ==================== DEBUG METHODS ====================

        private void Log(string message)
        {
            DebugLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }

        private void DebugPlayAudio(float[] audio, string label)
        {
            try
            {
                _debugSegmentCounter++;
                int durationMs = audio.Length * 1000 / SampleRate;

                Log($"[DEBUG] {label} segment #{_debugSegmentCounter}: {durationMs}ms");

                if (DebugSaveToFile)
                {
                    SaveDebugWav(audio, label);
                }

                if (DebugPlaybackEnabled)
                {
                    PlayAudioSync(audio);
                }
            }
            catch (Exception ex)
            {
                Log($"[DEBUG] Playback error: {ex.Message}");
            }
        }

        private void PlayAudioSync(float[] audio)
        {
            try
            {
                StopDebugPlayback();

                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
                var provider = new RawSourceWaveStream(
                    FloatArrayToStream(audio),
                    waveFormat);

                _waveOut = new WaveOutEvent();
                _waveOut.Init(provider);
                _waveOut.Play();

                while (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                Log($"[DEBUG] Play error: {ex.Message}");
            }
            finally
            {
                StopDebugPlayback();
            }
        }

        private void SaveDebugWav(float[] audio, string label)
        {
            try
            {
                if (!Directory.Exists(DebugOutputFolder))
                    Directory.CreateDirectory(DebugOutputFolder);

                string filename = $"{DateTime.Now:HHmmss_fff}_{label}_{_debugSegmentCounter}.wav";
                string path = Path.Combine(DebugOutputFolder, filename);

                using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
                writer.WriteSamples(audio, 0, audio.Length);

                Log($"[DEBUG] Saved: {path}");
            }
            catch (Exception ex)
            {
                Log($"[DEBUG] Save error: {ex.Message}");
            }
        }

        private MemoryStream FloatArrayToStream(float[] audio)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.Default, leaveOpen: true))
            {
                foreach (var sample in audio)
                {
                    writer.Write(sample);
                }
            }
            stream.Position = 0;
            return stream;
        }

        private void StopDebugPlayback()
        {
            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
            }
            catch { }
        }

        public void DebugPlaySamples(float[] samples)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ => PlayAudioSync(samples));
        }
    }
}