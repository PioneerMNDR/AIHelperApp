using System.Windows;
using AIHelperApp.Helpers;
using Wpf.Ui.Controls;

namespace AIHelperApp
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            Window mainWindow;

            if (OsHelper.IsWindows11)
            {
                var win11 = new MainWindowWin11();

                // На 22H2+ — Acrylic (красивый blur)
                // На 21H2  — Mica (лёгкая прозрачность)
                win11.WindowBackdropType = OsHelper.IsWindows11_22H2
                    ? WindowBackdropType.Acrylic
                    : WindowBackdropType.Mica;

                mainWindow = win11;
            }
            else
            {
                mainWindow = new MainWindowWin10();
            }

            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }
}