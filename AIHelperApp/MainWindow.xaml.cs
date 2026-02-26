using AIHelperApp.Data;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace AIHelperApp
{
    public partial class MainWindow : TianXiaTech.BlurWindow
    {
        private IntPtr _hwnd;
        private bool _isHidden = true;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyStealthProtection(_isHidden);
            UpdateStealthUI();
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Освобождаем ресурсы всех контролов при закрытии приложения
            ChatCtrl?.Cleanup();
        }

        #region ═══ STEALTH MODE ═══

        private void StealthToggle_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleStealth();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+` или Ctrl+Ё — переключение стелс-режима
            if ((e.Key == Key.OemTilde || e.Key == Key.Oem3) &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleStealth();
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
    }
}