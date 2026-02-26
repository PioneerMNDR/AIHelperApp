using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AIHelperApp.Data
{
    public static class NativeMethods
    {
        // Флаги DisplayAffinity
        public const uint WDA_NONE = 0x00000000;
        public const uint WDA_MONITOR = 0x00000001;          // Чёрный прямоугольник при захвате
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Полностью невидимо (Win10 2004+)

        [DllImport("user32.dll")]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);



    }
}
