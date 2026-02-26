// Services/FileUploadService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelperApp.Services
{
    public class FileUploadResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("file")]
        public UploadedFileInfo File { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }
    }

    public class UploadedFileInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }
    }

    public class FileUploadService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        // Максимум 20 МБ
        public const long MaxFileSize = 20 * 1024 * 1024;

        // Поддерживаемые расширения
        private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".png", "image/png" },
            { ".gif", "image/gif" },
            { ".webp", "image/webp" },
            { ".bmp", "image/bmp" }
        };

        private static readonly Dictionary<string, string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".mp3", "audio/mpeg" },
            { ".wav", "audio/wav" },
            { ".ogg", "audio/ogg" },
            { ".m4a", "audio/mp4" },
            { ".flac", "audio/flac" },
            { ".aac", "audio/aac" },
            { ".wma", "audio/x-ms-wma" },
            { ".webm", "audio/webm" },
            { ".opus", "audio/opus" }
        };

        private static readonly Dictionary<string, string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".pdf", "application/pdf" },
            { ".doc", "application/msword" },
            { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { ".txt", "text/plain" }
        };

        public FileUploadService(string baseUrl = "http://localhost:3264")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }

        /// <summary>
        /// Определяет тип файла: image, audio, document, file
        /// </summary>
        public static string GetFileType(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            if (ImageExtensions.ContainsKey(ext)) return "image";
            if (AudioExtensions.ContainsKey(ext)) return "audio";
            if (DocumentExtensions.ContainsKey(ext)) return "document";
            return "file";
        }

        /// <summary>
        /// Проверяет, поддерживается ли файл
        /// </summary>
        public static (bool isValid, string error) ValidateFile(string filePath)
        {
            if (!File.Exists(filePath))
                return (false, "Файл не найден");

            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Length == 0)
                return (false, "Файл пуст");

            if (fileInfo.Length > MaxFileSize)
                return (false, $"Файл слишком большой ({fileInfo.Length / (1024.0 * 1024.0):F1} МБ). Максимум: {MaxFileSize / (1024 * 1024)} МБ");

            return (true, null);
        }

        /// <summary>
        /// Загружает файл на сервер
        /// </summary>
        public async Task<FileUploadResponse> UploadFileAsync(
            string filePath,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            var (isValid, error) = ValidateFile(filePath);
            if (!isValid)
            {
                return new FileUploadResponse { Success = false, Error = error };
            }

            try
            {
                var fileName = Path.GetFileName(filePath);
                var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);

                progress?.Report(0.3);

                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(fileBytes);

                // Определяем MIME-тип
                var ext = Path.GetExtension(filePath);
                string mimeType = "application/octet-stream";
                if (ImageExtensions.TryGetValue(ext, out var imgMime)) mimeType = imgMime;
                else if (AudioExtensions.TryGetValue(ext, out var audMime)) mimeType = audMime;
                else if (DocumentExtensions.TryGetValue(ext, out var docMime)) mimeType = docMime;

                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
                content.Add(fileContent, "file", fileName);

                progress?.Report(0.5);

                var response = await _httpClient.PostAsync(
                    $"{_baseUrl}/api/files/upload",
                    content,
                    cancellationToken);

                progress?.Report(0.9);

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

                System.Diagnostics.Debug.WriteLine($"📤 Upload response: {responseJson}");

                if (!response.IsSuccessStatusCode)
                {
                    return new FileUploadResponse
                    {
                        Success = false,
                        Error = $"HTTP {(int)response.StatusCode}: {responseJson}"
                    };
                }

                var result = JsonSerializer.Deserialize<FileUploadResponse>(responseJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                progress?.Report(1.0);

                return result ?? new FileUploadResponse { Success = false, Error = "Пустой ответ от сервера" };
            }
            catch (OperationCanceledException)
            {
                return new FileUploadResponse { Success = false, Error = "Загрузка отменена" };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Upload error: {ex.Message}");
                return new FileUploadResponse
                {
                    Success = false,
                    Error = $"Ошибка загрузки: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Загружает несколько файлов
        /// </summary>
        public async Task<List<FileUploadResponse>> UploadFilesAsync(
            IEnumerable<string> filePaths,
            IProgress<(int current, int total, double fileProgress)> progress = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<FileUploadResponse>();
            var files = new List<string>(filePaths);
            var total = files.Count;

            for (int i = 0; i < total; i++)
            {
                var current = i + 1;
                var fileProgress = new Progress<double>(p =>
                {
                    progress?.Report((current, total, p));
                });

                var result = await UploadFileAsync(files[i], fileProgress, cancellationToken);
                results.Add(result);

                if (cancellationToken.IsCancellationRequested) break;
            }

            return results;
        }

        /// <summary>
        /// Формирует фильтр для OpenFileDialog
        /// </summary>
        public static string GetFileDialogFilter()
        {
            return "Все поддерживаемые|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.mp3;*.wav;*.ogg;*.m4a;*.flac;*.aac;*.wma;*.webm;*.opus;*.pdf;*.doc;*.docx;*.txt" +
                   "|Изображения|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp" +
                   "|Аудио|*.mp3;*.wav;*.ogg;*.m4a;*.flac;*.aac;*.wma;*.webm;*.opus" +
                   "|Документы|*.pdf;*.doc;*.docx;*.txt" +
                   "|Все файлы|*.*";
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}