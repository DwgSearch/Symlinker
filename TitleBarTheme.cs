namespace Symlinker
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security;
    using System.Windows;
    using System.Windows.Interop;

    using Microsoft.Win32;

    /// <summary>Gives the native title bar a dark look when Windows apps are set to dark mode.</summary>
    internal static class TitleBarTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

        /// <summary>Call from the window's SourceInitialized event.</summary>
        public static void Apply(Window window)
        {
            try
            {
                var appsUseLightTheme = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    1);
                if (appsUseLightTheme is int light && light == 0)
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    var on = 1;
                    DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                }
            }
            catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
            {
                // Cosmetic only: keep the default title bar.
            }
        }
    }
}
