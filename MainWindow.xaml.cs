using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SecureWindowApp
{
    public partial class MainWindow : Window
    {
        // Eindeutige IDs für die beiden Hotkeys innerhalb der App.
        private const int HOTKEY_ID_ENABLE = 9000;
        private const int HOTKEY_ID_DISABLE = 9001;

        private GlobalHotKey? _enableHotKey;
        private GlobalHotKey? _disableHotKey;

        public MainWindow()
        {
            InitializeComponent();

            // Reagiert auch, wenn der Zustand woanders geändert wird
            // (z. B. über den globalen Handler in App.xaml.cs).
            CaptureProtection.EnabledChanged += _ => Dispatcher.Invoke(RefreshStatusUi);

            Closed += (_, _) =>
            {
                _enableHotKey?.Dispose();
                _disableHotKey?.Dispose();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Sofortiger Schutz + UI-Rückmeldung für dieses Fenster.
            // Alle weiteren Fenster der App werden zusätzlich automatisch
            // über den globalen Handler in App.xaml.cs abgesichert.
            var hwnd = new WindowInteropHelper(this).Handle;
            var result = CaptureProtection.Apply(hwnd);
            UpdateStatusUi(result);

            RegisterHotKeys();
        }

        private void RegisterHotKeys()
        {
            // Strg+Umschalt+E = Aktivieren, Strg+Umschalt+D = Deaktivieren.
            // Bewusst ohne Alt, da Strg+Alt auf deutschen Tastaturen mit
            // AltGr kollidieren kann.
            uint modifiers = HotKeyModifiers.Control | HotKeyModifiers.Shift;

            _enableHotKey = new GlobalHotKey(
                this, HOTKEY_ID_ENABLE, modifiers,
                (uint)KeyInterop.VirtualKeyFromKey(Key.E));
            _enableHotKey.Pressed += () => CaptureProtection.SetEnabled(true);

            _disableHotKey = new GlobalHotKey(
                this, HOTKEY_ID_DISABLE, modifiers,
                (uint)KeyInterop.VirtualKeyFromKey(Key.D));
            _disableHotKey.Pressed += () => CaptureProtection.SetEnabled(false);
        }

        private void RefreshStatusUi()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Nur zur Anzeige neu auswerten, ohne die API erneut aufzurufen
            // (SetEnabled hat den Zustand bereits auf alle Fenster angewendet).
            UpdateStatusUi(new CaptureProtection.Result
            {
                Success = true,
                ProtectionEnabled = CaptureProtection.IsEnabled,
                UsedExcludeFromCapture = CaptureProtection.SupportsExcludeFromCapture,
                Win32Error = 0
            });
        }

        private void UpdateStatusUi(CaptureProtection.Result result)
        {
            if (!result.ProtectionEnabled)
            {
                StatusText.Text = "Schutz deaktiviert (Strg+Umschalt+E zum Aktivieren)";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86)); // grau
            }
            else if (result.Success)
            {
                if (result.UsedExcludeFromCapture)
                {
                    StatusText.Text = "Geschützt vor Bildschirmaufnahmen (Strg+Umschalt+D zum Deaktivieren)";
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)); // grün
                }
                else
                {
                    StatusText.Text = "Nur eingeschränkter Schutz (WDA_MONITOR) – Windows zu alt";
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)); // gelb
                }
            }
            else
            {
                StatusText.Text = $"Schutz konnte nicht aktiviert werden (Fehlercode {result.Win32Error})";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)); // rot
            }

            OsInfoText.Text = $"Windows Build {Environment.OSVersion.Version.Build}";
        }
    }
}
