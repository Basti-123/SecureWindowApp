using System;
using System.Drawing;
using WinForms = System.Windows.Forms;

namespace SecureWindowApp
{
    /// <summary>
    /// Kleines Symbol im Infobereich der Taskleiste (neben der Uhr) mit
    /// Kontextmenue: Fenster zeigen/verstecken und Beenden.
    /// Nutzt System.Windows.Forms.NotifyIcon (WPF hat kein eigenes Tray-Symbol).
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ContextMenuStrip _menu;
        private bool _disposed;

        public TrayIconManager(Action toggleWindow, Action exitApp)
        {
            _menu = new WinForms.ContextMenuStrip();

            var toggleItem = new WinForms.ToolStripMenuItem("Fenster zeigen / verstecken");
            toggleItem.Click += (_, _) => toggleWindow();

            var exitItem = new WinForms.ToolStripMenuItem("Beenden");
            exitItem.Click += (_, _) => exitApp();

            _menu.Items.Add(toggleItem);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _icon = new WinForms.NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "Sicheres Fenster",
                ContextMenuStrip = _menu,
                Visible = true
            };

            // Doppelklick mit links = Fenster zeigen/verstecken.
            _icon.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                    toggleWindow();
            };

        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Wichtig: Symbol vor dem Beenden entfernen, sonst bleibt bis zum
            // naechsten Mausueberflug ein "Geister-Symbol" im Infobereich.
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();

            _disposed = true;
        }
    }
}
