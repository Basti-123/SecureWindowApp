using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

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

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

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
                window.BrowserInitializationCompleted += async success =>
                    Shutdown(success && await window.VerifyShellAsync() ? 0 : 1);
            }

            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

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

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogUnhandledException("Dispatcher", e.Exception);

            // Die Ausnahme ist bereits protokolliert. Das verhindert, dass ein
            // einzelner fehlerhafter UI-Callback die gesamte App beendet.
            e.Handled = true;
            if (IsSmokeTest)
                Shutdown(1);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogUnhandledException("Task", e.Exception);
            e.SetObserved();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
                LogUnhandledException("AppDomain", exception);
            else
                LogUnhandledException("AppDomain", new Exception(e.ExceptionObject?.ToString()));
        }

        private static void LogUnhandledException(string source, Exception exception)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SecureWindowApp");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "application-failures.log"),
                    $"[{DateTime.Now:O}] Quelle: {source}{Environment.NewLine}{exception}{Environment.NewLine}");
            }
            catch
            {
                // Fehlerdiagnostik darf einen bereits fehlerhaften Prozess nicht weiter stoeren.
            }
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
