// Services/ScreenshotService.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace AIHelperApp.Services
{
    /// <summary>
    /// Сервис захвата скриншотов экрана
    /// </summary>
    public class ScreenshotService : IDisposable
    {
        private readonly string _screenshotsDir;
        private int _sessionCounter;
        private bool _disposed;

        // ═══ P/Invoke для получения реальных размеров экрана ═══
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int DESKTOPHORZRES = 118;
        private const int DESKTOPVERTRES = 117;

        public string ScreenshotsDirectory => _screenshotsDir;

        public ScreenshotService()
        {
            _screenshotsDir = Path.Combine(
                Path.GetTempPath(),
                "AIHelper",
                "screenshots"
            );

            try
            {
                Directory.CreateDirectory(_screenshotsDir);
                Debug.WriteLine($"[Screenshot] Directory: {_screenshotsDir}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Failed to create directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Сбрасывает счётчик для новой сессии
        /// </summary>
        public void ResetSessionCounter()
        {
            _sessionCounter = 0;
        }

        /// <summary>
        /// Захватывает скриншот основного экрана
        /// </summary>
        public ScreenshotResult CaptureScreen()
        {
            return CaptureScreenInternal(CaptureMode.PrimaryMonitor);
        }

        /// <summary>
        /// Захватывает скриншот всех мониторов
        /// </summary>
        public ScreenshotResult CaptureAllScreens()
        {
            return CaptureScreenInternal(CaptureMode.AllMonitors);
        }

        private ScreenshotResult CaptureScreenInternal(CaptureMode mode)
        {
            try
            {
                Rectangle bounds = GetScreenBounds(mode);

                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid screen bounds: {bounds.Width}x{bounds.Height}");
                }

                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);

                graphics.CopyFromScreen(
                    bounds.Location,
                    Point.Empty,
                    bounds.Size,
                    CopyPixelOperation.SourceCopy
                );

                // Генерируем уникальное имя
                _sessionCounter++;
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string filename = $"screenshot_{timestamp}_{_sessionCounter:D3}.png";
                string fullPath = Path.Combine(_screenshotsDir, filename);

                // Сохраняем PNG
                bitmap.Save(fullPath, ImageFormat.Png);
                Debug.WriteLine($"[Screenshot] Saved: {fullPath} ({bounds.Width}x{bounds.Height})");

                // Генерируем thumbnail (200px width)
                var thumbnail = CreateThumbnail(bitmap, 200);

                // Конвертируем в Base64
                string base64 = ConvertToBase64(bitmap);

                return new ScreenshotResult
                {
                    FilePath = fullPath,
                    FileName = filename,
                    Base64 = base64,
                    Thumbnail = thumbnail,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    CapturedAt = DateTime.Now,
                    ScreenshotNumber = _sessionCounter,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Capture failed: {ex.Message}");
                return new ScreenshotResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    CapturedAt = DateTime.Now
                };
            }
        }

        private Rectangle GetScreenBounds(CaptureMode mode)
        {
            if (mode == CaptureMode.AllMonitors)
            {
                // Виртуальный экран (все мониторы)
                int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
                int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                return new Rectangle(x, y, width, height);
            }
            else
            {
                // Основной монитор - учитываем DPI scaling
                IntPtr hdc = GetDC(IntPtr.Zero);
                try
                {
                    int width = GetDeviceCaps(hdc, DESKTOPHORZRES);
                    int height = GetDeviceCaps(hdc, DESKTOPVERTRES);

                    // Fallback если GetDeviceCaps вернул 0
                    if (width <= 0 || height <= 0)
                    {
                        width = GetSystemMetrics(SM_CXSCREEN);
                        height = GetSystemMetrics(SM_CYSCREEN);
                    }

                    return new Rectangle(0, 0, width, height);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }

        private BitmapImage CreateThumbnail(Bitmap original, int maxWidth)
        {
            try
            {
                // Рассчитываем пропорциональные размеры
                double ratio = (double)original.Height / original.Width;
                int newWidth = Math.Min(maxWidth, original.Width);
                int newHeight = (int)(newWidth * ratio);

                // Минимальные размеры
                newWidth = Math.Max(1, newWidth);
                newHeight = Math.Max(1, newHeight);

                using var thumbnail = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(thumbnail);

                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                graphics.DrawImage(original, 0, 0, newWidth, newHeight);

                // Конвертируем в WPF BitmapImage
                using var ms = new MemoryStream();
                thumbnail.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze(); // Важно для thread safety

                return bitmapImage;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Thumbnail creation failed: {ex.Message}");
                return null;
            }
        }

        private string ConvertToBase64(Bitmap bitmap)
        {
            try
            {
                using var ms = new MemoryStream();

                // Используем JPEG для меньшего размера (base64 для API)
                var encoder = GetEncoder(ImageFormat.Jpeg);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, 85L);

                bitmap.Save(ms, encoder, encoderParams);

                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Base64 conversion failed: {ex.Message}");

                // Fallback: PNG без оптимизации
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                    return codec;
            }
            return null;
        }

        /// <summary>
        /// Загружает полноразмерное изображение для просмотра
        /// </summary>
        public BitmapImage LoadFullImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Debug.WriteLine($"[Screenshot] File not found: {path}");
                return null;
            }

            try
            {
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.UriSource = new Uri(path, UriKind.Absolute);
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Failed to load image: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Удаляет скриншоты старше указанного количества дней
        /// </summary>
        public int CleanupOldScreenshots(int maxAgeDays = 7)
        {
            int deletedCount = 0;
            try
            {
                var cutoff = DateTime.Now.AddDays(-maxAgeDays);
                foreach (var file in Directory.GetFiles(_screenshotsDir, "*.png"))
                {
                    try
                    {
                        if (File.GetCreationTime(file) < cutoff)
                        {
                            File.Delete(file);
                            deletedCount++;
                        }
                    }
                    catch { }
                }

                if (deletedCount > 0)
                    Debug.WriteLine($"[Screenshot] Cleaned up {deletedCount} old screenshots");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Cleanup error: {ex.Message}");
            }

            return deletedCount;
        }

        /// <summary>
        /// Удаляет все скриншоты текущей сессии
        /// </summary>
        public void CleanupSessionScreenshots()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_screenshotsDir, "*.png"))
                {
                    try { File.Delete(file); } catch { }
                }
                Debug.WriteLine("[Screenshot] Session screenshots cleaned up");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screenshot] Session cleanup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Получает размер директории скриншотов в байтах
        /// </summary>
        public long GetDirectorySize()
        {
            try
            {
                var files = Directory.GetFiles(_screenshotsDir, "*", SearchOption.AllDirectories);
                long size = 0;
                foreach (var file in files)
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
                return size;
            }
            catch { return 0; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Ресурсы освобождаются в using-блоках методов
        }
    }

    /// <summary>
    /// Результат захвата скриншота
    /// </summary>
    public class ScreenshotResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Base64 { get; set; }
        public BitmapImage Thumbnail { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }
        public int ScreenshotNumber { get; set; }
        public DateTime CapturedAt { get; set; }

        /// <summary>
        /// Размер в МБ (приблизительный по Base64)
        /// </summary>
        public double SizeMB => string.IsNullOrEmpty(Base64)
            ? 0
            : Base64.Length * 3.0 / 4.0 / 1024.0 / 1024.0;
    }

    public enum CaptureMode
    {
        PrimaryMonitor,
        AllMonitors
    }
}