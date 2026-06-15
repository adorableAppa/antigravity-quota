using System;
using System.Runtime.InteropServices;

namespace AntigravityQuota
{
    public static class GdiInterop
    {
        // ── GDI / AlphaBlend ────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hGDIOBJ);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(
            IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("msimg32.dll", SetLastError = true)]
        public static extern bool AlphaBlend(
            IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest,
            IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc,
            BLENDFUNCTION blendFunction);

        // ── DWM – Rounded corners (Windows 11+) ─────────────────────────────
        public enum DWM_WINDOW_CORNER_PREFERENCE
        {
            DWMWCP_DEFAULT    = 0,
            DWMWCP_DONOTROUND = 1,
            DWMWCP_ROUND      = 2,
            DWMWCP_ROUNDSMALL = 3
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// Applies Windows 11 DWM rounded corners to any window by handle.
        /// Silently ignored on Windows 10 and earlier.
        /// </summary>
        public static void ApplyRoundedCorners(
            IntPtr hwnd,
            DWM_WINDOW_CORNER_PREFERENCE preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND)
        {
            try
            {
                int value = (int)preference;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
            }
            catch { /* Silently ignore on older Windows */ }
        }
    }
}
