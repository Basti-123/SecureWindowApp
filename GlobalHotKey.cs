using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SecureWindowApp
{
    /// <summary>
    /// Modifier-Flags für RegisterHotKey (winuser.h).
    /// </summary>
    public static class HotKeyModifiers
    {
        public const uint Alt = 0x0001;
        public const uint Control = 0x0002;
        public const uint Shift = 0x0004;
        public const uint Win = 0x0008;
    }

    /// <summary>
    /// Registriert eine systemweite Tastenkombination (funktioniert auch,
    /// wenn die Anwendung nicht im Vordergrund ist) und löst ein Ereignis
    /// aus, sobald sie gedrückt wird.
    /// </summary>
    public sealed class GlobalHotKey : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        private readonly IntPtr _hwnd;
        private readonly int _id;
        private readonly HwndSource? _source;
        private bool _registered;
        private bool _disposed;

        /// <summary>Wird aufgerufen, wenn die Tastenkombination gedrückt wird.</summary>
        public event Action? Pressed;

        /// <param name="window">Ein bereits initialisiertes Fenster (Handle muss existieren, also erst ab OnSourceInitialized aufrufen).</param>
        /// <param name="id">Eindeutige ID pro registrierter Kombination innerhalb der App.</param>
        /// <param name="modifiers">Kombination aus HotKeyModifiers-Flags.</param>
        /// <param name="virtualKey">Virtueller Tastencode, z. B. via System.Windows.Input.KeyInterop.VirtualKeyFromKey(Key.E).</param>
        public GlobalHotKey(Window window, int id, uint modifiers, uint virtualKey)
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            _id = id;

            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(HwndHook);

            _registered = RegisterHotKey(_hwnd, _id, modifiers, virtualKey);
            if (!_registered)
            {
                int error = Marshal.GetLastWin32Error();
                // Meist Fehlercode 1409 (ERROR_HOTKEY_ALREADY_REGISTERED),
                // wenn eine andere Anwendung dieselbe Kombination belegt.
                System.Diagnostics.Debug.WriteLine(
                    $"Hotkey-Registrierung fehlgeschlagen (ID {id}, Fehlercode {error}).");
            }
        }

        public bool IsRegistered => _registered;

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
            {
                Pressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_registered)
                UnregisterHotKey(_hwnd, _id);

            _source?.RemoveHook(HwndHook);
            _disposed = true;
        }
    }
}
