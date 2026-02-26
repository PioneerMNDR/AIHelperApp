using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIHelperApp.Data;

namespace AIHelperApp.Controls
{
    public partial class MainContent : UserControl
    {
        private IntPtr _hwnd;
        private bool _isHidden = true;

        public MainContent()
        {
            InitializeComponent();
        }

        /// <summary>Вызывается из окна-хоста после получения HWND.</summary>
        public void Initialize(IntPtr hwnd)
        {
            _hwnd = hwnd;
            ApplyStealthProtection(_isHidden);
            UpdateStealthUI();
        }

        /// <summary>Освобождение ресурсов при закрытии.</summary>
        public void Cleanup()
        {
            ChatCtrl?.Cleanup();
        }

        /// <summary>Обработка горячих клавиш. Вызывается из Window.KeyDown.</summary>
        public bool HandleKeyDown(KeyEventArgs e)
        {
            if ((e.Key == Key.OemTilde || e.Key == Key.Oem3) &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleStealth();
                e.Handled = true;
                return true;
            }
            return false;
        }

        #region ═══ STEALTH MODE ═══

        private void StealthToggle_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleStealth();
        }

        private void ToggleStealth()
        {
            _isHidden = !_isHidden;
            ApplyStealthProtection(_isHidden);

            var sb = (Storyboard)FindResource(_isHidden ? "ToggleOn" : "ToggleOff");
            sb.Begin(this);

            UpdateStealthUI();
        }

        private void ApplyStealthProtection(bool hide)
        {
            if (_hwnd == IntPtr.Zero) return;

            uint flag = hide
                ? NativeMethods.WDA_EXCLUDEFROMCAPTURE
                : NativeMethods.WDA_NONE;

            if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, flag) && hide)
                NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_MONITOR);
        }

        private void UpdateStealthUI()
        {
            if (_isHidden)
            {
                StealthIcon.Text = "🔒";
                StealthStatusText.Text = "Скрыто";
                StealthStatusText.Foreground = (Brush)FindResource("GreenBrush");
            }
            else
            {
                StealthIcon.Text = "👁";
                StealthStatusText.Text = "Видимо";
                StealthStatusText.Foreground = (Brush)FindResource("RedBrush");
            }
        }

        #endregion
    }
}