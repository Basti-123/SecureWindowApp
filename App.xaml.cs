using System.Windows;
using System.Windows.Interop;

namespace SecureWindowApp
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Wendet den Aufnahmeschutz automatisch auf JEDES Fenster der
            // Anwendung an, sobald es geladen wird - auch auf Fenster, die
            // erst spaeter zur Laufzeit erzeugt werden (z. B. Dialoge).
            // Dadurch muss kein neues Fenster manuell abgesichert werden.
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyWindowLoaded));
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
