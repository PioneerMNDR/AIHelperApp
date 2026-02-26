using AIHelperApp.Models;
using AIHelperApp.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Net.Http;
using System.Text.Json;
namespace AIHelperApp.Controls
{
    public partial class InterviewControl : UserControl
    {
        // ═══ Services ═══
        private AudioCaptureService _audioService;
        private ScreenshotService _screenshotService;
        private VoiceActivityDetector _interviewerVad;
        private VoiceActivityDetector _userVad;
        private WhisperTranscriptionService _whisperService;
        private InterviewAiService _aiService;
        // ═══ Session ═══
        private InterviewSession _session;
        private readonly Stopwatch _sessionStopwatch = new();

        // ═══ Timers ═══
        private DispatcherTimer _vuMeterTimer;
        private DispatcherTimer _elapsedTimer;
        private DispatcherTimer _autoTriggerTimer;
        private string _currentViewingScreenshotPath;

        private bool _isInitialized;

        // ═══ Auto-trigger state ═══
        private bool _autoTriggerFired;
        private SpeakerType _lastSegmentSpeaker;
        private AiProviderSettings _aiSettings;
        // ═══ AI Request state ═══
        private CancellationTokenSource _aiCancellationSource;
        private bool _isAiRequestInProgress;

        // ═══ Brushes (cached) ═══
        private static readonly SolidColorBrush BrushGreen =
            new((Color)ColorConverter.ConvertFromString("#a6e3a1"));
        private static readonly SolidColorBrush BrushYellow =
            new((Color)ColorConverter.ConvertFromString("#f9e2af"));
        private static readonly SolidColorBrush BrushRed =
            new((Color)ColorConverter.ConvertFromString("#f38ba8"));
        private static readonly SolidColorBrush BrushGray =
            new((Color)ColorConverter.ConvertFromString("#6c7086"));
        private static readonly SolidColorBrush BrushSubtext =
            new((Color)ColorConverter.ConvertFromString("#a6adc8"));
        private static readonly SolidColorBrush BrushBlue =
            new((Color)ColorConverter.ConvertFromString("#89b4fa"));

        static InterviewControl()
        {
            BrushGreen.Freeze();
            BrushYellow.Freeze();
            BrushRed.Freeze();
            BrushGray.Freeze();
            BrushSubtext.Freeze();
            BrushBlue.Freeze();
        }

        public InterviewControl()
        {
            InitializeComponent();
            InitializeTimers();
            Loaded += OnLoaded;
        }

        // ═══════════════════════════════════════════
        //  INITIALIZATION
        // ═══════════════════════════════════════════

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Cleanup();
        }


        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            _audioService = new AudioCaptureService();
            _whisperService = new WhisperTranscriptionService();
            _screenshotService = new ScreenshotService();

            // ═══ Загрузка сохранённых настроек ═══
            _aiSettings = AiProviderSettings.Load();
            _aiService = new InterviewAiService(_aiSettings);

            _session = new InterviewSession();

            TranscriptionItemsControl.ItemsSource = _session.Segments;
            ResponsesItemsControl.ItemsSource = _session.Responses;

            _whisperService.TranscriptionCompleted += OnTranscriptionCompleted;
            _whisperService.StatusChanged += OnWhisperStatusChanged;

            _aiService.StatusChanged += OnAiStatusChanged;
            _aiService.ResponseReceived += OnAiResponseReceived;
            _aiService.ScreenshotDescribed += OnScreenshotDescribed;

            _screenshotService.CleanupOldScreenshots(7);
            EnumerateDevices();
            CheckWhisperModel();

            // ═══ Инициализация UI настроек провайдера ═══
            InitializeAiProviderUI();

