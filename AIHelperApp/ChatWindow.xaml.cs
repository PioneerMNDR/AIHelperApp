using AIHelperApp.Chat;
using AIHelperApp.Data;
using AIHelperApp.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace AIHelperApp
{
    public partial class ChatWindow : TianXiaTech.BlurWindow
    {
        // ═══ Services ═══
        private readonly LLMApiService _apiService;
        private readonly FileUploadService _fileUploadService;
        private readonly ObservableCollection<ChatMessageViewModel> _messages;
        private readonly ObservableCollection<AttachedFile> _pendingAttachments;
        private CancellationTokenSource _cancellationTokenSource;
        private string _selectedModel = "qwen-max-latest";
        private bool _isProcessing;
        private DispatcherTimer _loadingTimer;
        private int _dotCount;

        // ═══ Stealth Mode ═══
        private IntPtr _hwnd;
        private bool _isHidden = true;

        public ChatWindow()
        {
            InitializeComponent();

            _apiService = new LLMApiService("http://localhost:3264");
            _fileUploadService = new FileUploadService("http://localhost:3264");
            _messages = new ObservableCollection<ChatMessageViewModel>();
            _pendingAttachments = new ObservableCollection<AttachedFile>();

            MessagesItemsControl.ItemsSource = _messages;
            AttachmentsItemsControl.ItemsSource = _pendingAttachments;

            _pendingAttachments.CollectionChanged += (s, e) => UpdateAttachmentsPanel();

            _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _loadingTimer.Tick += LoadingTimer_Tick;

            Loaded += ChatWindow_Loaded;
            Closing += ChatWindow_Closing;
        }

        private async void ChatWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyStealthProtection(_isHidden);
            UpdateStealthUI();

            await LoadModelsAsync();
            await CheckStatusAsync();

            _apiService.SetSystemPrompt(SystemPromptTextBox.Text);

            AddMessage("assistant", "Привет! Я AI-ассистент. Можете прикрепить файлы (изображения, аудио, документы) кнопкой 📎 или перетащить их в окно.\n\nЯ поддерживаю **Markdown** форматирование:\n- `код`\n- **жирный**\n- *курсив*\n- списки и таблицы");
        }

        private void ChatWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _apiService?.Dispose();
            _fileUploadService?.Dispose();
            _loadingTimer?.Stop();
        }

        #region ═══ STEALTH MODE ═══

        private void StealthToggle_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleStealth();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.OemTilde && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleStealth();
                e.Handled = true;
            }
            else if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleStealth();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                AttachFile_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control
                     && !InputTextBox.IsFocused)
            {
                PasteFromClipboard();
                e.Handled = true;
            }
        }

        private void ToggleStealth()
        {
            _isHidden = !_isHidden;
            ApplyStealthProtection(_isHidden);

            var storyboard = (Storyboard)FindResource(_isHidden ? "ToggleOn" : "ToggleOff");
            storyboard.Begin(this);

            UpdateStealthUI();
        }

        private void ApplyStealthProtection(bool hide)
        {
            if (_hwnd == IntPtr.Zero) return;

            uint flag = hide
                ? NativeMethods.WDA_EXCLUDEFROMCAPTURE
                : NativeMethods.WDA_NONE;

            if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, flag) && hide)
            {
                NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_MONITOR);
            }
        }

        private void UpdateStealthUI()
        {
            if (_isHidden)
            {
                StealthIcon.Text = "🔒";
                StealthStatusText.Text = "Скрыто";
                StealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            }
            else
            {
                StealthIcon.Text = "👁";
                StealthStatusText.Text = "Видимо";
                StealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("RedBrush");
            }
        }

        #endregion

        #region ═══ MODEL & STATUS ═══

        private async Task LoadModelsAsync()
        {
            try
            {
                var models = await _apiService.GetModelsAsync();

                Dispatcher.Invoke(() =>
                {
                    ModelComboBox.Items.Clear();
                    foreach (var model in models)
                    {
                        ModelComboBox.Items.Add(model.Id);
                    }
                    if (ModelComboBox.Items.Count > 0)
                        ModelComboBox.SelectedIndex = 0;
                });
            }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    ModelComboBox.Items.Add("qwen-max-latest");
                    ModelComboBox.Items.Add("qwen3-max");
                    ModelComboBox.Items.Add("qwen3-coder-plus");
                    ModelComboBox.Items.Add("qwq-32b");
                    ModelComboBox.SelectedIndex = 0;
                });
                UpdateStatus("⚠️ Модели загружены локально", false);
            }
        }

        private async Task CheckStatusAsync()
        {
            try
            {
                var status = await _apiService.GetStatusAsync();
                var okCount = status.Accounts?.FindAll(a => a.Status == "OK").Count ?? 0;
                var total = status.Accounts?.Count ?? 0;
                UpdateStatus($"✅ Подключено ({okCount}/{total})", true);
            }
            catch
            {
                UpdateStatus("❌ Нет подключения", false);
            }
        }

        private void UpdateStatus(string text, bool ok)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = text;
                StatusText.Foreground = ok
                    ? (System.Windows.Media.Brush)FindResource("GreenBrush")
                    : (System.Windows.Media.Brush)FindResource("RedBrush");
            });
        }

        #endregion

        #region ═══ MESSAGES ═══

        private void AddMessage(string role, string content, ObservableCollection<AttachedFile> attachments = null)
        {
            Dispatcher.Invoke(() =>
            {
                _messages.Add(new ChatMessageViewModel
                {
                    Role = role,
                    Content = content,
                    Timestamp = DateTime.Now.ToString("HH:mm"),
                    Attachments = attachments
                });
                MessagesScrollViewer.ScrollToEnd();
            });
        }

        private void SetProcessing(bool processing)
        {
            _isProcessing = processing;
            Dispatcher.Invoke(() =>
            {
                SendButton.IsEnabled = !processing;
                InputTextBox.IsEnabled = !processing;
                AttachButton.IsEnabled = !processing;
                LoadingIndicator.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;

                if (processing) { _dotCount = 0; _loadingTimer.Start(); }
                else _loadingTimer.Stop();
            });
        }

        private void LoadingTimer_Tick(object sender, EventArgs e)
        {
            _dotCount = (_dotCount + 1) % 4;
            LoadingDots.Text = new string('●', _dotCount + 1).PadRight(4, '○');
        }

        #endregion

        #region ═══ FILE ATTACHMENTS ═══

        private void AttachFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = FileUploadService.GetFileDialogFilter(),
                Multiselect = true,
                Title = "Выберите файлы для прикрепления"
            };

            if (dialog.ShowDialog() == true)
            {
                AddFilesToPending(dialog.FileNames);
            }
        }

        private void AddFilesToPending(IEnumerable<string> filePaths)
        {
            foreach (var filePath in filePaths)
            {
                var (isValid, error) = FileUploadService.ValidateFile(filePath);

                if (!isValid)
                {
                    AddMessage("system", $"⚠️ {Path.GetFileName(filePath)}: {error}");
                    continue;
                }

                if (_pendingAttachments.Any(a => a.LocalPath == filePath))
                    continue;

                var fileInfo = new FileInfo(filePath);
                var attachment = new AttachedFile
                {
                    Name = fileInfo.Name,
                    LocalPath = filePath,
                    FileType = FileUploadService.GetFileType(filePath),
                    Size = fileInfo.Length
                };

                _pendingAttachments.Add(attachment);
            }
        }

        private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is AttachedFile attachment)
            {
                _pendingAttachments.Remove(attachment);
            }
        }

        private void ClearAttachments_Click(object sender, RoutedEventArgs e)
        {
            _pendingAttachments.Clear();
        }

        private void UpdateAttachmentsPanel()
        {
            Dispatcher.Invoke(() =>
            {
                var hasFiles = _pendingAttachments.Count > 0;
                AttachmentsPanel.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;

                var count = _pendingAttachments.Count;
                var totalSize = _pendingAttachments.Sum(a => a.Size);
                var sizeStr = totalSize < 1024 * 1024
                    ? $"{totalSize / 1024.0:F0} KB"
                    : $"{totalSize / (1024.0 * 1024.0):F1} MB";

                AttachmentCountText.Text = $" {count} {GetFileDeclension(count)} ({sizeStr})";
            });
        }

        private static string GetFileDeclension(int count)
        {
            var abs = Math.Abs(count) % 100;
            var lastDigit = abs % 10;

            if (abs > 10 && abs < 20) return "файлов";
            if (lastDigit == 1) return "файл";
            if (lastDigit >= 2 && lastDigit <= 4) return "файла";
            return "файлов";
        }

        private async Task<(List<string> imageUrls, List<string> audioUrls)> UploadPendingFilesAsync(CancellationToken cancellationToken)
        {
            var imageUrls = new List<string>();
            var audioUrls = new List<string>();

            if (_pendingAttachments.Count == 0)
                return (imageUrls, audioUrls);

            Dispatcher.Invoke(() =>
            {
                UploadProgressPanel.Visibility = Visibility.Visible;
                UploadProgressBar.Value = 0;
            });

            var total = _pendingAttachments.Count;
            var uploaded = 0;

            foreach (var attachment in _pendingAttachments.ToList())
            {
                if (cancellationToken.IsCancellationRequested) break;

                attachment.IsUploading = true;

                Dispatcher.Invoke(() =>
                {
                    UploadProgressText.Text = $"Загрузка {uploaded + 1}/{total}: {attachment.Name}";
                });

                var progress = new Progress<double>(p =>
                {
                    attachment.UploadProgress = p;
                    Dispatcher.Invoke(() =>
                    {
                        UploadProgressBar.Value = ((uploaded + p) / total) * 100;
                    });
                });

                var result = await _fileUploadService.UploadFileAsync(
                    attachment.LocalPath, progress, cancellationToken);

                attachment.IsUploading = false;

                if (result.Success && result.File != null)
                {
                    attachment.Url = result.File.Url;
                    attachment.UploadProgress = 1.0;

                    if (attachment.IsImage)
                        imageUrls.Add(result.File.Url);
                    else if (attachment.IsAudio)
                        audioUrls.Add(result.File.Url);

                    uploaded++;
                }
                else
                {
                    attachment.ErrorMessage = result.Error ?? "Неизвестная ошибка";
                    System.Diagnostics.Debug.WriteLine($"❌ Upload failed: {attachment.Name} - {result.Error}");
                }
            }

            Dispatcher.Invoke(() =>
            {
                UploadProgressPanel.Visibility = Visibility.Collapsed;
            });

            return (imageUrls, audioUrls);
        }

        #endregion

        #region ═══ DRAG & DROP ═══

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DragDropOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            DragDropOverlay.Visibility = Visibility.Collapsed;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    AddFilesToPending(files);
                }
            }
            e.Handled = true;
        }

        #endregion

        #region ═══ CLIPBOARD PASTE ═══

        private void PasteFromClipboard()
        {
            if (Clipboard.ContainsImage())
            {
                try
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        using (var stream = new FileStream(tempPath, FileMode.Create))
                        {
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                            encoder.Save(stream);
                        }

                        AddFilesToPending(new[] { tempPath });
                    }
                }
                catch (Exception ex)
                {
                    AddMessage("system", $"⚠️ Ошибка вставки из буфера: {ex.Message}");
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var filePaths = new List<string>();
                foreach (var file in files)
                {
                    if (file != null) filePaths.Add(file);
                }
                AddFilesToPending(filePaths);
            }
        }

        #endregion

        #region ═══ EVENT HANDLERS ═══

        private void ModelComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ModelComboBox.SelectedItem != null)
                _selectedModel = ModelComboBox.SelectedItem.ToString();
        }

        private void SystemPrompt_Click(object sender, RoutedEventArgs e)
        {
            SystemPromptPanel.Visibility = SystemPromptPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ApplySystemPrompt_Click(object sender, RoutedEventArgs e)
        {
            _apiService.ResetConversation();
            _apiService.SetSystemPrompt(SystemPromptTextBox.Text);

            AddMessage("system", "✓ System prompt обновлён. Начат новый диалог.");
            SystemPromptPanel.Visibility = Visibility.Collapsed;
        }

        private void ClearSystemPrompt_Click(object sender, RoutedEventArgs e)
        {
            SystemPromptTextBox.Text = "";
            _apiService.SetSystemPrompt(null);
            AddMessage("system", "System prompt очищен");
        }

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            _apiService.ResetConversation();
            _messages.Clear();
            _pendingAttachments.Clear();
            AddMessage("assistant", "Начат новый диалог. Чем могу помочь?");
        }

        /// <summary>
        /// Многострочный ввод: Enter = новая строка, Ctrl+Enter = отправить
        /// </summary>
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+Enter — отправить
                SendMessage();
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (Clipboard.ContainsImage())
                {
                    PasteFromClipboard();
                    e.Handled = true;
                }
                // Если не картинка — стандартная вставка текста (не перехватываем)
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessage();

        #endregion

        #region ═══ SEND MESSAGE ═══

        private async void SendMessage()
        {
            var message = InputTextBox.Text?.Trim();
            var hasAttachments = _pendingAttachments.Count > 0;

            if (string.IsNullOrEmpty(message) && !hasAttachments)
                return;

            if (_isProcessing)
                return;

            InputTextBox.Text = "";

            var currentAttachments = hasAttachments
                ? new ObservableCollection<AttachedFile>(_pendingAttachments)
                : null;

            AddMessage("user", message ?? (hasAttachments ? $"[{_pendingAttachments.Count} файл(ов)]" : ""), currentAttachments);

            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            SetProcessing(true);

            try
            {
                List<string> imageUrls = null;
                List<string> audioUrls = null;

                if (hasAttachments)
                {
                    var uploadResult = await UploadPendingFilesAsync(_cancellationTokenSource.Token);
                    imageUrls = uploadResult.imageUrls;
                    audioUrls = uploadResult.audioUrls;

                    var failedFiles = _pendingAttachments.Where(a => a.HasError).ToList();
                    if (failedFiles.Any())
                    {
                        var failedNames = string.Join(", ", failedFiles.Select(f => f.Name));
                        AddMessage("system", $"⚠️ Не удалось загрузить: {failedNames}");
                    }

                    _pendingAttachments.Clear();
                }

                if (imageUrls != null && imageUrls.Count == 0) imageUrls = null;
                if (audioUrls != null && audioUrls.Count == 0) audioUrls = null;

                var response = await _apiService.SendMessageWithFilesAsync(
                    message, imageUrls, audioUrls, _selectedModel, _cancellationTokenSource.Token);

                if (response?.Choices?.Count > 0)
                {
                    var content = response.Choices[0].Message?.Content ?? "Нет ответа";
                    AddMessage("assistant", content);
                }
                else
                {
                    AddMessage("assistant", "Пустой ответ от API");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddMessage("system", $"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                SetProcessing(false);
            }
        }

        #endregion
    }

    // ═══════════════════════════════════════════════════════════════════
    // CONVERTERS
    // ═══════════════════════════════════════════════════════════════════

    public class BoolToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (bool)value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Инвертированный BoolToVisibility: true → Collapsed, false → Visible
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long size)
            {
                if (size < 1024) return $"{size} B";
                if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
                return $"{size / (1024.0 * 1024.0):F1} MB";
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}