using Microsoft.Win32;
using System;

namespace AIHelperApp.Helpers
{
    public static class OsHelper
    {
        private static readonly Lazy<int> _build = new(() => GetBuildNumber());

        public static bool IsWindows11 => _build.Value >= 22000;
        public static bool IsWindows11_22H2 => _build.Value >= 22621;  // Tabbed Mica

        /// <summary>Лучший тип backdrop для текущей ОС.</summary>
        public static string RecommendedBackdrop
        {
            get
            {
                if (_build.Value >= 22621) return "Acrylic";   // Win 11 22H2+
                if (_build.Value >= 22000) return "Mica";      // Win 11 21H2
                return "None";                                  // Win 10
            }
        }

        private static int GetBuildNumber()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var s = key?.GetValue("CurrentBuildNumber")?.ToString();
                if (int.TryParse(s, out int b)) return b;
            }
            catch { }
            return Environment.OSVersion.Version.Build;
        }
    }
}