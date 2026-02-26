using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIHelperApp.Misc
{
    public static class OsHelper
    {
        private static readonly Lazy<bool> _isWin11 = new(() => DetectWindows11());

        public static bool IsWindows11 => _isWin11.Value;

        private static bool DetectWindows11()
        {
            try
            {
                // Надёжный способ — через реестр (работает и на .NET Framework)
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

                var buildStr = key?.GetValue("CurrentBuildNumber")?.ToString();
                if (int.TryParse(buildStr, out int build))
                    return build >= 22000;   // Win 11 начинается с build 22000
            }
            catch { }

            // Fallback
            return Environment.OSVersion.Version.Build >= 22000;
        }
    }
}
