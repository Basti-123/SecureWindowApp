using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace SecureWindowApp
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;
        public static bool IsSmokeTest { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: "Local\\SecureWindowApp.SingleInstance",
                createdNew: out bool createdNew);

            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown(0);
                return;
            }

            // Wendet den Aufnahmeschutz automatisch auf JEDES Fenster der
            // Anwendung an, sobald es geladen wird - auch auf Fenster, die
            // erst spaeter zur Laufzeit erzeugt werden (z. B. Dialoge).
            // Dadurch muss kein neues Fenster manuell abgesichert werden.
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyWindowLoaded));

            base.OnStartup(e);

            foreach (string argument in e.Args)
            {
                if (string.Equals(argument, "--smoke-test", System.StringComparison.OrdinalIgnoreCase))
                {
                    IsSmokeTest = true;
                    break;
                }
            }

            var window = new MainWindow();
            MainWindow = window;

            if (IsSmokeTest)
            {
                window.BrowserInitializationCompleted += success => Shutdown(success ? 0 : 1);
            }

            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Der Mutex wurde bereits vom Betriebssystem freigegeben.
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }

        private void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window)
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                CaptureProtection.Apply(hwnd);
            }
        }
    }
}
