using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SecureWindowApp
{
    /// <summary>
    /// Zentrale Logik zum Schutz eines Fensters vor Bildschirmaufnahmen
    /// via SetWindowDisplayAffinity (WDA_EXCLUDEFROMCAPTURE).
    /// Wird von App.xaml.cs automatisch auf JEDES Fenster angewendet.
    /// </summary>
    public static class CaptureProtection
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_MONITOR = 0x00000001;
        // Erfordert Windows 10 Version 2004 (Build 19041) oder neuer.
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        public static bool SupportsExcludeFromCapture =>
            Environment.OSVersion.Version.Build >= 19041;

        /// <summary>
        /// Globaler Ein/Aus-Zustand. Wird von den Hotkeys umgeschaltet und
        /// von jedem Apply()-Aufruf (auch für neue Fenster) berücksichtigt.
        /// </summary>
        public static bool IsEnabled { get; private set; } = true;

        /// <summary>Wird ausgelöst, wenn sich IsEnabled ändert (für UI-Updates).</summary>
        public static event Action<bool>? EnabledChanged;

        public readonly struct Result
        {
            public bool Success { get; init; }
            public bool ProtectionEnabled { get; init; }
            public bool UsedExcludeFromCapture { get; init; }
            public int Win32Error { get; init; }
        }

        /// <summary>
        /// Wendet den aktuellen Schutzzustand (an/aus) auf das angegebene
        /// Fenster-Handle an. Kann gefahrlos mehrfach aufgerufen werden.
        /// </summary>
        public static Result Apply(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return new Result { Success = false, ProtectionEnabled = IsEnabled, UsedExcludeFromCapture = false, Win32Error = 0 };

            bool useExclude = SupportsExcludeFromCapture;
            uint affinity = !IsEnabled
                ? WDA_NONE
                : (useExclude ? WDA_EXCLUDEFROMCAPTURE : WDA_MONITOR);

            bool success = SetWindowDisplayAffinity(hwnd, affinity);
            int error = success ? 0 : Marshal.GetLastWin32Error();

            return new Result
            {
                Success = success,
                ProtectionEnabled = IsEnabled,
                UsedExcludeFromCapture = useExclude,
                Win32Error = error
            };
        }

        /// <summary>
        /// Schaltet den Schutz global an/aus und wendet den neuen Zustand
        /// sofort auf alle aktuell offenen Fenster der Anwendung an.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (IsEnabled == enabled)
                return;

            IsEnabled = enabled;

            foreach (Window window in Application.Current.Windows)
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                Apply(hwnd);
            }

            EnabledChanged?.Invoke(IsEnabled);
        }

        public static void Toggle() => SetEnabled(!IsEnabled);
    }
}
