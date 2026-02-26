using System;
using System.Runtime.InteropServices;

namespace AIHelperApp
{
    internal static class AcrylicHelper
    {
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(
            IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor; // формат AABBGGRR (не AARRGGBB!)
            public int AnimationId;
        }

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;

        /// <summary>
        /// Включает Acrylic Blur на Windows 10.
        /// </summary>
        /// <param name="hwnd">Хэндл окна</param>
        /// <param name="opacity">Прозрачность фона 0-255 (0=полностью прозрачный)</param>
        /// <param name="r">Red 0-255</param>
        /// <param name="g">Green 0-255</param>
        /// <param name="b">Blue 0-255</param>
        public static void EnableAcrylic(IntPtr hwnd, byte opacity = 180,
            byte r = 30, byte g = 30, byte b = 46)
        {
            // GradientColor формат: AABBGGRR
            uint gradientColor = ((uint)opacity << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2, // ACCENT_FLAG_DRAW_BACKGROUND
                GradientColor = gradientColor
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = accentSize,
                Data = accentPtr
            };

            int result = SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);

            // Если Acrylic не сработал — пробуем обычный Blur
            if (result == 0)
            {
                EnableBlur(hwnd, opacity, r, g, b);
            }
        }

        /// <summary>
        /// Fallback: обычное размытие (работает на более старых Win10)
        /// </summary>
        private static void EnableBlur(IntPtr hwnd, byte opacity, byte r, byte g, byte b)
        {
            uint gradientColor = ((uint)opacity << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 2,
                GradientColor = gradientColor
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = accentSize,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }

        /// <summary>
        /// Отключает эффект
        /// </summary>
        public static void DisableAcrylic(IntPtr hwnd)
        {
            var accent = new AccentPolicy { AccentState = 0 };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = accentSize,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
    }
}