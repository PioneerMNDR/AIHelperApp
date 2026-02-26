using AIHelperApp.Chat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelperApp.Services
{
    public class LLMApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        private readonly List<HistoryMessage> _history = new();

        public string CurrentChatId { get; private set; }
        public string CurrentParentId { get; private set; }
        public string CurrentFid { get; private set; }
        public string SystemPrompt { get; set; }

        public LLMApiService(string baseUrl = "http://localhost:3264")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }

        private class HistoryMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public List<string> ImageUrls { get; set; }
            public List<string> AudioUrls { get; set; }
        }

        /// <summary>
        /// Строит массив messages с поддержкой изображений и аудио
        /// </summary>
        private List<object> BuildMessages(string newUserMessage, List<string> imageUrls = null, List<string> audioUrls = null)
        {
            var messages = new List<object>();

            if (!string.IsNullOrWhiteSpace(SystemPrompt))
            {
                messages.Add(new { role = "system", content = SystemPrompt });
            }

            // История
            foreach (var msg in _history)
            {
                bool hasMedia = msg.Role == "user" &&
                    ((msg.ImageUrls != null && msg.ImageUrls.Count > 0) ||
                     (msg.AudioUrls != null && msg.AudioUrls.Count > 0));

                if (hasMedia)
                {
                    var parts = new List<object>();
                    parts.Add(new { type = "text", text = msg.Content });

                    if (msg.ImageUrls != null)
                    {
                        foreach (var url in msg.ImageUrls)
                        {
                            parts.Add(new { type = "image", image = url });
                        }
                    }

                    if (msg.AudioUrls != null)
                    {
                        foreach (var url in msg.AudioUrls)
                        {
                            parts.Add(new { type = "audio", audio = url });
                        }
                    }

                    messages.Add(new { role = msg.Role, content = (object)parts });
                }
                else
                {
                    messages.Add(new { role = msg.Role, content = (object)msg.Content });
                }
            }

            // Новое сообщение
            bool newHasMedia = (imageUrls != null && imageUrls.Count > 0) ||
                               (audioUrls != null && audioUrls.Count > 0);

            if (newHasMedia)
            {
                var parts = new List<object>();
                if (!string.IsNullOrWhiteSpace(newUserMessage))
                {
                    parts.Add(new { type = "text", text = newUserMessage });
                }

                if (imageUrls != null)
                {
                    foreach (var url in imageUrls)
                    {
                        parts.Add(new { type = "image", image = url });
                    }
                }

                if (audioUrls != null)
                {
                    foreach (var url in audioUrls)
                    {
                        parts.Add(new { type = "audio", audio = url });
                    }
                }

                messages.Add(new { role = "user", content = (object)parts });
            }
            else
            {
                messages.Add(new { role = "user", content = (object)newUserMessage });
            }

            return messages;
        }

        /// <summary>
        /// Отправка сообщения с файлами (изображения + аудио)
        /// </summary>
        public async Task<ChatResponse> SendMessageWithFilesAsync(
            string message,
            List<string> imageUrls = null,
            List<string> audioUrls = null,
            string model = "qwen-max-latest",
            CancellationToken cancellationToken = default)
        {
            await EnsureChatExistsAsync(model, cancellationToken);

            var messages = BuildMessages(message, imageUrls, audioUrls);

            var requestBody = new
            {
                model = model,
                messages = messages,
                stream = false,
                chatId = CurrentChatId,
                parentId = CurrentParentId
            };

            var response = await PostAsync<ChatResponse>("/api/chat/completions", requestBody, cancellationToken);

            if (response != null)
            {
                var newParentId = response.GetParentId();
                if (string.IsNullOrEmpty(newParentId))
                    newParentId = response.GetResponseId();
                if (string.IsNullOrEmpty(newParentId) && !string.IsNullOrEmpty(response.Id))
                    newParentId = response.Id;
                if (!string.IsNullOrEmpty(newParentId))
                    CurrentParentId = newParentId;

                CurrentFid = response.Fid;

                // Сохраняем в историю с изображениями и аудио
                _history.Add(new HistoryMessage
                {
                    Role = "user",
                    Content = message ?? "",
                    ImageUrls = imageUrls,
                    AudioUrls = audioUrls
                });

                var assistantContent = response.Choices?.FirstOrDefault()?.Message?.Content;
                if (!string.IsNullOrEmpty(assistantContent))
                {
                    _history.Add(new HistoryMessage { Role = "assistant", Content = assistantContent });
                }
            }

            return response;
        }

        #region Chat Management

        public async Task<string> EnsureChatExistsAsync(
            string model = "qwen-max-latest",
            CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(CurrentChatId))
            {
                return CurrentChatId;
            }

            return await CreateChatAsync(null, model, cancellationToken);
        }

        public async Task<string> CreateChatAsync(
            string name = null,
            string model = "qwen-max-latest",
            CancellationToken cancellationToken = default)
        {
            var chatName = name ?? $"Chat_{DateTime.Now:yyyyMMdd_HHmmss}";
            var requestBody = new { name = chatName, model = model };

            try
            {
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_baseUrl}/api/chats",
                    content,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("chatId", out var chatIdElement))
                {
                    CurrentChatId = chatIdElement.GetString();
                    CurrentParentId = null;
                    CurrentFid = null;
                    _history.Clear();

                    System.Diagnostics.Debug.WriteLine($"✅ Чат создан: {CurrentChatId}");
                    return CurrentChatId;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка создания чата: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Send Message

        public async Task<ChatResponse> SendMessageAsync(
            string message,
            string model = "qwen-max-latest",
            CancellationToken cancellationToken = default)
        {
            return await SendMessageWithFilesAsync(message, null, null, model, cancellationToken);
        }

        #endregion

        #region Conversation Management

        public void ResetConversation()
        {
            CurrentChatId = null;
            CurrentParentId = null;
            CurrentFid = null;
            _history.Clear();
        }

        public void ResetAll()
        {
            ResetConversation();
            SystemPrompt = null;
        }

        public void SetSystemPrompt(string prompt)
        {
            SystemPrompt = prompt;
        }

        public int HistoryCount => _history.Count;

        public async Task<string> StartNewConversationAsync(
            string name = null,
            string model = "qwen-max-latest",
            CancellationToken cancellationToken = default)
        {
            ResetConversation();
            return await CreateChatAsync(name, model, cancellationToken);
        }

        #endregion

        #region Models & Status

        public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await GetAsync<ModelsResponse>("/api/models", cancellationToken);
                return response?.Data ?? new List<ModelInfo>();
            }
            catch
            {
                return new List<ModelInfo>();
            }
        }

        public async Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return await GetAsync<StatusResponse>("/api/status", cancellationToken);
        }

        #endregion

        #region HTTP Helpers

        private async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default)
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}{endpoint}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }

        private async Task<T> PostAsync<T>(string endpoint, object data, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            System.Diagnostics.Debug.WriteLine($"📤 Request: {json}");

            var response = await _httpClient.PostAsync($"{_baseUrl}{endpoint}", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"📥 Response: {responseJson}");

            return JsonSerializer.Deserialize<T>(responseJson, _jsonOptions);
        }

        #endregion

        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}