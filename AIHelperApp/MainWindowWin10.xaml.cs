using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AIHelperApp
{
    public partial class MainWindowWin10 : TianXiaTech.BlurWindow
    {
        public MainWindowWin10()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                ContentArea.Initialize(hwnd);
            };
            Closing += (_, __) => ContentArea.Cleanup();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
            => ContentArea.HandleKeyDown(e);
    }
}