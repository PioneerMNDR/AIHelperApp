// Services/GlobalHotkeyService.cs
using NHotkey;
using NHotkey.Wpf;
using System;
using System.Diagnostics;
using System.Windows.Input;

namespace AIHelperApp.Services
{
    /// <summary>
    /// Сервис глобальных хоткеев для Interview Mode
    /// </summary>
    public class GlobalHotkeyService : IDisposable
    {
        // ═══ Events ═══
        public event Action? OnAnswerHotkey;           // F2
        public event Action? OnToggleRecordHotkey;     // F3
        public event Action? OnTogglePauseHotkey;      // F4
        public event Action? OnScreenshotHotkey;       // F5
        public event Action? OnScreenshotToAiHotkey;   // Shift+F5

        private bool _isRegistered;
        private bool _isEnabled = true;

        /// <summary>
        /// Включены ли хоткеи (можно временно отключить)
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => _isEnabled = value;
        }

        /// <summary>
        /// Зарегистрированы ли хоткеи
        /// </summary>
        public bool IsRegistered => _isRegistered;

        /// <summary>
        /// Регистрирует все глобальные хоткеи
        /// </summary>
        public void RegisterHotkeys()
        {
            if (_isRegistered) return;

            try
            {
                // F2 - Отправить в AI
                HotkeyManager.Current.AddOrReplace(
                    "Interview_F2_Answer",
                    Key.F2,
                    ModifierKeys.None,
                    OnF2Handler);

                // F3 - Начать/остановить запись
                HotkeyManager.Current.AddOrReplace(
                    "Interview_F3_Record",
                    Key.F3,
                    ModifierKeys.None,
                    OnF3Handler);

                // F4 - Пауза/продолжить
                HotkeyManager.Current.AddOrReplace(
                    "Interview_F4_Pause",
                    Key.F4,
                    ModifierKeys.None,
                    OnF4Handler);

                // F5 - Скриншот
                HotkeyManager.Current.AddOrReplace(
                    "Interview_F5_Screenshot",
                    Key.F5,
                    ModifierKeys.None,
                    OnF5Handler);

                // Shift+F5 - Скриншот сразу в AI
                HotkeyManager.Current.AddOrReplace(
                    "Interview_ShiftF5_ScreenshotAI",
                    Key.F5,
                    ModifierKeys.Shift,
                    OnShiftF5Handler);

                _isRegistered = true;
                Debug.WriteLine("[Hotkeys] Global hotkeys registered: F2, F3, F4, F5, Shift+F5");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Hotkeys] Failed to register hotkeys: {ex.Message}");

                // Попробуем зарегистрировать хотя бы часть
                TryRegisterFallback();
            }
        }

        /// <summary>
        /// Попытка зарегистрировать хоткеи с альтернативными комбинациями
        /// </summary>
        private void TryRegisterFallback()
        {
            try
            {
                // Альтернатива: Ctrl+Shift + клавиша
                HotkeyManager.Current.AddOrReplace(
                    "Interview_Alt_Answer",
                    Key.F2,
                    ModifierKeys.Control,
                    OnF2Handler);

                Debug.WriteLine("[Hotkeys] Fallback hotkeys registered (Ctrl+F2, etc.)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Hotkeys] Fallback registration also failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Отменяет регистрацию всех хоткеев
        /// </summary>
        public void UnregisterHotkeys()
        {
            if (!_isRegistered) return;

            try
            {
                HotkeyManager.Current.Remove("Interview_F2_Answer");
                HotkeyManager.Current.Remove("Interview_F3_Record");
                HotkeyManager.Current.Remove("Interview_F4_Pause");
                HotkeyManager.Current.Remove("Interview_F5_Screenshot");
                HotkeyManager.Current.Remove("Interview_ShiftF5_ScreenshotAI");

                // Fallback keys
                try { HotkeyManager.Current.Remove("Interview_Alt_Answer"); } catch { }

                _isRegistered = false;
                Debug.WriteLine("[Hotkeys] Global hotkeys unregistered");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Hotkeys] Error unregistering hotkeys: {ex.Message}");
            }
        }

        // ═══ Handlers ═══

        private void OnF2Handler(object? sender, HotkeyEventArgs e)
        {
            if (!_isEnabled) return;

            Debug.WriteLine("[Hotkey] F2 pressed - Answer");
            OnAnswerHotkey?.Invoke();
            e.Handled = true;
        }

        private void OnF3Handler(object? sender, HotkeyEventArgs e)
        {
            if (!_isEnabled) return;

            Debug.WriteLine("[Hotkey] F3 pressed - Toggle Record");
            OnToggleRecordHotkey?.Invoke();
            e.Handled = true;
        }

        private void OnF4Handler(object? sender, HotkeyEventArgs e)
        {
            if (!_isEnabled) return;

            Debug.WriteLine("[Hotkey] F4 pressed - Toggle Pause");
            OnTogglePauseHotkey?.Invoke();
            e.Handled = true;
        }

        private void OnF5Handler(object? sender, HotkeyEventArgs e)
        {
            if (!_isEnabled) return;

            Debug.WriteLine("[Hotkey] F5 pressed - Screenshot");
            OnScreenshotHotkey?.Invoke();
            e.Handled = true;
        }

        private void OnShiftF5Handler(object? sender, HotkeyEventArgs e)
        {
            if (!_isEnabled) return;

            Debug.WriteLine("[Hotkey] Shift+F5 pressed - Screenshot to AI");
            OnScreenshotToAiHotkey?.Invoke();
            e.Handled = true;
        }

        public void Dispose()
        {
            UnregisterHotkeys();
        }
    }
}