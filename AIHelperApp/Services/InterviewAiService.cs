// Services/InterviewAiService.cs
using AIHelperApp.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelperApp.Services
{
    public class InterviewAiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private AiProviderSettings _settings;

        // ═══ Qwen-specific: Chat Pool ═══
        private readonly ConcurrentBag<ChatSession> _chatPool = new();
        private readonly SemaphoreSlim _poolLock = new(1, 1);
        private bool _isInitialized;

        private const int MAX_CONCURRENT_REQUESTS = 3;
        private readonly SemaphoreSlim _requestSemaphore = new(MAX_CONCURRENT_REQUESTS, MAX_CONCURRENT_REQUESTS);

        private const int MAX_CONTEXT_MINUTES = 5;
        private const int MAX_CONTEXT_SEGMENTS = 30;
        private const int MAX_SCREENSHOTS_AS_IMAGE = 2;

        // ═══ OpenRouter ═══
        private const string OPENROUTER_BASE_URL = "https://openrouter.ai/api/v1";

        // ═══ Retry настройки ═══
        private const int MAX_RETRY_ATTEMPTS = 3;
        private static readonly int[] RETRY_DELAYS_MS = { 2000, 5000, 10000 }; // 2s, 5s, 10s

        // ═══ Rate limit tracking ═══
        private DateTime _lastRateLimitTime = DateTime.MinValue;
        private int _rateLimitHitCount = 0;

        // ═══ История сообщений для OpenRouter ═══
        private List<object> _openRouterMessageHistory = new();
        private const int MAX_HISTORY_MESSAGES = 20;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public event Action<string> StatusChanged;
        public event Action<AiResponse> ResponseReceived;
        public event Action<TranscriptSegment, string> ScreenshotDescribed;

        /// <summary>
        /// Текущий провайдер
        /// </summary>
        public AiProviderType CurrentProvider => _settings?.ProviderType ?? AiProviderType.QwenFreeApi;

        /// <summary>
        /// Поддерживает ли текущий провайдер изображения
        /// </summary>
        public bool SupportsVision => _settings?.SupportsVision ?? false;

        public InterviewAiService(AiProviderSettings settings = null)
        {
            _settings = settings ?? AiProviderSettings.Load();

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3)
            };
        }

        /// <summary>
        /// Обновляет настройки провайдера
        /// </summary>
        public void UpdateSettings(AiProviderSettings settings)
        {
            _settings = settings ?? new AiProviderSettings();

            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Remove("HTTP-Referer");
            _httpClient.DefaultRequestHeaders.Remove("X-Title");

            if (_settings.IsOpenRouterProvider && _settings.HasOpenRouterApiKey)
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.OpenRouterApiKey}");
                _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/AIHelperApp");
                _httpClient.DefaultRequestHeaders.Add("X-Title", "AI Interview Helper");
            }

            _settings.Save();

            // Сброс счётчика rate limit при смене настроек
            _rateLimitHitCount = 0;

            Debug.WriteLine($"[InterviewAI] Settings updated: Provider={_settings.ProviderType}, Model={_settings.CurrentTextModel}");
        }

        #region Initialization

        public Task InitializeAsync(CancellationToken ct = default)
        {
            _isInitialized = true;

            if (_settings.IsOpenRouterProvider && _settings.HasOpenRouterApiKey)
            {
                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.OpenRouterApiKey}");
                _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/AIHelperApp");
                _httpClient.DefaultRequestHeaders.Add("X-Title", "AI Interview Helper");
            }

            StatusChanged?.Invoke("готов");
            Debug.WriteLine($"[InterviewAI] Service initialized (Provider: {_settings.ProviderType})");
            return Task.CompletedTask;
        }

        public void ResetForNewSession()
        {
            var chatsToReset = new List<ChatSession>();
            while (_chatPool.TryTake(out var chat))
            {
                chat.ParentId = null;
                chatsToReset.Add(chat);
            }
            foreach (var chat in chatsToReset)
            {
                _chatPool.Add(chat);
            }

            _openRouterMessageHistory.Clear();
            _rateLimitHitCount = 0;

            Debug.WriteLine($"[InterviewAI] Reset for new session (Provider: {_settings.ProviderType})");
        }

        #endregion

        #region Main API

        public async Task<AiResponse> GetAnswerAsync(
            InterviewSession interviewSession,
            CancellationToken ct = default)
        {
            StatusChanged?.Invoke("генерирую...");

            var response = new AiResponse
            {
                Timestamp = DateTime.Now,
                IsStreaming = false
            };

            try
            {
                if (_settings.IsOpenRouterProvider)
                {
                    return await GetAnswerFromOpenRouterAsync(interviewSession, response, ct);
                }
                else
                {
                    return await GetAnswerFromQwenAsync(interviewSession, response, ct);
                }
            }
            catch (OperationCanceledException)
            {
                response.Answer = "⏹ Запрос отменён";
                StatusChanged?.Invoke("отменён");
            }
            catch (RateLimitException rle)
            {
                response.Answer = rle.UserFriendlyMessage;
                StatusChanged?.Invoke("rate limit");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InterviewAI] Error: {ex}");
                response.Answer = $"❌ Ошибка: {ex.Message}";
                StatusChanged?.Invoke("ошибка");
            }

            return response;
        }

        public async Task<string> DescribeScreenshotAsync(
            TranscriptSegment screenshotSegment,
            CancellationToken ct = default)
        {
            if (screenshotSegment == null || !screenshotSegment.IsScreenshot)
                return null;

            if (!string.IsNullOrWhiteSpace(screenshotSegment.Text))
                return screenshotSegment.Text;

            if (_settings.IsOpenRouterProvider)
            {
                Debug.WriteLine("[InterviewAI] OpenRouter: Vision not supported for free models");
                return null;
            }

            try
            {
                return await DescribeScreenshotQwenAsync(screenshotSegment, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InterviewAI] Failed to describe screenshot: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region OpenRouter Implementation

        private async Task<AiResponse> GetAnswerFromOpenRouterAsync(
            InterviewSession interviewSession,
            AiResponse response,
            CancellationToken ct)
        {
            await _requestSemaphore.WaitAsync(ct);

            try
            {
                var context = BuildContextWithHistory(interviewSession, includeImages: false);

                if (!context.HasNewContent)
                {
                    response.Answer = "⚠️ Нет новых вопросов от интервьюера";
                    response.DetectedQuestion = "";
                    StatusChanged?.Invoke("готов");
                    return response;
                }

                string model = _settings.OpenRouterTextModel;
                var messages = BuildOpenRouterMessages(context, interviewSession);

                // ═══ Retry логика ═══
                var (success, answerText, errorMessage) = await ExecuteOpenRouterRequestWithRetry(
                    model, messages, ct);

                if (!success)
                {
                    response.Answer = errorMessage;
                    StatusChanged?.Invoke("ошибка");
                    return response;
                }

                if (string.IsNullOrEmpty(answerText))
                {
                    response.Answer = "❌ Пустой ответ от OpenRouter";
                    StatusChanged?.Invoke("ошибка");
                    return response;
                }

                if (IsNoQuestionResponse(answerText))
                {
                    response.Answer = "⚠️ В новой информации нет вопроса";
                    response.DetectedQuestion = "";
                    StatusChanged?.Invoke("готов");
                    return response;
                }

                ParseResponse(answerText, response);
                AddToOpenRouterHistory("assistant", answerText);

                response.IncludedScreenshots = new List<string>();

                MarkSegmentsAsProcessed(context.NewSegments, Guid.NewGuid().ToString("N"));
                interviewSession.LastAiTriggerTime = DateTime.Now;
                interviewSession.LastProcessedSegmentIndex = interviewSession.Segments.Count - 1;

                StatusChanged?.Invoke("готов");
                ResponseReceived?.Invoke(response);
            }
            finally
            {
                _requestSemaphore.Release();
            }

            return response;
        }

        /// <summary>
        /// Выполняет запрос к OpenRouter с retry при rate limit
        /// </summary>
        private async Task<(bool Success, string Answer, string Error)> ExecuteOpenRouterRequestWithRetry(
            string model,
            List<object> messages,
            CancellationToken ct)
        {
            var requestBody = new
            {
                model = model,
                messages = messages,
                max_tokens = 2048,
                temperature = 0.7
            };

            var json = JsonSerializer.Serialize(requestBody, _jsonOptions);

            for (int attempt = 0; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = RETRY_DELAYS_MS[Math.Min(attempt - 1, RETRY_DELAYS_MS.Length - 1)];
                    StatusChanged?.Invoke($"ожидание {delay / 1000}с...");
                    Debug.WriteLine($"[OpenRouter] Retry {attempt}/{MAX_RETRY_ATTEMPTS} after {delay}ms");

                    await Task.Delay(delay, ct);

                    StatusChanged?.Invoke("повтор запроса...");
                }

                Debug.WriteLine($"[OpenRouter] Request to {model}, attempt {attempt + 1}");

                var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    var httpResponse = await _httpClient.PostAsync(
                        $"{OPENROUTER_BASE_URL}/chat/completions",
                        content,
                        ct);

                    if (httpResponse.IsSuccessStatusCode)
                    {
                        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
                        var apiResponse = JsonSerializer.Deserialize<OpenRouterResponse>(responseJson, _jsonOptions);
                        var answerText = apiResponse?.Choices?.FirstOrDefault()?.Message?.Content;

                        // Сброс счётчика при успехе
                        _rateLimitHitCount = 0;

                        return (true, answerText, null);
                    }

                    // Обработка ошибок
                    var errorContent = await httpResponse.Content.ReadAsStringAsync(ct);
                    var errorInfo = ParseOpenRouterError(errorContent, httpResponse.StatusCode);

                    Debug.WriteLine($"[OpenRouter] Error {httpResponse.StatusCode}: {errorInfo.ShortMessage}");

                    // Rate Limit - пробуем retry
                    if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        _rateLimitHitCount++;
                        _lastRateLimitTime = DateTime.Now;

                        // Если слишком много rate limit подряд, останавливаемся
                        if (_rateLimitHitCount >= 5)
                        {
                            return (false, null,
                                "⏳ Слишком много запросов. OpenRouter временно ограничил доступ.\n" +
                                "Подождите 1-2 минуты или смените модель.");
                        }

                        // Продолжаем retry
                        continue;
                    }

                    // Другие ошибки - не retry
                    return (false, null, errorInfo.UserMessage);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - пробуем retry
                    Debug.WriteLine($"[OpenRouter] Request timeout, attempt {attempt + 1}");
                    continue;
                }
            }

            // Все попытки исчерпаны
            return (false, null,
                "⏳ OpenRouter временно недоступен (rate limit).\n" +
                $"Модель: {model}\n" +
                "Попробуйте:\n" +
                "• Подождать 30-60 секунд\n" +
                "• Выбрать другую модель\n" +
                "• Переключиться на Qwen Free API");
        }

        /// <summary>
        /// Парсит ошибку от OpenRouter
        /// </summary>
        private (string ShortMessage, string UserMessage) ParseOpenRouterError(string errorJson, HttpStatusCode statusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(errorJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorObj))
                {
                    var code = errorObj.TryGetProperty("code", out var codeEl)
                        ? codeEl.GetInt32()
                        : (int)statusCode;

                    var message = errorObj.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString()
                        : "Unknown error";

                    // Извлекаем metadata для более подробной информации
                    string providerName = null;
                    string rawMessage = null;

                    if (errorObj.TryGetProperty("metadata", out var metadata))
                    {
                        if (metadata.TryGetProperty("provider_name", out var provEl))
                            providerName = provEl.GetString();
                        if (metadata.TryGetProperty("raw", out var rawEl))
                            rawMessage = rawEl.GetString();
                    }

                    // Rate limit
                    if (code == 429)
                    {
                        var shortMsg = $"Rate limit от {providerName ?? "OpenRouter"}";
                        var userMsg = $"⏳ Превышен лимит запросов\n" +
                            $"Провайдер: {providerName ?? "Unknown"}\n" +
                            "Подождите немного и попробуйте снова.";

                        return (shortMsg, userMsg);
                    }

                    // Другие ошибки
                    return (message, $"❌ Ошибка OpenRouter ({code}):\n{message}");
                }
            }
            catch (JsonException)
            {
                // Не JSON
            }

            return ($"HTTP {(int)statusCode}", $"❌ Ошибка OpenRouter: {statusCode}");
        }

        private List<object> BuildOpenRouterMessages(
            ContextData context,
            InterviewSession interviewSession)
        {
            var messages = new List<object>();

            messages.Add(new
            {
                role = "system",
                content = BuildSystemPrompt(interviewSession)
            });

            foreach (var msg in _openRouterMessageHistory.TakeLast(MAX_HISTORY_MESSAGES))
            {
                messages.Add(msg);
            }

            var userContent = BuildUserContentWithHistory(context);
            messages.Add(new { role = "user", content = userContent });

            AddToOpenRouterHistory("user", userContent);

            return messages;
        }

        private void AddToOpenRouterHistory(string role, string content)
        {
            _openRouterMessageHistory.Add(new { role = role, content = content });

            while (_openRouterMessageHistory.Count > MAX_HISTORY_MESSAGES * 2)
            {
                _openRouterMessageHistory.RemoveAt(0);
            }
        }

        #endregion

        #region Qwen Free API Implementation

        private async Task<AiResponse> GetAnswerFromQwenAsync(
            InterviewSession interviewSession,
            AiResponse response,
            CancellationToken ct)
        {
            var responseId = Guid.NewGuid().ToString("N");
            var currentTriggerTime = DateTime.Now;
            ChatSession chatSession = null;

            try
            {
                chatSession = await AcquireChatAsync(ct);
                if (chatSession == null)
                {
                    response.Answer = "❌ Не удалось получить сессию чата";
                    StatusChanged?.Invoke("ошибка");
                    return response;
                }

                var context = BuildContextWithHistory(interviewSession, includeImages: true);

                if (!context.HasNewContent)
                {
                    response.Answer = "⚠️ Нет новых вопросов от интервьюера";
                    response.DetectedQuestion = "";
                    StatusChanged?.Invoke("готов");
                    return response;
                }

                bool hasImageScreenshots = context.ImageScreenshots.Any();
                string model = hasImageScreenshots ? _settings.QwenVisionModel : _settings.QwenTextModel;

                var requestBody = BuildQwenRequestBody(chatSession, context, interviewSession, model);

                Debug.WriteLine($"[Qwen] Request to {model}, " +
                    $"chat: {chatSession.ChatId}, " +
                    $"prev: {context.PreviousSegments.Count}, " +
                    $"new: {context.NewSegments.Count}, " +
                    $"images: {context.ImageScreenshots.Count}");

                var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpResponse = await _httpClient.PostAsync(
                    $"{_settings.QwenApiUrl.TrimEnd('/')}/api/chat/completions",
                    content,
                    ct);

                httpResponse.EnsureSuccessStatusCode();

                var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
                var apiResponse = JsonSerializer.Deserialize<ApiChatResponse>(responseJson, _jsonOptions);

                if (apiResponse?.Choices?.FirstOrDefault()?.Message?.Content is string answerText)
                {
                    var newParentId = apiResponse.ParentId ?? apiResponse.ParentIdSnake;
                    if (!string.IsNullOrEmpty(newParentId))
                    {
                        chatSession.ParentId = newParentId;
                    }

                    ParseResponse(answerText, response);

                    if (IsNoQuestionResponse(answerText))
                    {
                        response.Answer = "⚠️ В новой информации нет вопроса";
                        response.DetectedQuestion = "";
                        StatusChanged?.Invoke("готов");
                        return response;
                    }

                    response.IncludedScreenshots = context.ImageScreenshots
                        .Select(s => s.ScreenshotPath)
                        .ToList();

                    MarkSegmentsAsProcessed(context.NewSegments, responseId);
                    interviewSession.LastAiTriggerTime = currentTriggerTime;
                    interviewSession.LastProcessedSegmentIndex = interviewSession.Segments.Count - 1;

                    StatusChanged?.Invoke("готов");
                    ResponseReceived?.Invoke(response);

                    _ = UpdateScreenshotDescriptionsAsync(context.ImageScreenshots, answerText, ct);
                }
                else
                {
                    response.Answer = "❌ Пустой ответ от AI";
                    StatusChanged?.Invoke("ошибка");
                }
            }
            finally
            {
                ReleaseChat(chatSession);
            }

            return response;
        }

        private async Task<string> DescribeScreenshotQwenAsync(
            TranscriptSegment screenshotSegment,
            CancellationToken ct)
        {
            ChatSession chatSession = null;

            try
            {
                chatSession = await AcquireChatAsync(ct);
                if (chatSession == null)
                    return null;

                var messages = new List<object>
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = "Кратко опиши что изображено на скриншоте. Ответь одним-двумя предложениями." },
                            new { type = "image", image = $"data:image/png;base64,{screenshotSegment.ScreenshotBase64}" }
                        }
                    }
                };

                var requestBody = new
                {
                    model = _settings.QwenVisionModel,
                    messages = messages,
                    stream = false,
                    chatId = chatSession.ChatId,
                    parentId = chatSession.ParentId
                };

                var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpResponse = await _httpClient.PostAsync(
                    $"{_settings.QwenApiUrl.TrimEnd('/')}/api/chat/completions",
                    content,
                    ct);

                httpResponse.EnsureSuccessStatusCode();

                var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
                var apiResponse = JsonSerializer.Deserialize<ApiChatResponse>(responseJson, _jsonOptions);

                var description = apiResponse?.Choices?.FirstOrDefault()?.Message?.Content;

                if (!string.IsNullOrEmpty(description))
                {
                    screenshotSegment.Text = description;

                    var newParentId = apiResponse.ParentId ?? apiResponse.ParentIdSnake;
                    if (!string.IsNullOrEmpty(newParentId))
                    {
                        chatSession.ParentId = newParentId;
                    }

                    ScreenshotDescribed?.Invoke(screenshotSegment, description);
                    return description;
                }
            }
            finally
            {
                ReleaseChat(chatSession);
            }

            return null;
        }

        #endregion

        #region Qwen Chat Pool Management

        private async Task<ChatSession> AcquireChatAsync(CancellationToken ct = default)
        {
            await _requestSemaphore.WaitAsync(ct);

            if (_chatPool.TryTake(out var existingSession))
            {
                Debug.WriteLine($"[Qwen] Reusing chat: {existingSession.ChatId}");
                return existingSession;
            }

            var newSession = await CreateChatSessionAsync(ct);
            if (newSession != null)
            {
                Debug.WriteLine($"[Qwen] Created new chat: {newSession.ChatId}");
                return newSession;
            }

            _requestSemaphore.Release();
            return null;
        }

        private void ReleaseChat(ChatSession session)
        {
            if (session != null)
            {
                session.RequestCount++;
                _chatPool.Add(session);
                Debug.WriteLine($"[Qwen] Released chat: {session.ChatId} (requests: {session.RequestCount})");
            }

            _requestSemaphore.Release();
        }

        private async Task<ChatSession> CreateChatSessionAsync(CancellationToken ct = default)
        {
            try
            {
                var chatName = $"Interview_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 4)}";

                var requestBody = new
                {
                    name = chatName,
                    model = _settings.QwenTextModel
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_settings.QwenApiUrl.TrimEnd('/')}/api/chats",
                    content,
                    ct);

                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseJson);

                if (doc.RootElement.TryGetProperty("chatId", out var chatIdElement))
                {
                    return new ChatSession
                    {
                        ChatId = chatIdElement.GetString(),
                        CreatedAt = DateTime.Now,
                        RequestCount = 0
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Qwen] Failed to create chat: {ex.Message}");
            }

            return null;
        }

        private object BuildQwenRequestBody(
            ChatSession chatSession,
            ContextData context,
            InterviewSession interviewSession,
            string model)
        {
            var systemPrompt = BuildSystemPrompt(interviewSession);
            var userContent = BuildUserContentWithHistory(context);

            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt }
            };

            if (context.ImageScreenshots.Any())
            {
                var contentParts = new List<object>
                {
                    new { type = "text", text = userContent }
                };

                foreach (var screenshot in context.ImageScreenshots)
                {
                    contentParts.Add(new
                    {
                        type = "image",
                        image = $"data:image/png;base64,{screenshot.ScreenshotBase64}"
                    });
                }

                messages.Add(new { role = "user", content = contentParts });
            }
            else
            {
                messages.Add(new { role = "user", content = userContent });
            }

            return new
            {
                model = model,
                messages = messages,
                stream = false,
                chatId = chatSession.ChatId,
                parentId = chatSession.ParentId
            };
        }

        #endregion

        #region Context Building

        private ContextData BuildContextWithHistory(InterviewSession session, bool includeImages)
        {
            var context = new ContextData();
            var cutoffTime = DateTime.Now.AddMinutes(-MAX_CONTEXT_MINUTES);

            var allRecentSegments = session.Segments
                .Where(s => s.Timestamp >= cutoffTime)
                .Where(s => s.IsSpeech || s.IsScreenshot)
                .TakeLast(MAX_CONTEXT_SEGMENTS)
                .ToList();

            if (allRecentSegments.Count < 5)
            {
                allRecentSegments = session.Segments
                    .Where(s => s.IsSpeech || s.IsScreenshot)
                    .TakeLast(MAX_CONTEXT_SEGMENTS)
                    .ToList();
            }

            context.Segments = allRecentSegments;

            if (session.LastAiTriggerTime.HasValue)
            {
                var triggerTime = session.LastAiTriggerTime.Value;

                context.PreviousSegments = allRecentSegments
                    .Where(s => s.Timestamp <= triggerTime)
                    .ToList();

                context.NewSegments = allRecentSegments
                    .Where(s => s.Timestamp > triggerTime)
                    .ToList();
            }
            else
            {
                context.PreviousSegments = new List<TranscriptSegment>();
                context.NewSegments = allRecentSegments;
            }

            context.HasNewContent = context.NewSegments
                .Any(s => s.Speaker == SpeakerType.Interviewer && s.IsSpeech);

            if (includeImages)
            {
                var newScreenshots = context.NewSegments.Where(s => s.IsScreenshot).ToList();

                context.ImageScreenshots = newScreenshots
                    .TakeLast(MAX_SCREENSHOTS_AS_IMAGE)
                    .Where(s => !string.IsNullOrEmpty(s.ScreenshotBase64))
                    .ToList();

                var imageScreenshotIds = new HashSet<TranscriptSegment>(context.ImageScreenshots);
                context.TextScreenshots = allRecentSegments
                    .Where(s => s.IsScreenshot && !imageScreenshotIds.Contains(s) && !string.IsNullOrEmpty(s.Text))
                    .ToList();
            }
            else
            {
                context.ImageScreenshots = new List<TranscriptSegment>();
                context.TextScreenshots = allRecentSegments
                    .Where(s => s.IsScreenshot && !string.IsNullOrEmpty(s.Text))
                    .ToList();
            }

            return context;
        }

        private void MarkSegmentsAsProcessed(List<TranscriptSegment> segments, string responseId)
        {
            foreach (var segment in segments)
            {
                segment.ProcessedInResponseId = responseId;
            }
        }

        private string BuildSystemPrompt(InterviewSession session)
        {
            var screenshotNote = _settings.SupportsVision
                ? "5. Если есть скриншоты — учти их"
                : "5. Скриншоты могут содержать текстовые описания";

            return $@"Ты — скрытый AI-ассистент на техническом собеседовании.
Кандидат проходит собеседование на позицию: {session.Role}
Технический стек: {session.TechStack}

ТВОЯ ЗАДАЧА:
1. Проанализируй транскрипцию диалога между Interviewer и User
2. ВАЖНО: Ищи вопрос ТОЛЬКО в секции ""НОВАЯ ИНФОРМАЦИЯ""
3. Секция ""ПРЕДЫДУЩИЙ КОНТЕКСТ"" — уже обработано, НЕ ищи там вопросы
4. Сформулируй КРАТКИЙ ответ (1-3 предложений)
{screenshotNote}

ФОРМАТ:
❓ Вопрос: [вопрос из НОВОЙ ИНФОРМАЦИИ]
✅ Ответ: [ответ]
💡 Ключевые слова: [2-3 термина]

ПРАВИЛА:
- Не пытайся оправдываться, если не нашел вопроса. Также не задавай лишних вопросов.
- Пытайся ответить так, как будто ты отвечающий. Будь человечен.
- Отвечай на языке собеседования
- Если в ""НОВАЯ ИНФОРМАЦИЯ"" нет вопроса — так и напиши
- Будь уверенным, профессиональным";
        }

        private string BuildUserContentWithHistory(ContextData context)
        {
            var sb = new StringBuilder();

            if (context.PreviousSegments.Any())
            {
                sb.AppendLine("=== ПРЕДЫДУЩИЙ КОНТЕКСТ (уже обработано) ===");
                foreach (var segment in context.PreviousSegments)
                {
                    AppendSegment(sb, segment, context);
                }
                sb.AppendLine("=== КОНЕЦ КОНТЕКСТА ===");
                sb.AppendLine();
            }

            sb.AppendLine("=== НОВАЯ ИНФОРМАЦИЯ (ищи вопрос ЗДЕСЬ) ===");
            if (context.NewSegments.Any())
            {
                foreach (var segment in context.NewSegments)
                {
                    AppendSegment(sb, segment, context);
                }
            }
            else
            {
                sb.AppendLine("[Нет новых сегментов]");
            }
            sb.AppendLine("=== КОНЕЦ ===");
            sb.AppendLine();
            sb.AppendLine("Найди вопрос в \"НОВАЯ ИНФОРМАЦИЯ\" и ответь.");

            return sb.ToString();
        }

        private void AppendSegment(StringBuilder sb, TranscriptSegment segment, ContextData context)
        {
            if (segment.IsScreenshot)
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    sb.AppendLine($"[{segment.TimeCode}] 📷 #{segment.ScreenshotNumber}: {segment.Text}");
                else if (context.ImageScreenshots.Contains(segment))
                    sb.AppendLine($"[{segment.TimeCode}] 📷 #{segment.ScreenshotNumber}: [изображение прикреплено]");
                else
                    sb.AppendLine($"[{segment.TimeCode}] 📷 #{segment.ScreenshotNumber}: [скриншот без описания]");
            }
            else if (segment.IsSpeech)
            {
                var speaker = segment.Speaker == SpeakerType.Interviewer ? "Interviewer" : "User";
                sb.AppendLine($"[{segment.TimeCode}] {speaker}: {segment.Text}");
            }
        }

        #endregion

        #region Response Parsing

        private void ParseResponse(string responseText, AiResponse response)
        {
            response.Answer = responseText;

            foreach (var line in responseText.Split('\n'))
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("❓"))
                {
                    response.DetectedQuestion = trimmed
                        .Replace("❓ Вопрос:", "")
                        .Replace("❓Вопрос:", "")
                        .Replace("❓", "")
                        .Trim();
                }
                else if (trimmed.StartsWith("💡"))
                {
                    response.KeyWords = trimmed
                        .Replace("💡 Ключевые слова:", "")
                        .Replace("💡Ключевые слова:", "")
                        .Replace("💡", "")
                        .Trim();
                }
            }

            response.TokenCount = responseText.Length / 3;
        }

        private bool IsNoQuestionResponse(string answerText)
        {
            if (string.IsNullOrWhiteSpace(answerText))
                return true;

            var lowerText = answerText.ToLowerInvariant();

            var noQuestionPhrases = new[]
            {
                "нет вопроса",
                "отсутствует постановка вопроса",
                "нет нового вопроса",
                "нет новых вопросов",
                "вопрос не обнаружен",
                "вопрос не найден",
                "в новой информации нет",
                "отсутствует вопрос",
                "не содержит вопрос",
                "не найден вопрос",
                "вопросов не найдено",
                "no question",
                "no new question",
                "question not found",
                "[нет новых сегментов]"
            };

            return noQuestionPhrases.Any(phrase => lowerText.Contains(phrase));
        }

        private async Task UpdateScreenshotDescriptionsAsync(
            List<TranscriptSegment> screenshots,
            string aiResponse,
            CancellationToken ct)
        {
            if (!_settings.SupportsVision)
                return;

            foreach (var screenshot in screenshots.Where(s => string.IsNullOrWhiteSpace(s.Text)))
            {
                _ = DescribeScreenshotAsync(screenshot, ct);
            }
        }

        #endregion

        #region Cleanup

        public void Dispose()
        {
            _httpClient?.Dispose();
            _poolLock?.Dispose();
            _requestSemaphore?.Dispose();
        }

        #endregion

        #region Inner Classes

        private class ChatSession
        {
            public string ChatId { get; set; }
            public string ParentId { get; set; }
            public DateTime CreatedAt { get; set; }
            public int RequestCount { get; set; }
        }

        private class ContextData
        {
            public List<TranscriptSegment> Segments { get; set; } = new();
            public List<TranscriptSegment> PreviousSegments { get; set; } = new();
            public List<TranscriptSegment> NewSegments { get; set; } = new();
            public List<TranscriptSegment> ImageScreenshots { get; set; } = new();
            public List<TranscriptSegment> TextScreenshots { get; set; } = new();
            public bool HasNewContent { get; set; }
        }

        private class ApiChatResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("choices")] public List<ApiChoice> Choices { get; set; }
            [JsonPropertyName("chatId")] public string ChatId { get; set; }
            [JsonPropertyName("parentId")] public string ParentId { get; set; }
            [JsonPropertyName("parent_id")] public string ParentIdSnake { get; set; }
        }

        private class OpenRouterResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("choices")] public List<ApiChoice> Choices { get; set; }
            [JsonPropertyName("model")] public string Model { get; set; }
            [JsonPropertyName("usage")] public OpenRouterUsage Usage { get; set; }
        }

        private class OpenRouterUsage
        {
            [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
            [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
            [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
        }

        private class ApiChoice
        {
            [JsonPropertyName("message")] public ApiMessage Message { get; set; }
            [JsonPropertyName("finish_reason")] public string FinishReason { get; set; }
        }

        private class ApiMessage
        {
            [JsonPropertyName("role")] public string Role { get; set; }
            [JsonPropertyName("content")] public string Content { get; set; }
        }

        /// <summary>
        /// Исключение для rate limit ошибок
        /// </summary>
        private class RateLimitException : Exception
        {
            public string UserFriendlyMessage { get; }

            public RateLimitException(string userMessage) : base(userMessage)
            {
                UserFriendlyMessage = userMessage;
            }
        }

        #endregion
    }
}