            await _aiService.InitializeAsync();
        }

        private void InitializeAiProviderUI()
        {
            // Заполняем ComboBox моделей OpenRouter
            OpenRouterModelComboBox.Items.Clear();
            foreach (var (id, name) in OpenRouterModels.FreeModels)
            {
                OpenRouterModelComboBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            }

            // Устанавливаем сохранённые значения
            AiProviderComboBox.SelectedIndex = _aiSettings.IsOpenRouterProvider ? 1 : 0;
            QwenUrlTextBox.Text = _aiSettings.QwenApiUrl;
            OpenRouterApiKeyBox.Password = _aiSettings.OpenRouterApiKey;

            // Выбираем сохранённую модель OpenRouter
            for (int i = 0; i < OpenRouterModelComboBox.Items.Count; i++)
            {
                if (OpenRouterModelComboBox.Items[i] is ComboBoxItem item &&
                    item.Tag?.ToString() == _aiSettings.OpenRouterTextModel)
                {
                    OpenRouterModelComboBox.SelectedIndex = i;
                    break;
                }
            }
            if (OpenRouterModelComboBox.SelectedIndex < 0)
                OpenRouterModelComboBox.SelectedIndex = 0;

            UpdateProviderUI();
            UpdateAiStatusDisplay();
        }
        private void AiProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;

            if (AiProviderComboBox.SelectedItem is ComboBoxItem item)
            {
                var providerTag = item.Tag?.ToString();
                _aiSettings.ProviderType = providerTag == "OpenRouter"
                    ? AiProviderType.OpenRouter
                    : AiProviderType.QwenFreeApi;

                UpdateProviderUI();
                ApplyAndSaveSettings();
            }
        }
        private void OpenRouterModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;

            if (OpenRouterModelComboBox.SelectedItem is ComboBoxItem modelItem)
            {
                var modelId = modelItem.Tag?.ToString();
                if (!string.IsNullOrEmpty(modelId))
                {
                    _aiSettings.OpenRouterTextModel = modelId;
                    ApplyAndSaveSettings();
                }
            }
        }

        private void UpdateProviderUI()
        {
            if (QwenSettingsPanel == null || OpenRouterSettingsPanel == null) return;

            if (_aiSettings.IsOpenRouterProvider)
            {
                QwenSettingsPanel.Visibility = Visibility.Collapsed;
                OpenRouterSettingsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                QwenSettingsPanel.Visibility = Visibility.Visible;
                OpenRouterSettingsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void OpenRouterApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            if (sender is Wpf.Ui.Controls.PasswordBox pb)
            {
                _aiSettings.OpenRouterApiKey = pb.Password;
                // Не сохраняем при каждом изменении символа, только при потере фокуса
            }
        }
        private void ApplyAndSaveSettings()
        {
            // Обновляем URL Qwen
            _aiSettings.QwenApiUrl = QwenUrlTextBox.Text;

            // Применяем к сервису (это также сохраняет настройки)
            _aiService?.UpdateSettings(_aiSettings);

            UpdateAiStatusDisplay();
        }

        private void UpdateAiStatusDisplay()
        {
            if (_aiSettings.IsOpenRouterProvider)
            {
                var modelName = "OpenRouter";
                if (OpenRouterModelComboBox.SelectedItem is ComboBoxItem item)
                {
                    modelName = item.Content?.ToString() ?? "OpenRouter";
                }
                QwenStatusRun.Text = $" {modelName}";
                QwenStatusRun.Foreground = _aiSettings.HasOpenRouterApiKey ? BrushBlue : BrushYellow;
            }
            else
            {
                QwenStatusRun.Text = " Qwen";
                QwenStatusRun.Foreground = BrushBlue;
            }
        }
  

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            // Сначала сохраняем текущие настройки
            ApplyAndSaveSettings();

            var button = sender as Wpf.Ui.Controls.Button;
            var originalContent = button.Content;
            button.Content = "...";
            button.IsEnabled = false;

            try
            {
                if (_aiSettings.IsOpenRouterProvider)
                {
                    if (!_aiSettings.HasOpenRouterApiKey)
                    {
                        MessageBox.Show("Введите API ключ OpenRouter", "Ошибка",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_aiSettings.OpenRouterApiKey}");
                    client.Timeout = TimeSpan.FromSeconds(10);

                    var response = await client.GetAsync("https://openrouter.ai/api/v1/models");

                    if (response.IsSuccessStatusCode)
                    {
                        MessageBox.Show("✅ Подключение к OpenRouter успешно!", "Успех",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        QwenStatusRun.Text = " OpenRouter ✓";
                        QwenStatusRun.Foreground = BrushGreen;
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        MessageBox.Show($"❌ Ошибка: {response.StatusCode}\n{error}", "Ошибка",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        QwenStatusRun.Foreground = BrushRed;
                    }
                }
                else
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await client.GetAsync($"{_aiSettings.QwenApiUrl.TrimEnd('/')}/api/models");

                    if (response.IsSuccessStatusCode)
                    {
                        MessageBox.Show("✅ Подключение к Qwen API успешно!", "Успех",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        QwenStatusRun.Text = " Qwen ✓";
                        QwenStatusRun.Foreground = BrushGreen;
                    }
                    else
                    {
                        MessageBox.Show($"❌ Ошибка: {response.StatusCode}", "Ошибка",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        QwenStatusRun.Foreground = BrushRed;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Ошибка подключения:\n{ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                QwenStatusRun.Text = _aiSettings.IsOpenRouterProvider ? " OpenRouter ✗" : " Qwen ✗";
                QwenStatusRun.Foreground = BrushRed;
            }
            finally
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }

        // Опционально: сохранение/загрузка настроек
        private AiProviderSettings LoadAiSettings()
        {
            try
            {
                var settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIHelperApp",
                    "ai_settings.json");

                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    return JsonSerializer.Deserialize<AiProviderSettings>(json) ?? new AiProviderSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Failed to load: {ex.Message}");
            }

            return new AiProviderSettings();
        }

        private void SaveAiSettings()
        {
            try
            {
                var settingsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIHelperApp");

                Directory.CreateDirectory(settingsDir);

                var settingsPath = Path.Combine(settingsDir, "ai_settings.json");
                var json = JsonSerializer.Serialize(_aiSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsPath, json);

                Debug.WriteLine("[Settings] AI settings saved");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Failed to save: {ex.Message}");
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }


        private void Cleanup()
        {
            // Stop AI requests
            _aiCancellationSource?.Cancel();
            _aiCancellationSource?.Dispose();

            StopAllTimers();

            try
            {
                if (_audioService != null)
                {
                    _audioService.LoopbackSamplesAvailable -= OnLoopbackSamples;
                    _audioService.MicSamplesAvailable -= OnMicSamples;
                }
            }
            catch { }

            _audioService?.Dispose();
            _audioService = null;
            // Сохраняем API ключ если он был изменён
            if (_aiSettings != null && OpenRouterApiKeyBox != null)
            {
                _aiSettings.OpenRouterApiKey = OpenRouterApiKeyBox.Password;
                _aiSettings.Save();
            }
            if (_interviewerVad != null)
            {
                _interviewerVad.SpeechSegmentCompleted -= OnInterviewerSegmentCompleted;
                _interviewerVad = null;
            }

            if (_userVad != null)
            {
                _userVad.SpeechSegmentCompleted -= OnUserSegmentCompleted;
                _userVad = null;
            }

            if (_whisperService != null)
            {
                _whisperService.TranscriptionCompleted -= OnTranscriptionCompleted;
                _whisperService.StatusChanged -= OnWhisperStatusChanged;
                _whisperService.Dispose();
                _whisperService = null;
            }

            // AI service cleanup ← НОВОЕ
            if (_aiService != null)
            {
                _aiService.StatusChanged -= OnAiStatusChanged;
                _aiService.ResponseReceived -= OnAiResponseReceived;
                _aiService.ScreenshotDescribed -= OnScreenshotDescribed;
                _aiService.Dispose();
                _aiService = null;
            }
            SaveAiSettings();
            _screenshotService?.Dispose();
            _screenshotService = null;
        }

        private void InitializeTimers()
        {
            _vuMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _vuMeterTimer.Tick += VuMeterTimer_Tick;

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += ElapsedTimer_Tick;

            _autoTriggerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _autoTriggerTimer.Tick += AutoTriggerTimer_Tick;
        }

        private void StopAllTimers()
        {
            _vuMeterTimer?.Stop();
            _elapsedTimer?.Stop();
            _autoTriggerTimer?.Stop();
        }

        // ═══════════════════════════════════════════
        //  DEVICE ENUMERATION
        // ═══════════════════════════════════════════

        private void EnumerateDevices()
        {
            try
            {
                var loopbackDevices = _audioService.GetLoopbackDevices();
                LoopbackComboBox.Items.Clear();
                foreach (var dev in loopbackDevices)
                    LoopbackComboBox.Items.Add(dev);
                if (loopbackDevices.Any())
                {
                    int idx = loopbackDevices.FindIndex(d => d.IsDefault);
                    LoopbackComboBox.SelectedIndex = idx >= 0 ? idx : 0;
                }

                var micDevices = _audioService.GetMicDevices();
                MicComboBox.Items.Clear();
                foreach (var dev in micDevices)
                    MicComboBox.Items.Add(dev);
                if (micDevices.Any())
                {
                    int idx = micDevices.FindIndex(d => d.IsDefault);
                    MicComboBox.SelectedIndex = idx >= 0 ? idx : 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Interview] Device enumeration error: {ex.Message}");
            }
        }

        private void RefreshDevices_Click(object sender, RoutedEventArgs e) => EnumerateDevices();

        // ═══════════════════════════════════════════
        //  WHISPER MODEL
        // ═══════════════════════════════════════════

        private void CheckWhisperModel()
        {
            if (WhisperTranscriptionService.IsModelDownloaded)
            {
                ModelDownloadOverlay.Visibility = Visibility.Collapsed;
                InitializeWhisper();
            }
            else
            {
                ModelDownloadOverlay.Visibility = Visibility.Visible;
                WhisperStatusRun.Text = "нет модели";
                WhisperStatusRun.Foreground = BrushRed;
            }
        }

        private void InitializeWhisper()
        {
            try
            {
                var langItem = LanguageComboBox.SelectedItem as ComboBoxItem;
                var language = langItem?.Tag?.ToString() ?? "auto";

                _whisperService.Initialize(language);
                WhisperStatusRun.Text = "готов";
                WhisperStatusRun.Foreground = BrushGreen;
            }
            catch (Exception ex)
            {
                WhisperStatusRun.Text = "ошибка";
                WhisperStatusRun.Foreground = BrushRed;
                Debug.WriteLine($"[Whisper] Init error: {ex.Message}");
                MessageBox.Show($"Ошибка инициализации Whisper:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DownloadModel_Click(object sender, RoutedEventArgs e)
        {
            DownloadModelButton.IsEnabled = false;
            DownloadProgressPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text = "Начинаю скачивание...";

            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                {
                    double percent = (double)p.downloaded / p.total * 100;
                    ModelDownloadProgress.Value = percent;
                    double mb = p.downloaded / 1048576.0;
                    double totalMb = p.total / 1048576.0;
                    DownloadStatusText.Text = $"Скачивание: {mb:F1} / {totalMb:F1} МБ ({percent:F0}%)";
                }
                else
                {
                    double mb = p.downloaded / 1048576.0;
                    DownloadStatusText.Text = $"Скачивание: {mb:F1} МБ...";
                }
            });

            try
            {
                await _whisperService.DownloadModelAsync(progress);
                DownloadStatusText.Text = "✅ Модель скачана!";
                await System.Threading.Tasks.Task.Delay(1000);
                ModelDownloadOverlay.Visibility = Visibility.Collapsed;
                InitializeWhisper();
            }
            catch (Exception ex)
            {
                DownloadStatusText.Text = $"❌ Ошибка: {ex.Message}";
                DownloadModelButton.IsEnabled = true;
            }
        }

        // ═══════════════════════════════════════════
        //  RECORDING CONTROLS (button handlers)
        // ═══════════════════════════════════════════

        private void RecButton_Click(object sender, RoutedEventArgs e)
        {
            if (_session.IsRecording && _session.IsPaused)
            {
                ResumeRecording();
                return;
            }
            if (_session.IsRecording) return;
            StartRecording();
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_session.IsRecording) return;
            if (_session.IsPaused)
                ResumeRecording();
            else
                PauseRecording();
        }
        private async void AnswerButton_Click(object sender, RoutedEventArgs e)
        {
            await TriggerAiResponseAsync();
        }
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_session.IsRecording && !_session.IsPaused) return;
            StopRecording();
        }

        // ═══════════════════════════════════════════
        //  RECORDING LIFECYCLE
        // ═══════════════════════════════════════════

        private void StartRecording()
        {
            if (!_whisperService.IsInitialized)
            {
                MessageBox.Show("Whisper не инициализирован. Скачайте модель.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Save session settings
            _session.Role = RoleTextBox.Text;
            _session.TechStack = TechStackTextBox.Text;
            _session.Language = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            _session.StartedAt = DateTime.Now;
            _session.Segments.Clear();
            _session.Responses.Clear(); // ← НОВОЕ


  

            _session.LastAiTriggerTime = null;
            _session.LastProcessedSegmentIndex = -1;
            _aiService?.ResetForNewSession();

            ResponseCountRun.Text = "";
            NoResponsesPlaceholder.Visibility = Visibility.Visible;
            // Create VAD instances
            _interviewerVad = new VoiceActivityDetector();
            _userVad = new VoiceActivityDetector();
            _interviewerVad.SpeechSegmentCompleted += OnInterviewerSegmentCompleted;
            _userVad.SpeechSegmentCompleted += OnUserSegmentCompleted;

            // Start audio capture
            var loopbackId = (LoopbackComboBox.SelectedItem as AudioDeviceInfo)?.Id;
            var micId = (MicComboBox.SelectedItem as AudioDeviceInfo)?.Id;

            _audioService.LoopbackSamplesAvailable += OnLoopbackSamples;
            _audioService.MicSamplesAvailable += OnMicSamples;
            _audioService.Start(loopbackId, micId);

            // Start Whisper queue processor
            _whisperService.StartProcessing();

            // Start timers
            _sessionStopwatch.Restart();
            _vuMeterTimer.Start();
            _elapsedTimer.Start();
            _autoTriggerTimer.Start();

            // Сброс счётчика скриншотов
            _screenshotService?.ResetSessionCounter();

            // State
            _session.IsRecording = true;
            _session.IsPaused = false;
            _autoTriggerFired = false;
            _lastSegmentSpeaker = SpeakerType.System;

            // UI
            UpdateRecordingUI();
            CollapseSettings_Click(null, null);
            AddSystemSegment("🟢 Запись начата");
        }


        private void PauseRecording()
        {
            // ═══ FIX: Проверка на null перед отпиской ═══
            if (_audioService != null)
            {
                _audioService.LoopbackSamplesAvailable -= OnLoopbackSamples;
                _audioService.MicSamplesAvailable -= OnMicSamples;
                _audioService.Stop();
            }

            // Flush pending VAD segments
            _interviewerVad?.Flush();
            _userVad?.Flush();

            // Stop timers
            _sessionStopwatch.Stop();
            _vuMeterTimer.Stop();
            _autoTriggerTimer.Stop();

            // Reset VU bars
            InterviewerVuBar.Width = 0;
            UserVuBar.Width = 0;

            // State
            _session.IsPaused = true;

            // UI
            UpdateRecordingUI();
            AddSystemSegment("⏸ Пауза");
        }

        private void ResumeRecording()
        {
            // Restart audio capture
            var loopbackId = (LoopbackComboBox.SelectedItem as AudioDeviceInfo)?.Id;
            var micId = (MicComboBox.SelectedItem as AudioDeviceInfo)?.Id;

            _audioService.LoopbackSamplesAvailable += OnLoopbackSamples;
            _audioService.MicSamplesAvailable += OnMicSamples;
            _audioService.Start(loopbackId, micId);

            // Resume timers
            _sessionStopwatch.Start();
            _vuMeterTimer.Start();
            _autoTriggerTimer.Start();

            // State
            _session.IsPaused = false;
            _autoTriggerFired = false;

            // UI
            UpdateRecordingUI();
            AddSystemSegment("▶ Продолжено");
        }

        private void StopRecording()
        {
            _aiCancellationSource?.Cancel();
            // ═══ FIX: Проверка на null перед отпиской ═══
            if (_audioService != null)
            {
                _audioService.LoopbackSamplesAvailable -= OnLoopbackSamples;
                _audioService.MicSamplesAvailable -= OnMicSamples;
                _audioService.Stop();
            }

            // Flush remaining VAD buffers
            _interviewerVad?.Flush();
            _userVad?.Flush();

            // Detach VAD handlers
            if (_interviewerVad != null)
            {
                _interviewerVad.SpeechSegmentCompleted -= OnInterviewerSegmentCompleted;
                _interviewerVad = null;
            }
            if (_userVad != null)
            {
                _userVad.SpeechSegmentCompleted -= OnUserSegmentCompleted;
                _userVad = null;
            }

            // Stop timers
            _sessionStopwatch.Stop();
            StopAllTimers();

            // Reset VU bars
            InterviewerVuBar.Width = 0;
            UserVuBar.Width = 0;

            // State
            _session.IsRecording = false;
            _session.IsPaused = false;

            // UI
            UpdateRecordingUI();
            AddSystemSegment("⏹ Запись остановлена");

            Debug.WriteLine($"[Interview] Session stopped. " +
                $"Duration: {_session.ElapsedFormatted}, Segments: {_session.Segments.Count}");
        }

        // ═══════════════════════════════════════════
        //  AUDIO → VAD PIPELINE
        // ═══════════════════════════════════════════

        private void OnLoopbackSamples(float[] samples)
        {
            _interviewerVad?.ProcessSamples(samples);
        }

        private void OnMicSamples(float[] samples)
        {
            _userVad?.ProcessSamples(samples);
        }
        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            TakeScreenshot(sendToAI: false);
        }


        // ═══════════════════════════════════════════
        //  SCREENSHOT WITH AI
        // ═══════════════════════════════════════════

        /// <summary>
        /// Делает скриншот и добавляет в транскрипцию
        /// </summary>
        public void TakeScreenshot(bool sendToAI = false)
        {
            if (_screenshotService == null)
            {
                Debug.WriteLine("[Screenshot] Service not initialized");
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
            {
                await Task.Delay(50);

                var result = _screenshotService.CaptureScreen();

                if (!result.Success)
                {
                    AddSystemSegment($"❌ Ошибка скриншота: {result.ErrorMessage}");
                    return;
                }

                var segment = new TranscriptSegment
                {
                    Speaker = SpeakerType.System,
                    Type = SegmentType.Screenshot,
                    Text = "",
                    StartTime = _sessionStopwatch.Elapsed,
                    EndTime = _sessionStopwatch.Elapsed,
                    Timestamp = result.CapturedAt,
                    ScreenshotPath = result.FilePath,
                    ScreenshotBase64 = result.Base64,
                    ScreenshotThumbnail = result.Thumbnail,
                    ScreenshotWidth = result.Width,
                    ScreenshotHeight = result.Height,
                    ScreenshotNumber = result.ScreenshotNumber
                };

                _session.Segments.Add(segment);

                int totalCount = _session.Segments.Count;
                int screenshotCount = _session.Segments.Count(s => s.IsScreenshot);
                SegmentCountRun.Text = $" ({totalCount} | 📷{screenshotCount})";

                ScrollToBottom();
                FlashButton(ScreenshotButton);

                Debug.WriteLine($"[Screenshot] #{result.ScreenshotNumber} captured: " +
                    $"{result.Width}x{result.Height}, {result.SizeMB:F2} MB");

                if (sendToAI)
                {
                    // Сразу запрашиваем описание и ответ от AI
                    await TriggerAiResponseAsync();
                }
                else
                {
                    // Запрашиваем описание скриншота в фоне (кэширование)
                    _ = _aiService?.DescribeScreenshotAsync(segment);
                }
            });
        }

        // ═══════════════════════════════════════════
        //  VAD → WHISPER PIPELINE
        // ═══════════════════════════════════════════

        private void OnInterviewerSegmentCompleted(float[] audio, TimeSpan startTime, TimeSpan duration)
        {
            _lastSegmentSpeaker = SpeakerType.Interviewer;
            _autoTriggerFired = false;

            _whisperService.EnqueueSegment(new AudioSegmentData
            {
                Samples = audio,
                Speaker = SpeakerType.Interviewer,
                StartTime = startTime,
                Duration = duration
            });

            Dispatcher.BeginInvoke(() =>
            {
                TranscribingIndicator.Visibility = Visibility.Visible;
                _session.IsProcessing = true;
            });
        }

        private void OnUserSegmentCompleted(float[] audio, TimeSpan startTime, TimeSpan duration)
        {
            _lastSegmentSpeaker = SpeakerType.User;
            _autoTriggerFired = false;

            _whisperService.EnqueueSegment(new AudioSegmentData
            {
                Samples = audio,
                Speaker = SpeakerType.User,
                StartTime = startTime,
                Duration = duration
            });

            Dispatcher.BeginInvoke(() =>
            {
                TranscribingIndicator.Visibility = Visibility.Visible;
                _session.IsProcessing = true;
            });
        }

        // ═══════════════════════════════════════════
        //  WHISPER → UI PIPELINE
        // ═══════════════════════════════════════════

        private void OnTranscriptionCompleted(TranscriptSegment segment)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _session.Segments.Add(segment);

                int speechCount = _session.Segments.Count(s => s.IsSpeech);
                SegmentCountRun.Text = $" ({speechCount})";

                bool stillProcessing = _whisperService.QueueCount > 0;
                TranscribingIndicator.Visibility = stillProcessing
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _session.IsProcessing = stillProcessing;

                ScrollToBottom();

                Debug.WriteLine($"[Transcription] [{segment.TimeCode}] " +
                    $"{segment.SpeakerLabel}: {segment.Text}");
            });
        }

        private void OnWhisperStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(() =>
            {
                WhisperStatusRun.Text = status;

                if (status.Contains("ошибка"))
                    WhisperStatusRun.Foreground = BrushRed;
                else if (status.Contains("транскрибирую") || status.Contains("очередь"))
                    WhisperStatusRun.Foreground = BrushYellow;
                else
                    WhisperStatusRun.Foreground = BrushGreen;

                _session.WhisperQueueCount = _whisperService.QueueCount;
            });
        }

        // ═══════════════════════════════════════════
        //  TIMER TICKS
        // ═══════════════════════════════════════════

        private void VuMeterTimer_Tick(object sender, EventArgs e)
        {
            if (_audioService == null || !_audioService.IsCapturing) return;

            double loopbackLevel = Math.Min(1.0, _audioService.LoopbackRmsLevel * 10.0);
            double interviewerWidth = InterviewerVuContainer.ActualWidth * loopbackLevel;
            InterviewerVuBar.Width = Math.Max(0, interviewerWidth);

            double micLevel = Math.Min(1.0, _audioService.MicRmsLevel * 10.0);
            double userWidth = UserVuContainer.ActualWidth * micLevel;
            UserVuBar.Width = Math.Max(0, userWidth);
        }

        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            if (!_sessionStopwatch.IsRunning) return;

            _session.Elapsed = _sessionStopwatch.Elapsed;
            ElapsedRun.Text = _session.ElapsedFormatted;
        }

        private async void AutoTriggerTimer_Tick(object sender, EventArgs e)
        {
            if (!_session.IsRecording || _session.IsPaused) return;
            if (AutoTriggerCheckBox.IsChecked != true) return;
            if (_autoTriggerFired) return;
            if (_isAiRequestInProgress) return; // ← НОВОЕ
            if (_session.Segments.Count == 0) return;

            if (_lastSegmentSpeaker != SpeakerType.Interviewer) return;

            int thresholdSec = GetSilenceThreshold();

            double interviewerSilence = _interviewerVad?.GetSilenceDurationSeconds() ?? 0;
            double userSilence = _userVad?.GetSilenceDurationSeconds() ?? 0;

            if (interviewerSilence >= thresholdSec && userSilence >= thresholdSec)
            {
                _autoTriggerFired = true;

                Debug.WriteLine($"[AutoTrigger] Silence detected " +
                    $"(interviewer: {interviewerSilence:F1}s, user: {userSilence:F1}s). " +
                    $"Threshold: {thresholdSec}s. Triggering AI...");

                // Запускаем AI запрос ← НОВОЕ
                await TriggerAiResponseAsync();
            }
        }

        // ═══════════════════════════════════════════
        //  UI HELPERS
        // ═══════════════════════════════════════════

        private void UpdateRecordingUI()
        {
            if (_session.IsRecording && !_session.IsPaused)
            {
                RecButton.Content = "● REC";
                RecButton.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#40a6e3a1"));
                RecButton.IsEnabled = false;

                PauseButton.Content = "⏸ Пауза";
                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                ScreenshotButton.IsEnabled = true;
                AnswerButton.IsEnabled = true;

                StatusDot.Fill = BrushGreen;
                RecordingStatusText.Text = "Запись";
                RecordingStatusText.Foreground = BrushGreen;
            }
            else if (_session.IsRecording && _session.IsPaused)
            {
                RecButton.Content = "▶ Продолжить";
                RecButton.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#40a6e3a1"));
                RecButton.IsEnabled = true;

                PauseButton.Content = "▶ Снять паузу";
                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                ScreenshotButton.IsEnabled = false;
                AnswerButton.IsEnabled = true;

                StatusDot.Fill = BrushYellow;
                RecordingStatusText.Text = "Пауза";
                RecordingStatusText.Foreground = BrushYellow;
            }
            else
            {
                RecButton.Content = "● REC";
                RecButton.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#40a6e3a1"));
                RecButton.IsEnabled = true;

                PauseButton.Content = "⏸ Пауза";
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                ScreenshotButton.IsEnabled = false;
                AnswerButton.IsEnabled = false;

                StatusDot.Fill = BrushGray;
                RecordingStatusText.Text = "Готов к записи";
                RecordingStatusText.Foreground = BrushSubtext;

                ElapsedRun.Text = "00:00";
            }
        }
        /// <summary>
        /// Кратковременная подсветка кнопки как обратная связь
        /// </summary>
        private async void FlashButton(Wpf.Ui.Controls.Button button)
        {
            var originalBackground = button.Background;
            button.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#4089b4fa"));

            await System.Threading.Tasks.Task.Delay(200);

            button.Background = originalBackground;
        }
        private void AddSystemSegment(string text)
        {
            var segment = new TranscriptSegment
            {
                Speaker = SpeakerType.System,
                Type = SegmentType.Speech,
                Text = text,
                StartTime = _sessionStopwatch.Elapsed,
                EndTime = _sessionStopwatch.Elapsed,
                Timestamp = DateTime.Now
            };

            _session.Segments.Add(segment);

            int speechCount = _session.Segments.Count(s => s.IsSpeech);
            SegmentCountRun.Text = $" ({speechCount})";

            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                TranscriptionScrollViewer.ScrollToEnd();
            });
        }

        private int GetSilenceThreshold()
        {
            if (SilenceThresholdComboBox.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int val))
                return val;
            return 2;
        }



        // ═══════════════════════════════════════════
        //  AI SERVICE EVENTS
        // ═══════════════════════════════════════════

        private void OnAiStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _session.QwenStatus = status;
                QwenStatusRun.Text = $" {status}";

                // Цвет в зависимости от статуса
                if (status.Contains("ошибка") || status.Contains("отменён"))
                {
                    QwenStatusRun.Foreground = BrushRed;
                    GeneratingIndicator.Visibility = Visibility.Collapsed;
                }
                else if (status == "rate limit")
                {
                    QwenStatusRun.Foreground = BrushYellow;
                    QwenStatusRun.Text = " ⏳ rate limit";
                    GeneratingIndicator.Visibility = Visibility.Collapsed;
                }
                else if (status.Contains("генерирую") || status.Contains("повтор"))
                {
                    QwenStatusRun.Foreground = BrushYellow;
                    GeneratingIndicator.Visibility = Visibility.Visible;
                    _session.IsGenerating = true;
                }
                else if (status.Contains("ожидание"))
                {
                    QwenStatusRun.Foreground = BrushYellow;
                    GeneratingIndicator.Visibility = Visibility.Visible;
                }
                else if (status == "готов")
                {
                    QwenStatusRun.Foreground = BrushGreen;
                    GeneratingIndicator.Visibility = Visibility.Collapsed;
                    _session.IsGenerating = false;
                }
                else
                {
                    QwenStatusRun.Foreground = BrushSubtext;
                    GeneratingIndicator.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void OnAiResponseReceived(AiResponse response)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Снимаем флаг "latest" с предыдущих ответов
                foreach (var oldResponse in _session.Responses)
                {
                    oldResponse.IsLatest = false;
                }

                // Новый ответ - latest
                response.IsLatest = true;
                _session.Responses.Add(response);

                // Обновляем счётчик
                ResponseCountRun.Text = $" ({_session.Responses.Count})";

                // Скрываем плейсхолдер
                NoResponsesPlaceholder.Visibility = Visibility.Collapsed;

                // Скролл вниз
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    ResponsesScrollViewer.ScrollToEnd();
                });

                Debug.WriteLine($"[Interview] AI response received: {response.DetectedQuestion}");
            });
        }

        private void OnScreenshotDescribed(TranscriptSegment segment, string description)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Segment.Text уже обновлён в сервисе, но для UI обновления
                // нужно вызвать PropertyChanged - это происходит автоматически
                Debug.WriteLine($"[Interview] Screenshot #{segment.ScreenshotNumber} described");
            });
        }

        // ═══════════════════════════════════════════
        //  AI TRIGGER
        // ═══════════════════════════════════════════

        /// <summary>
        /// Запускает запрос к AI (вызывается по F2, кнопке или автотриггеру)
        /// </summary>
        public async Task TriggerAiResponseAsync()
        {
            if (_isAiRequestInProgress)
            {
                Debug.WriteLine("[Interview] AI request already in progress, skipping");
                return;
            }

            if (_session.Segments.Count == 0)
            {
                AddSystemSegment("⚠️ Нет сегментов для анализа");
                return;
            }

            var speechSegments = _session.Segments.Where(s => s.IsSpeech).ToList();
            if (speechSegments.Count == 0)
            {
                AddSystemSegment("⚠️ Нет речевых сегментов для анализа");
                return;
            }

            _isAiRequestInProgress = true;
            _aiCancellationSource?.Cancel();
            _aiCancellationSource = new CancellationTokenSource();

            try
            {
                FlashButton(AnswerButton);
                AnswerButton.IsEnabled = false;

                // ═══ УБРАЛИ: AddSystemSegment("🧠 Отправляю в AI..."); ═══
                // Вместо этого просто показываем индикатор в UI

                var response = await _aiService.GetAnswerAsync(_session, _aiCancellationSource.Token);

                if (response != null)
                {
                    if (string.IsNullOrEmpty(response.Answer) ||
                        response.Answer.StartsWith("⚠️"))
                    {
                        // Нет нового контента — добавляем как системное сообщение
                        AddSystemSegment(response.Answer ?? "⚠️ Нет ответа");
                    }
                    // Успешный ответ добавляется через событие OnAiResponseReceived
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[Interview] AI request cancelled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Interview] AI request failed: {ex.Message}");
                AddSystemSegment($"❌ Ошибка AI: {ex.Message}");
            }
            finally
            {
                _isAiRequestInProgress = false;

                Dispatcher.BeginInvoke(() =>
                {
                    AnswerButton.IsEnabled = _session.IsRecording || _session.IsPaused;
                });
            }
        }

        // ═══════════════════════════════════════════
        //  SETTINGS PANEL
        // ═══════════════════════════════════════════

        private void CollapseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void ExpandSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Visible;
        }
        // ═══════════════════════════════════════════
        //  SCREENSHOT VIEWER
        // ═══════════════════════════════════════════

        private void ScreenshotThumbnail_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string path)
            {
                OpenScreenshotViewer(path);
            }
            e.Handled = true;
        }

        private void OpenScreenshotViewer(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Debug.WriteLine($"[Screenshot] File not found: {path}");
                return;
            }

            _currentViewingScreenshotPath = path;

            var image = _screenshotService.LoadFullImage(path);
            if (image == null)
            {
                AddSystemSegment($"❌ Не удалось загрузить: {Path.GetFileName(path)}");
                return;
            }

            ScreenshotViewerImage.Source = image;
            ScreenshotViewerTitle.Text = $"📷 {Path.GetFileName(path)}";
            ScreenshotViewerInfo.Text = $"Разрешение: {image.PixelWidth}x{image.PixelHeight}";

            ScreenshotViewerOverlay.Visibility = Visibility.Visible;
        }

        private void CloseScreenshotViewer_Click(object sender, RoutedEventArgs e)
        {
            CloseScreenshotViewer();
        }

        private void ScreenshotViewerImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Клик на изображение не закрывает просмотрщик
            e.Handled = true;
        }

        private void CloseScreenshotViewer()
        {
            ScreenshotViewerOverlay.Visibility = Visibility.Collapsed;
            ScreenshotViewerImage.Source = null;
            _currentViewingScreenshotPath = null;
        }

        private void OpenScreenshotFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentViewingScreenshotPath) &&
                    File.Exists(_currentViewingScreenshotPath))
                {
                    // Открываем папку и выделяем файл
                    System.Diagnostics.Process.Start("explorer.exe",
                        $"/select,\"{_currentViewingScreenshotPath}\"");
                }
                else
                {
                    // Просто открываем папку скриншотов
                    System.Diagnostics.Process.Start("explorer.exe",
                        _screenshotService.ScreenshotsDirectory);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Failed to open folder: {ex.Message}");
            }
        }

        private void CopyScreenshotPath_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentViewingScreenshotPath))
            {
                try
                {
                    System.Windows.Clipboard.SetText(_currentViewingScreenshotPath);

                    // Визуальная обратная связь
                    var button = sender as Wpf.Ui.Controls.Button;
                    if (button != null)
                    {
                        var originalContent = button.Content;
                        button.Content = "✓ Скопировано";

                        Dispatcher.BeginInvoke(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(1500);
                            button.Content = originalContent;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Screenshot] Failed to copy path: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  KEYBOARD HANDLING (для Escape)
        // ═══════════════════════════════════════════

        // Добавьте обработчик PreviewKeyDown в UserControl:
        // В XAML: PreviewKeyDown="OnPreviewKeyDown"

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (ScreenshotViewerOverlay.Visibility == Visibility.Visible)
                {
                    CloseScreenshotViewer();
                    e.Handled = true;
                }
            }
        }
    }
}