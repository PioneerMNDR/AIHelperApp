using AIHelperApp.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private readonly string _baseUrl;


        private readonly ConcurrentBag<ChatSession> _chatPool = new();
        private readonly SemaphoreSlim _poolLock = new(1, 1);
        private bool _isInitialized;

        private const int MAX_CONCURRENT_REQUESTS = 3; // Максимум параллельных запросов
        private readonly SemaphoreSlim _requestSemaphore = new(MAX_CONCURRENT_REQUESTS, MAX_CONCURRENT_REQUESTS);

        private const int MAX_CONTEXT_MINUTES = 5;
        private const int MAX_CONTEXT_SEGMENTS = 30;
        private const int MAX_SCREENSHOTS_AS_IMAGE = 2;

        private const string TEXT_MODEL = "qwen3.5-flash";
        private const string VISION_MODEL = "qwen3.5-flash";

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public event Action<string> StatusChanged;
        public event Action<AiResponse> ResponseReceived;
        public event Action<TranscriptSegment, string> ScreenshotDescribed;

        public InterviewAiService(string baseUrl = "http://localhost:3264")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3)
            };
        }

        #region Lazy Chat Pool Management

        /// <summary>
        /// Инициализация — просто помечаем как готовый, чаты создаются лениво
        /// </summary>
        public Task InitializeAsync(CancellationToken ct = default)
        {
            _isInitialized = true;
            StatusChanged?.Invoke("готов");
            Debug.WriteLine("[InterviewAI] Service initialized (lazy mode)");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Получает чат из пула или создаёт новый
        /// </summary>
        private async Task<ChatSession> AcquireChatAsync(CancellationToken ct = default)
        {
            // Ограничиваем параллельные запросы
            await _requestSemaphore.WaitAsync(ct);

            // Пытаемся взять из пула
            if (_chatPool.TryTake(out var existingSession))
            {
                Debug.WriteLine($"[InterviewAI] Reusing chat: {existingSession.ChatId}");
                return existingSession;
            }

            // Создаём новый
            var newSession = await CreateChatSessionAsync(ct);
            if (newSession != null)
            {
                Debug.WriteLine($"[InterviewAI] Created new chat: {newSession.ChatId}");
                return newSession;
            }

            // Не удалось создать — освобождаем семафор
            _requestSemaphore.Release();
            return null;
        }

        /// <summary>
        /// Возвращает чат в пул для переиспользования
        /// </summary>
        private void ReleaseChat(ChatSession session)
        {
            if (session != null)
            {
                session.RequestCount++;
                _chatPool.Add(session);
                Debug.WriteLine($"[InterviewAI] Released chat: {session.ChatId} (requests: {session.RequestCount})");
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
                    model = TEXT_MODEL
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/api/chats", content, ct);
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
                Debug.WriteLine($"[InterviewAI] Failed to create chat: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Сброс контекста для новой сессии (НЕ создаёт новые чаты!)
        /// </summary>
        public void ResetForNewSession()
        {
            // Просто сбрасываем parentId у всех чатов в пуле
            // Это позволит начать новый контекст в тех же чатах

            var chatsToReset = new List<ChatSession>();
            while (_chatPool.TryTake(out var chat))
            {
                chat.ParentId = null; // Сброс контекста
                chatsToReset.Add(chat);
            }

            foreach (var chat in chatsToReset)
            {
                _chatPool.Add(chat);
            }

            Debug.WriteLine($"[InterviewAI] Reset {chatsToReset.Count} chats for new session");
        }

        #endregion

        #region Main API

        public async Task<AiResponse> GetAnswerAsync(
     InterviewSession interviewSession,
     CancellationToken ct = default)
        {
            StatusChanged?.Invoke("генерирую...");

            var responseId = Guid.NewGuid().ToString("N");
            var currentTriggerTime = DateTime.Now;

            var response = new AiResponse
            {
                Timestamp = currentTriggerTime,
                IsStreaming = false
            };

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

                var context = BuildContextWithHistory(interviewSession);

                if (!context.HasNewContent)
                {
                    response.Answer = "⚠️ Нет новых вопросов от интервьюера";
                    response.DetectedQuestion = "";
                    StatusChanged?.Invoke("готов");
                    return response;
                }

                bool hasImageScreenshots = context.ImageScreenshots.Any();
                string model = hasImageScreenshots ? VISION_MODEL : TEXT_MODEL;

                var requestBody = BuildRequestBody(
                    chatSession,
                    context,
                    interviewSession,
                    model);

                Debug.WriteLine($"[InterviewAI] Request to {model}, " +
                    $"chat: {chatSession.ChatId}, " +
                    $"prev: {context.PreviousSegments.Count}, " +
                    $"new: {context.NewSegments.Count}");

                var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpResponse = await _httpClient.PostAsync(
                    $"{_baseUrl}/api/chat/completions",
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

                    // ═══ Проверяем, есть ли реальный вопрос ═══
                    if (IsNoQuestionResponse(answerText))
                    {
                        response.Answer = "⚠️ В новой информации нет вопроса";
                        response.DetectedQuestion = "";
                        StatusChanged?.Invoke("готов");

                        Debug.WriteLine("[InterviewAI] No question detected in response, skipping");
                        return response; // НЕ добавляем в панель ответов
                    }

                    response.IncludedScreenshots = context.ImageScreenshots
                        .Select(s => s.ScreenshotPath)
                        .ToList();

                    MarkSegmentsAsProcessed(context.NewSegments, responseId);
                    interviewSession.LastAiTriggerTime = currentTriggerTime;
                    interviewSession.LastProcessedSegmentIndex =
                        interviewSession.Segments.Count - 1;

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
            catch (OperationCanceledException)
            {
                response.Answer = "⏹ Запрос отменён";
                StatusChanged?.Invoke("отменён");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InterviewAI] Error: {ex}");
                response.Answer = $"❌ Ошибка: {ex.Message}";
                StatusChanged?.Invoke("ошибка");
            }
            finally
            {
                ReleaseChat(chatSession);
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
                    model = VISION_MODEL,
                    messages = messages,
                    stream = false,
                    chatId = chatSession.ChatId,
                    parentId = chatSession.ParentId
                };

                var json = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpResponse = await _httpClient.PostAsync(
                    $"{_baseUrl}/api/chat/completions",
                    content,
                    ct);

                httpResponse.EnsureSuccessStatusCode();

                var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
                var apiResponse = JsonSerializer.Deserialize<ApiChatResponse>(responseJson, _jsonOptions);

                var description = apiResponse?.Choices?.FirstOrDefault()?.Message?.Content;

                if (!string.IsNullOrEmpty(description))
                {
                    screenshotSegment.Text = description;

                    // Обновляем parentId
                    var newParentId = apiResponse.ParentId ?? apiResponse.ParentIdSnake;
                    if (!string.IsNullOrEmpty(newParentId))
                    {
                        chatSession.ParentId = newParentId;
                    }

                    ScreenshotDescribed?.Invoke(screenshotSegment, description);
                    return description;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InterviewAI] Failed to describe screenshot: {ex.Message}");
            }
            finally
            {
                ReleaseChat(chatSession);
            }

            return null;
        }

        #endregion

        #region Context Building

        private ContextData BuildContextWithHistory(InterviewSession session)
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

            var newScreenshots = context.NewSegments.Where(s => s.IsScreenshot).ToList();

            context.ImageScreenshots = newScreenshots
                .TakeLast(MAX_SCREENSHOTS_AS_IMAGE)
                .Where(s => !string.IsNullOrEmpty(s.ScreenshotBase64))
                .ToList();

            var imageScreenshotIds = new HashSet<TranscriptSegment>(context.ImageScreenshots);
            context.TextScreenshots = allRecentSegments
                .Where(s => s.IsScreenshot && !imageScreenshotIds.Contains(s) && !string.IsNullOrEmpty(s.Text))
                .ToList();

            return context;
        }

        private void MarkSegmentsAsProcessed(List<TranscriptSegment> segments, string responseId)
        {
            foreach (var segment in segments)
            {
                segment.ProcessedInResponseId = responseId;
            }
        }

        private object BuildRequestBody(
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

        private string BuildSystemPrompt(InterviewSession session)
        {
            return $@"Ты — скрытый AI-ассистент на техническом собеседовании.
Кандидат проходит собеседование на позицию: {session.Role}
Технический стек: {session.TechStack}

ТВОЯ ЗАДАЧА:
1. Проанализируй транскрипцию диалога между Interviewer и User
2. ВАЖНО: Ищи вопрос ТОЛЬКО в секции ""НОВАЯ ИНФОРМАЦИЯ""
3. Секция ""ПРЕДЫДУЩИЙ КОНТЕКСТ"" — уже обработано, НЕ ищи там вопросы
4. Сформулируй КРАТКИЙ ответ (1-3 предложений)
5. Если есть скриншоты — учти их

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
                    sb.AppendLine($"[{segment.TimeCode}] 📷 #{segment.ScreenshotNumber}: [изображение]");
                else
                    sb.AppendLine($"[{segment.TimeCode}] 📷 #{segment.ScreenshotNumber}");
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
        /// <summary>
        /// Проверяет, указывает ли ответ на отсутствие вопроса
        /// </summary>
        private bool IsNoQuestionResponse(string answerText)
        {
            if (string.IsNullOrWhiteSpace(answerText))
                return true;

            var lowerText = answerText.ToLowerInvariant();

            // Фразы, указывающие на отсутствие вопроса
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

        #endregion
    }
}