// Models/AiProviderSettings.cs
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHelperApp.Models
{
    public enum AiProviderType
    {
        QwenFreeApi,
        OpenRouter
    }

    /// <summary>
    /// Настройки AI провайдера
    /// </summary>
    public class AiProviderSettings : INotifyPropertyChanged
    {
        private AiProviderType _providerType = AiProviderType.QwenFreeApi;
        private string _qwenApiUrl = "http://localhost:3264";
        private string _qwenTextModel = "qwen3.5-flash";
        private string _qwenVisionModel = "qwen3.5-flash";
        private string _openRouterApiKey = "";
        private string _openRouterTextModel = "google/gemma-3-27b-it:free";

        // ═══ Путь к файлу настроек ═══
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIHelperApp");

        private static readonly string SettingsFilePath = Path.Combine(
            SettingsDirectory,
            "ai_provider_settings.json");

        public AiProviderType ProviderType
        {
            get => _providerType;
            set { _providerType = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsQwenProvider)); OnPropertyChanged(nameof(IsOpenRouterProvider)); }
        }

        // ═══ Qwen Free API ═══

        public string QwenApiUrl
        {
            get => _qwenApiUrl;
            set { _qwenApiUrl = value; OnPropertyChanged(); }
        }

        public string QwenTextModel
        {
            get => _qwenTextModel;
            set { _qwenTextModel = value; OnPropertyChanged(); }
        }

        public string QwenVisionModel
        {
            get => _qwenVisionModel;
            set { _qwenVisionModel = value; OnPropertyChanged(); }
        }

        // ═══ OpenRouter ═══

        public string OpenRouterApiKey
        {
            get => _openRouterApiKey;
            set { _openRouterApiKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOpenRouterApiKey)); }
        }

        public string OpenRouterTextModel
        {
            get => _openRouterTextModel;
            set { _openRouterTextModel = value; OnPropertyChanged(); }
        }

        // ═══ Вычисляемые свойства ═══

        [JsonIgnore]
        public bool IsQwenProvider => ProviderType == AiProviderType.QwenFreeApi;

        [JsonIgnore]
        public bool IsOpenRouterProvider => ProviderType == AiProviderType.OpenRouter;

        [JsonIgnore]
        public bool HasOpenRouterApiKey => !string.IsNullOrWhiteSpace(OpenRouterApiKey);

        /// <summary>
        /// OpenRouter НЕ поддерживает мультимодальность для бесплатных моделей
        /// </summary>
        [JsonIgnore]
        public bool SupportsVision => IsQwenProvider;

        /// <summary>
        /// Текущая модель для текста
        /// </summary>
        [JsonIgnore]
        public string CurrentTextModel => ProviderType switch
        {
            AiProviderType.QwenFreeApi => QwenTextModel,
            AiProviderType.OpenRouter => OpenRouterTextModel,
            _ => QwenTextModel
        };

        /// <summary>
        /// Текущая модель для vision (только Qwen)
        /// </summary>
        [JsonIgnore]
        public string CurrentVisionModel => ProviderType switch
        {
            AiProviderType.QwenFreeApi => QwenVisionModel,
            _ => null // OpenRouter не поддерживает vision для бесплатных моделей
        };

        /// <summary>
        /// Название провайдера для UI
        /// </summary>
        [JsonIgnore]
        public string ProviderDisplayName => ProviderType switch
        {
            AiProviderType.QwenFreeApi => "Qwen Free API",
            AiProviderType.OpenRouter => "OpenRouter",
            _ => "Unknown"
        };

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Создаёт копию настроек
        /// </summary>
        public AiProviderSettings Clone()
        {
            return new AiProviderSettings
            {
                ProviderType = ProviderType,
                QwenApiUrl = QwenApiUrl,
                QwenTextModel = QwenTextModel,
                QwenVisionModel = QwenVisionModel,
                OpenRouterApiKey = OpenRouterApiKey,
                OpenRouterTextModel = OpenRouterTextModel
            };
        }

        // ═══ Сохранение / Загрузка ═══

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Сохраняет настройки в файл
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                var json = JsonSerializer.Serialize(this, JsonOptions);
                File.WriteAllText(SettingsFilePath, json);
                System.Diagnostics.Debug.WriteLine($"[AiSettings] Saved to {SettingsFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiSettings] Failed to save: {ex.Message}");
            }
        }

        /// <summary>
        /// Загружает настройки из файла
        /// </summary>
        public static AiProviderSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AiProviderSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AiSettings] Loaded from {SettingsFilePath}");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiSettings] Failed to load: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[AiSettings] Using default settings");
            return new AiProviderSettings();
        }

        /// <summary>
        /// Путь к папке настроек
        /// </summary>
        public static string GetSettingsDirectory() => SettingsDirectory;
    }

    /// <summary>
    /// Бесплатные модели OpenRouter (только текст, без vision)
    /// </summary>
    public static class OpenRouterModels
    {
        // ═══ Бесплатные текстовые модели ═══
        public const string Gemma3_27B = "google/gemma-3-27b-it:free";
        public const string Qwen3Coder = "qwen/qwen3-coder:free";
        public const string GLM4_5Air = "z-ai/glm-4.5-air:free";
        public const string Qwen3Next80B = "qwen/qwen3-next-80b-a3b-instruct:free";
        public const string TrinityLarge = "arcee-ai/trinity-large-preview:free";
        public const string Step35Flash = "stepfun/step-3.5-flash:free";

        /// <summary>
        /// Список бесплатных моделей для ComboBox
        /// </summary>
        public static readonly (string Id, string Name)[] FreeModels = new[]
        {
            (Gemma3_27B, "Gemma 3 27B"),
            (Qwen3Coder, "Qwen 3 Coder"),
            (GLM4_5Air, "GLM 4.5 Air"),
            (Qwen3Next80B, "Qwen 3 Next 80B"),
            (TrinityLarge, "Trinity Large Preview"),
            (Step35Flash, "Step 3.5 Flash"),
        };

        /// <summary>
        /// Модель по умолчанию
        /// </summary>
        public const string Default = Gemma3_27B;
    }
}