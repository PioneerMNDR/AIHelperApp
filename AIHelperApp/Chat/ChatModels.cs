using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AIHelperApp.Chat
{
    // Модель прикреплённого файла
    public class AttachedFile : INotifyPropertyChanged
    {
        private string _name;
        private string _localPath;
        private string _fileType;
        private long _size;
        private string _url;
        private bool _isUploading;
        private double _uploadProgress;
        private string _errorMessage;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string LocalPath
        {
            get => _localPath;
            set { _localPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Тип файла: "image", "audio", "document", "file"
        /// </summary>
        public string FileType
        {
            get => _fileType;
            set
            {
                _fileType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsAudio));
            }
        }

        public long Size
        {
            get => _size;
            set { _size = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeFormatted)); }
        }

        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        public bool IsUploading
        {
            get => _isUploading;
            set { _isUploading = value; OnPropertyChanged(); }
        }

        public double UploadProgress
        {
            get => _uploadProgress;
            set { _uploadProgress = value; OnPropertyChanged(); }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public bool IsImage => FileType == "image";

        public bool IsAudio => FileType == "audio";

        public string SizeFormatted
        {
            get
            {
                if (Size < 1024) return $"{Size} B";
                if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
                return $"{Size / (1024.0 * 1024.0):F1} MB";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Основной запрос к API
    public class ChatRequest
    {
        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("incremental_output")]
        public bool IncrementalOutput { get; set; } = true;

        [JsonPropertyName("chat_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ChatId { get; set; }

        [JsonPropertyName("chat_mode")]
        public string ChatMode { get; set; } = "normal";

        [JsonPropertyName("messages")]
        public List<ChatMessageRequest> Messages { get; set; } = new List<ChatMessageRequest>();

        [JsonPropertyName("model")]
        public string Model { get; set; }

        [JsonPropertyName("parent_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ParentId { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    public class ChatMessageRequest
    {
        [JsonPropertyName("fid")]
        public string Fid { get; set; }

        [JsonPropertyName("parentId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ParentIdCamel { get; set; }

        [JsonPropertyName("parent_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ParentId { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }

        [JsonPropertyName("chat_type")]
        public string ChatType { get; set; } = "t2t";

        [JsonPropertyName("sub_chat_type")]
        public string SubChatType { get; set; } = "t2t";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("user_action")]
        public string UserAction { get; set; } = "chat";

        [JsonPropertyName("models")]
        public List<string> Models { get; set; } = new List<string>();

        [JsonPropertyName("files")]
        public List<object> Files { get; set; } = new List<object>();

        [JsonPropertyName("childrenIds")]
        public List<string> ChildrenIds { get; set; } = new List<string>();

        [JsonPropertyName("extra")]
        public MessageExtra Extra { get; set; }

        [JsonPropertyName("feature_config")]
        public FeatureConfig FeatureConfig { get; set; }
    }

    public class MessageExtra
    {
        [JsonPropertyName("meta")]
        public MessageMeta Meta { get; set; }
    }

    public class MessageMeta
    {
        [JsonPropertyName("subChatType")]
        public string SubChatType { get; set; } = "t2t";
    }

    public class FeatureConfig
    {
        [JsonPropertyName("thinking_enabled")]
        public bool ThinkingEnabled { get; set; }

        [JsonPropertyName("output_schema")]
        public string OutputSchema { get; set; } = "phase";
    }


    #region Response Models

    public class ChatResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("object")]
        public string Object { get; set; }

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; }

        [JsonPropertyName("choices")]
        public List<Choice> Choices { get; set; }

        [JsonPropertyName("usage")]
        public Usage Usage { get; set; }

        [JsonPropertyName("chatId")]
        public string ChatId { get; set; }

        [JsonPropertyName("chat_id")]
        public string ChatIdSnake { get; set; }

        [JsonPropertyName("parentId")]
        public string ParentId { get; set; }

        [JsonPropertyName("parent_id")]
        public string ParentIdSnake { get; set; }

        [JsonPropertyName("fid")]
        public string Fid { get; set; }

        // Получить ChatId из любого формата
        public string GetChatId() => ChatId ?? ChatIdSnake;
        public string GetParentId() => ParentId ?? ParentIdSnake;

        [JsonPropertyName("response_id")]
        public string ResponseId { get; set; }

        public string GetResponseId()
        {
            // Пробуем получить response_id из разных мест
            if (!string.IsNullOrEmpty(ResponseId))
                return ResponseId;

            return null;
        }
    }

    public class Choice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public ChatMessageResponse Message { get; set; }

        [JsonPropertyName("delta")]
        public Delta Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string FinishReason { get; set; }
    }

    public class ChatMessageResponse
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }
    }

    public class Delta
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }
    }

    public class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    #endregion

    #region Other Models

    public class ModelsResponse
    {
        [JsonPropertyName("object")]
        public string Object { get; set; }

        [JsonPropertyName("data")]
        public List<ModelInfo> Data { get; set; }
    }

    public class ModelInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("object")]
        public string Object { get; set; }

        [JsonPropertyName("owned_by")]
        public string OwnedBy { get; set; }
    }

    public class StatusResponse
    {
        [JsonPropertyName("authenticated")]
        public bool Authenticated { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("accounts")]
        public List<AccountStatus> Accounts { get; set; }
    }

    public class AccountStatus
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("resetAt")]
        public string ResetAt { get; set; }
    }

    // UI модель
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        private string _content;
        private ObservableCollection<AttachedFile> _attachments;

        public string Role { get; set; }

        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<AttachedFile> Attachments
        {
            get => _attachments;
            set
            {
                _attachments = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAttachments));
            }
        }

        public bool HasAttachments => Attachments?.Count > 0;
        public bool IsUser => Role == "user";
        public bool IsAssistant => Role == "assistant";
        public bool IsSystem => Role == "system";
        public string Timestamp { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion



}
