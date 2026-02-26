using System.Windows;

using AIHelperApp.Misc;

namespace AIHelperApp
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            Window mainWindow;

            if (OsHelper.IsWindows11)
            {
                // Mica + FluentWindow + скруглённые углы
                mainWindow = new MainWindowWin11();
            }
            else
            {
                // Acrylic Blur для Windows 10
                mainWindow = new MainWindowWin10();
            }

            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }
}