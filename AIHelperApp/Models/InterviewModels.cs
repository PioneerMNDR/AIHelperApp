// Models/InterviewModels.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace AIHelperApp.Models
{
    public enum SpeakerType
    {
        Interviewer,
        User,
        System
    }

    public enum SegmentType
    {
        Speech,
        Screenshot
    }

    /// <summary>
    /// Сегмент транскрипции (речь или скриншот)
    /// </summary>
    public class TranscriptSegment : INotifyPropertyChanged
    {
        private string _text = "";
        private bool _isQuestion;
        private string _screenshotPath;
        private string _screenshotBase64;
        private BitmapImage _screenshotThumbnail;
        /// <summary>
        /// ID ответа AI, в котором этот сегмент был обработан (null = ещё не обработан)
        /// </summary>
        public string ProcessedInResponseId { get; set; }
        /// <summary>
        /// Был ли сегмент уже обработан AI
        /// </summary>
        public bool IsProcessedByAi => !string.IsNullOrEmpty(ProcessedInResponseId);

        public SpeakerType Speaker { get; set; }
        public SegmentType Type { get; set; }

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDescription));
            }
        }

        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public DateTime Timestamp { get; set; }

        public bool IsQuestion
        {
            get => _isQuestion;
            set { _isQuestion = value; OnPropertyChanged(); }
        }

        // ═══ Скриншоты ═══

        public string ScreenshotPath
        {
            get => _screenshotPath;
            set
            {
                _screenshotPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ScreenshotFileName));
            }
        }

        public string ScreenshotBase64
        {
            get => _screenshotBase64;
            set { _screenshotBase64 = value; OnPropertyChanged(); }
        }

        public BitmapImage ScreenshotThumbnail
        {
            get => _screenshotThumbnail;
            set { _screenshotThumbnail = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Ширина скриншота в пикселях
        /// </summary>
        public int ScreenshotWidth { get; set; }

        /// <summary>
        /// Высота скриншота в пикселях
        /// </summary>
        public int ScreenshotHeight { get; set; }

        /// <summary>
        /// Порядковый номер скриншота в сессии
        /// </summary>
        public int ScreenshotNumber { get; set; }

        // ═══ Вычисляемые свойства ═══

        public bool IsScreenshot => Type == SegmentType.Screenshot;
        public bool IsSpeech => Type == SegmentType.Speech;

        /// <summary>
        /// Есть ли описание скриншота (от AI)
        /// </summary>
        public bool HasDescription => IsScreenshot && !string.IsNullOrWhiteSpace(Text);

        public string TimeCode => StartTime.ToString(@"mm\:ss");

        public string SpeakerIcon => Speaker switch
        {
            SpeakerType.Interviewer => "🎤",
            SpeakerType.User => "🙋",
            SpeakerType.System => IsScreenshot ? "📷" : "⚙️",
            _ => "❓"
        };

        public string SpeakerLabel => Speaker switch
        {
            SpeakerType.Interviewer => "Интервьюер",
            SpeakerType.User => "Вы",
            SpeakerType.System => IsScreenshot ? $"Скриншот #{ScreenshotNumber}" : "Система",
            _ => ""
        };

        /// <summary>
        /// Имя файла скриншота (без пути)
        /// </summary>
        public string ScreenshotFileName => string.IsNullOrEmpty(ScreenshotPath)
            ? ""
            : Path.GetFileName(ScreenshotPath);

        /// <summary>
        /// Информация о размере скриншота
        /// </summary>
        public string ScreenshotResolution => ScreenshotWidth > 0 && ScreenshotHeight > 0
            ? $"{ScreenshotWidth}×{ScreenshotHeight}"
            : "";

        // ═══ INotifyPropertyChanged ═══

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Ответ AI на вопрос интервьюера
    /// </summary>
    public class AiResponse : INotifyPropertyChanged
    {
        private string _detectedQuestion = "";
        private string _answer = "";
        private bool _isStreaming;
        private string _keyWords = "";
        private bool _isLatest;

        public string DetectedQuestion
        {
            get => _detectedQuestion;
            set { _detectedQuestion = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasQuestion)); }
        }

        public string Answer
        {
            get => _answer;
            set
            {
                _answer = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnswerBody));
            }
        }

        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Форматированное время для отображения
        /// </summary>
        public string TimestampFormatted => Timestamp.ToString("HH:mm:ss");

        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Ключевые слова/термины из ответа
        /// </summary>
        public string KeyWords
        {
            get => _keyWords;
            set { _keyWords = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasKeyWords)); }
        }

        /// <summary>
        /// Это последний (самый свежий) ответ
        /// </summary>
        public bool IsLatest
        {
            get => _isLatest;
            set { _isLatest = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Пути к скриншотам, которые были включены в запрос
        /// </summary>
        public List<string> IncludedScreenshots { get; set; } = new();

        /// <summary>
        /// Количество токенов в ответе (приблизительно)
        /// </summary>
        public int TokenCount { get; set; }

        // ═══ Вычисляемые свойства для UI ═══

        public bool HasQuestion => !string.IsNullOrWhiteSpace(DetectedQuestion);
        public bool HasKeyWords => !string.IsNullOrWhiteSpace(KeyWords);
        public bool HasScreenshots => IncludedScreenshots?.Count > 0;
        public int IncludedScreenshotsCount => IncludedScreenshots?.Count ?? 0;

        /// <summary>
        /// Тело ответа без секции вопроса и ключевых слов
        /// </summary>
        public string AnswerBody
        {
            get
            {
                if (string.IsNullOrEmpty(Answer))
                    return "";

                var result = Answer;

                // Убираем строку с вопросом
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"❓\s*[Вв]опрос:.*?[\r\n]+",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                // Убираем строку с ключевыми словами
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"💡\s*[Кк]лючевые слова:.*?($|[\r\n])",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                // Убираем "✅ Ответ:" если есть
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"✅\s*[Оо]твет:\s*",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                return result.Trim();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Сессия интервью
    /// </summary>
    public class InterviewSession : INotifyPropertyChanged
    {
        private bool _isRecording;
        private bool _isPaused;
        private bool _isProcessing;
        private bool _isGenerating;
        private TimeSpan _elapsed;
        private string _whisperStatus = "не загружен";
        private int _whisperQueueCount;
        private string _qwenStatus = "—";
        /// <summary>
        /// Время последнего триггера AI (для разделения контекста)
        /// </summary>
        public DateTime? LastAiTriggerTime { get; set; }
        /// <summary>
        /// Индекс последнего обработанного сегмента
        /// </summary>
        public int LastProcessedSegmentIndex { get; set; } = -1;
        public ObservableCollection<TranscriptSegment> Segments { get; set; } = new();
        public ObservableCollection<AiResponse> Responses { get; set; } = new();
        public DateTime StartedAt { get; set; }

        // ═══ Состояние записи ═══

        public bool IsRecording
        {
            get => _isRecording;
            set { _isRecording = value; OnPropertyChanged(); }
        }

        public bool IsPaused
        {
            get => _isPaused;
            set { _isPaused = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whisper обрабатывает аудио
        /// </summary>
        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Qwen генерирует ответ
        /// </summary>
        public bool IsGenerating
        {
            get => _isGenerating;
            set { _isGenerating = value; OnPropertyChanged(); }
        }

        public TimeSpan Elapsed
        {
            get => _elapsed;
            set
            {
                _elapsed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ElapsedFormatted));
            }
        }

        public string ElapsedFormatted => Elapsed.ToString(@"mm\:ss");

        // ═══ Статусы сервисов ═══

        public string WhisperStatus
        {
            get => _whisperStatus;
            set { _whisperStatus = value; OnPropertyChanged(); }
        }

        public int WhisperQueueCount
        {
            get => _whisperQueueCount;
            set { _whisperQueueCount = value; OnPropertyChanged(); }
        }

        public string QwenStatus
        {
            get => _qwenStatus;
            set { _qwenStatus = value; OnPropertyChanged(); }
        }

        // ═══ Настройки сессии ═══

        /// <summary>
        /// Роль кандидата (Backend Developer, etc.)
        /// </summary>
        public string Role { get; set; } = "Backend Developer";

        /// <summary>
        /// Технический стек
        /// </summary>
        public string TechStack { get; set; } = "C#, .NET, Docker";

        /// <summary>
        /// Язык транскрипции (ru, en, auto)
        /// </summary>
        public string Language { get; set; } = "ru";

        /// <summary>
        /// Автоматический триггер AI при тишине
        /// </summary>
        public bool AutoTriggerEnabled { get; set; } = true;

        /// <summary>
        /// Порог тишины для автотриггера (секунды)
        /// </summary>
        public int SilenceThresholdSeconds { get; set; } = 2;

        /// <summary>
        /// Автоматические скриншоты
        /// </summary>
        public bool AutoScreenshotEnabled { get; set; } = false;

        /// <summary>
        /// Интервал автоскриншотов (секунды)
        /// </summary>
        public int AutoScreenshotIntervalSeconds { get; set; } = 30;

        // ═══ Статистика сессии ═══

        /// <summary>
        /// Количество сегментов речи
        /// </summary>
        public int SpeechSegmentCount => Segments?.Count ?? 0;

        /// <summary>
        /// Количество скриншотов
        /// </summary>
        public int ScreenshotCount
        {
            get
            {
                int count = 0;
                if (Segments != null)
                {
                    foreach (var s in Segments)
                        if (s.IsScreenshot) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// Количество ответов AI
        /// </summary>
        public int ResponseCount => Responses?.Count ?? 0;

        // ═══ INotifyPropertyChanged ═══

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Обновляет все вычисляемые свойства статистики
        /// </summary>
        public void RefreshStatistics()
        {
            OnPropertyChanged(nameof(SpeechSegmentCount));
            OnPropertyChanged(nameof(ScreenshotCount));
            OnPropertyChanged(nameof(ResponseCount));
        }
    }

    /// <summary>
    /// Данные аудио-сегмента для передачи в Whisper
    /// </summary>
    public class AudioSegmentData
    {
        public float[] Samples { get; set; }
        public SpeakerType Speaker { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Приоритет обработки (интервьюер выше)
        /// </summary>
        public int Priority => Speaker == SpeakerType.Interviewer ? 0 : 1;
    }

    /// <summary>
    /// Информация об аудио-устройстве
    /// </summary>
    public class AudioDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>
        /// Тип устройства (для отображения)
        /// </summary>
        public string DeviceType { get; set; }

        public override string ToString() => IsDefault ? $"⭐ {Name}" : Name;
    }
}