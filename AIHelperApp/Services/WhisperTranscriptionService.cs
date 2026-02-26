using AIHelperApp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace AIHelperApp.Services
{
    public class WhisperTranscriptionService : IDisposable
    {
        private const string ModelFileName = "ggml-base.bin";
        private const string ModelUrl =
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";

        private WhisperFactory _factory;
        private WhisperProcessor _processor;
        private Channel<AudioSegmentData> _queue;
        private CancellationTokenSource _cts;
        private Task _processingTask;
        private int _queueCount;
        private bool _isDisposed;

        public event Action<TranscriptSegment> TranscriptionCompleted;
        public event Action<string> StatusChanged;

        public bool IsInitialized { get; private set; }
        public int QueueCount => _queueCount;

        // ═══════════════════════════════════════════
        //  BAN WORD LIST — фильтр мусорных фраз
        // ═══════════════════════════════════════════

        /// <summary>
        /// Если результат СОДЕРЖИТ любое из этих слов (case-insensitive) → отбрасываем
        /// </summary>
        private static readonly List<string> BannedContains = new List<string>
        {
            "Егорина",
            "Егорова",
            "DimaTorzok",
            // сюда можно добавлять ещё
        };

        /// <summary>
        /// Если результат ТОЧНО совпадает с одной из этих строк (case-insensitive, trim) → отбрасываем
        /// </summary>
        private static readonly List<string> BannedExact = new List<string>
        {
            "Продолжение следует...",
            // сюда можно добавлять ещё
        };

        /// <summary>
        /// Возвращает true, если текст нужно отбросить
        /// </summary>
        private static bool IsBanned(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var trimmed = text.Trim();

            // Точное совпадение
            foreach (var exact in BannedExact)
            {
                if (trimmed.Equals(exact, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Содержит запрещённое слово
            foreach (var word in BannedContains)
            {
                if (trimmed.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        // ═══════════════════════════════════════════
        //  CONSTRUCTOR
        // ═══════════════════════════════════════════

        public WhisperTranscriptionService()
        {
            // Канал создаётся в StartProcessing()
        }

        // ═══════════════════════════════════════════
        //  MODEL PATH
        // ═══════════════════════════════════════════

        public static string ModelDirectory =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");

        public static string ModelPath =>
            Path.Combine(ModelDirectory, ModelFileName);

        public static bool IsModelDownloaded =>
            File.Exists(ModelPath);

        // ═══════════════════════════════════════════
        //  MODEL DOWNLOAD
        // ═══════════════════════════════════════════

        public async Task DownloadModelAsync(IProgress<(long downloaded, long total)> progress,
                                              CancellationToken ct = default)
        {
            Directory.CreateDirectory(ModelDirectory);
            var tempPath = ModelPath + ".tmp";

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(30);

                using var response = await client.GetAsync(ModelUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;

                using var downloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long bytesRead = 0;
                int read;

                while ((read = await downloadStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    bytesRead += read;
                    progress?.Report((bytesRead, totalBytes));
                }

                fileStream.Close();
                if (File.Exists(ModelPath))
                    File.Delete(ModelPath);
                File.Move(tempPath, ModelPath);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        // ═══════════════════════════════════════════
        //  INITIALIZATION
        // ═══════════════════════════════════════════

        public void Initialize(string language = "auto", bool useGpu = true)
        {
            if (!IsModelDownloaded)
                throw new FileNotFoundException($"Whisper model not found at {ModelPath}");

            // Очищаем предыдущие ресурсы
            CleanupResources();

            if (useGpu)
            {
                _factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions
                {
                    UseGpu = true,
                    UseFlashAttention = true
                });
            }
            else
            {
                _factory = WhisperFactory.FromPath(ModelPath);
            }

            var builder = _factory.CreateBuilder()
                .WithNoContext();

            var info = WhisperFactory.GetRuntimeInfo();
            System.Diagnostics.Debug.WriteLine(info);
            System.Diagnostics.Debug.WriteLine($"Loaded runtime: {RuntimeOptions.LoadedLibrary}");

            if (useGpu)
                builder.WithThreads(4);
            else
                builder.WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

            if (language != "auto")
                builder.WithLanguage(language);
            else
                builder.WithLanguageDetection();

            _processor = builder.Build();
            IsInitialized = true;

            var mode = useGpu ? "GPU (CUDA)" : "CPU";
            StatusChanged?.Invoke($"готов ({mode})");
            System.Diagnostics.Debug.WriteLine($"[Whisper] Initialized: {mode}, model: {ModelPath}");
        }

        // ═══════════════════════════════════════════
        //  START / STOP PROCESSING
        // ═══════════════════════════════════════════

        public void StartProcessing()
        {
            if (!IsInitialized)
            {
                System.Diagnostics.Debug.WriteLine("[Whisper] Cannot start - not initialized");
                return;
            }

            // Останавливаем предыдущую обработку если есть
            StopProcessing();

            // Создаём новый канал для каждой сессии
            _queue = Channel.CreateUnbounded<AudioSegmentData>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _queueCount = 0;
            _cts = new CancellationTokenSource();
            _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));

            System.Diagnostics.Debug.WriteLine("[Whisper] Processing started");
        }

        public void StopProcessing()
        {
            // 1. Завершаем канал - это позволит WaitToReadAsync вернуть false без исключения
            try
            {
                _queue?.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Whisper] Error completing channel: {ex.Message}");
            }

            // 2. Отменяем токен
            try
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Whisper] Error cancelling token: {ex.Message}");
            }

            // 3. Ждём завершения задачи
            if (_processingTask != null)
            {
                try
                {
                    // Используем Wait с таймаутом
                    if (!_processingTask.Wait(3000))
                    {
                        System.Diagnostics.Debug.WriteLine("[Whisper] Processing task didn't complete in time");
                    }
                }
                catch (AggregateException ae)
                {
                    // Разворачиваем и логируем внутренние исключения
                    foreach (var inner in ae.Flatten().InnerExceptions)
                    {
                        if (!(inner is OperationCanceledException))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Whisper] Task exception: {inner.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ожидаемое - игнорируем
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Whisper] Error waiting for task: {ex.Message}");
                }
            }

            // 4. Освобождаем ресурсы
            try
            {
                _cts?.Dispose();
            }
            catch { }

            _cts = null;
            _processingTask = null;
            _queue = null;
            _queueCount = 0;

            System.Diagnostics.Debug.WriteLine("[Whisper] Processing stopped");
        }

        // ═══════════════════════════════════════════
        //  ENQUEUE SEGMENT
        // ═══════════════════════════════════════════

        public void EnqueueSegment(AudioSegmentData segment)
        {
            if (!IsInitialized)
            {
                System.Diagnostics.Debug.WriteLine("[Whisper] Cannot enqueue - not initialized");
                return;
            }

            if (_queue == null)
            {
                System.Diagnostics.Debug.WriteLine("[Whisper] Cannot enqueue - queue is null");
                return;
            }

            if (segment?.Samples == null || segment.Samples.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("[Whisper] Cannot enqueue - empty segment");
                return;
            }

            if (_queue.Writer.TryWrite(segment))
            {
                Interlocked.Increment(ref _queueCount);
                StatusChanged?.Invoke($"очередь: {_queueCount}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Whisper] Failed to write to queue");
            }
        }

        // ═══════════════════════════════════════════
        //  QUEUE PROCESSOR
        // ═══════════════════════════════════════════

        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            System.Diagnostics.Debug.WriteLine("[Whisper] Queue processor started");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Ждём появления данных или завершения канала
                    bool hasData;
                    try
                    {
                        hasData = await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Токен отменён - выходим
                        System.Diagnostics.Debug.WriteLine("[Whisper] WaitToReadAsync cancelled");
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        // Канал закрыт - выходим
                        System.Diagnostics.Debug.WriteLine("[Whisper] Channel closed");
                        break;
                    }

                    if (!hasData)
                    {
                        // Канал завершён (Writer.Complete() вызван)
                        System.Diagnostics.Debug.WriteLine("[Whisper] No more data in channel");
                        break;
                    }

                    // Читаем все доступные элементы
                    while (_queue.Reader.TryRead(out var segment))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            System.Diagnostics.Debug.WriteLine("[Whisper] Cancellation requested during read");
                            break;
                        }

                        Interlocked.Decrement(ref _queueCount);
                        StatusChanged?.Invoke("транскрибирую...");

                        try
                        {
                            var text = await TranscribeAsync(segment.Samples, ct).ConfigureAwait(false);

                            // Фильтр: проверяем ban list
                            if (!string.IsNullOrWhiteSpace(text) && !IsBanned(text))
                            {
                                var result = new TranscriptSegment
                                {
                                    Speaker = segment.Speaker,
                                    Type = SegmentType.Speech,
                                    Text = text.Trim(),
                                    StartTime = segment.StartTime,
                                    EndTime = segment.StartTime + segment.Duration,
                                    Timestamp = DateTime.Now
                                };

                                TranscriptionCompleted?.Invoke(result);
                            }
                            else if (IsBanned(text))
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[Whisper] Banned phrase filtered: \"{text?.Trim()}\"");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            System.Diagnostics.Debug.WriteLine("[Whisper] Transcription cancelled");
                            break;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Whisper] Transcription error: {ex.Message}");
                        }

                        // Обновляем статус
                        var currentCount = _queueCount;
                        StatusChanged?.Invoke(currentCount > 0 ? $"очередь: {currentCount}" : "готов");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ожидаемое завершение
                System.Diagnostics.Debug.WriteLine("[Whisper] Queue processor cancelled");
            }
            catch (ChannelClosedException)
            {
                // Канал закрыт - ожидаемое завершение
                System.Diagnostics.Debug.WriteLine("[Whisper] Queue processor - channel closed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Whisper] Queue processing error: {ex.Message}");
                StatusChanged?.Invoke($"ошибка: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[Whisper] Queue processor stopped");
        }

        // ═══════════════════════════════════════════
        //  TRANSCRIPTION
        // ═══════════════════════════════════════════

        private async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
        {
            if (_processor == null)
            {
                return null;
            }

            var sb = new StringBuilder();

            try
            {
                await foreach (var seg in _processor.ProcessAsync(samples, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();

                    var text = seg.Text?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Whisper] ProcessAsync error: {ex.Message}");
                throw;
            }

            return sb.ToString();
        }

        // ═══════════════════════════════════════════
        //  CLEANUP
        // ═══════════════════════════════════════════

        private void CleanupResources()
        {
            StopProcessing();

            try { _processor?.Dispose(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Whisper] Error disposing processor: {ex.Message}");
            }

            try { _factory?.Dispose(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Whisper] Error disposing factory: {ex.Message}");
            }

            _processor = null;
            _factory = null;
            IsInitialized = false;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            CleanupResources();

            System.Diagnostics.Debug.WriteLine("[Whisper] Service disposed");
        }
    }
}