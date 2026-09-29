using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SecureWindowApp
{
    public partial class MainWindow : Window
    {
        // Startseite des eingebetteten Browsers und Suchmaschine fuer Eingaben
        // in der Adressleiste, die keine Adresse sind.
        private const string HomeUrl = "https://www.google.com";
        private const string SearchUrl = "https://www.google.com/search?q=";

        private TrayIconManager? _tray;
        private CoreWebView2Environment? _webEnvironment;
        private readonly ObservableCollection<BrowserTabState> _tabs = new();
        private bool _browserInitializationStarted;
        private bool _browserInitializationFailed;
        private bool _blockedByProtectionFailure;
        private bool _protectionApiHealthy;
        private bool _tabCreationInProgress;
        private bool _addressBoxUpdating;
        private readonly string _favoritesFilePath;
        private readonly string _webView2DiagnosticsFilePath;
        private readonly List<Favorite> _favorites = new();
        private bool _updatingFavorites;
        private bool _audioPlaying;

        public event Action<bool>? BrowserInitializationCompleted;

        public ObservableCollection<BrowserTabState> Tabs => _tabs;

        private BrowserTabState? CurrentTab => BrowserTabStrip.SelectedItem as BrowserTabState;

        private CoreWebView2? CurrentCoreWebView2 => CurrentTab?.WebView.CoreWebView2;

        public MainWindow()
        {
            _favoritesFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SecureWindowApp", "favorites.json");
            _webView2DiagnosticsFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SecureWindowApp", "webview2-process-failures.log");

            InitializeComponent();
            LoadFavorites();
            RefreshFavorites();

            // Hält die Statusanzeige synchron mit der Einstellung im Fenster.
            CaptureProtection.EnabledChanged += OnProtectionEnabledChanged;

            // Tray-Symbol: "Fenster zeigen/verstecken" und "Beenden".
            if (!App.IsSmokeTest)
                _tray = new TrayIconManager(ToggleVisibility, Close);

            Loaded += async (_, _) => await StartBrowserIfAllowedAsync();

            Closed += (_, _) =>
            {
                CaptureProtection.EnabledChanged -= OnProtectionEnabledChanged;
                _tray?.Dispose();

                foreach (var tab in new List<BrowserTabState>(_tabs))
                {
                    try
                    {
                        tab.WebView.Dispose();
                    }
                    catch
                    {
                        // Beim Beenden darf ein bereits beendeter WebView2-
                        // Controller den App-Schluss nicht verhindern.
                    }
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Sofortiger Schutz + UI-Rueckmeldung fuer dieses Fenster - noch
            // bevor das Fenster (und damit der Browser) erstmals sichtbar wird.
            var hwnd = new WindowInteropHelper(this).Handle;
            var result = CaptureProtection.Apply(hwnd);
            HandleProtectionResult(result);
        }

        // ------------------------------------------------------------------
        // Zeigen / Verstecken
        // ------------------------------------------------------------------

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Hide();
            }
        }

        private void ToggleVisibility()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Topmost = true;
                Topmost = false;
                Activate();
            }
        }

        // ------------------------------------------------------------------
        // Einstellungen
        // ------------------------------------------------------------------

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ProtectionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            CaptureProtection.SetEnabled(ProtectionCheckBox.IsChecked == true);
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom((CurrentTab?.WebView.ZoomFactor ?? 1.0) - 0.1);
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom((CurrentTab?.WebView.ZoomFactor ?? 1.0) + 0.1);
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(1.0);
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentCoreWebView2 == null)
                return;

            CurrentCoreWebView2.IsMuted = !CurrentCoreWebView2.IsMuted;
            UpdateMuteButton();
        }

        private async void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (_webEnvironment == null || !_protectionApiHealthy || _tabCreationInProgress)
                return;

            await CreateTabAsync(HomeUrl, selectTab: true);
        }

        private void SetZoom(double zoomFactor)
        {
            if (CurrentTab == null)
                return;

            CurrentTab.WebView.ZoomFactor = Math.Clamp(zoomFactor, 0.5, 2.0);
            UpdateZoomText();
        }

        private void UpdateZoomText()
        {
            double zoom = CurrentTab?.WebView.ZoomFactor ?? 1.0;
            ZoomText.Text = $"{Math.Round(zoom * 100):0} %";
        }

        private async void ClearBrowsingDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentCoreWebView2 == null)
            {
                SettingsMessage.Text = "Der Browser ist noch nicht bereit.";
                return;
            }

            try
            {
                await CurrentCoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllProfile);
                SettingsMessage.Text = "Cookies, Cache und Browserdaten wurden gelöscht.";
            }
            catch (Exception ex)
            {
                SettingsMessage.Text = "Browserdaten konnten nicht gelöscht werden: " + ex.Message;
            }
        }

        private void AddFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentCoreWebView2 == null || !IsAllowedNavigation(CurrentCoreWebView2.Source))
            {
                SettingsMessage.Text = "Diese Seite kann nicht als Favorit gespeichert werden.";
                return;
            }

            string url = CurrentCoreWebView2.Source;
            string title = CurrentCoreWebView2.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title))
                title = url;

            var existing = _favorites.Find(favorite =>
                string.Equals(favorite.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                _favorites.Add(new Favorite { Title = title, Url = url });
            else
                existing.Title = title;

            SaveFavorites();
            RefreshFavorites();
            SettingsMessage.Text = "Favorit gespeichert.";
        }

        private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (FavoritesBox.SelectedItem is not Favorite favorite)
            {
                SettingsMessage.Text = "Bitte zuerst einen Favoriten auswählen.";
                return;
            }

            _favorites.Remove(favorite);
            SaveFavorites();
            RefreshFavorites();
            SettingsMessage.Text = "Favorit entfernt.";
        }

        private void FavoritesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingFavorites || FavoritesBox.SelectedItem is not Favorite favorite)
                return;

            NavigateTo(favorite.Url);
            FavoritesBox.SelectedIndex = -1;
        }

        private void LoadFavorites()
        {
            try
            {
                if (!File.Exists(_favoritesFilePath))
                    return;

                var favorites = JsonSerializer.Deserialize<List<Favorite>>(
                    File.ReadAllText(_favoritesFilePath));
                if (favorites != null)
                    _favorites.AddRange(favorites.FindAll(favorite => IsAllowedNavigation(favorite.Url)));
            }
            catch
            {
                // Eine beschädigte Favoritendatei darf den Browserstart nicht verhindern.
                _favorites.Clear();
            }
        }

        private void SaveFavorites()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_favoritesFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(_favoritesFilePath,
                    JsonSerializer.Serialize(_favorites, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                SettingsMessage.Text = "Favoriten konnten nicht gespeichert werden: " + ex.Message;
            }
        }

        private void RefreshFavorites()
        {
            _updatingFavorites = true;
            FavoritesBox.ItemsSource = null;
            FavoritesBox.ItemsSource = _favorites;
            _updatingFavorites = false;
        }

        private void OnProtectionEnabledChanged(bool _)
        {
            if (Dispatcher.CheckAccess())
            {
                RefreshStatusUi();
                return;
            }

            Dispatcher.BeginInvoke(new Action(RefreshStatusUi));
        }

        // ------------------------------------------------------------------
        // Eingebetteter Browser (WebView2) und Tabs
        // ------------------------------------------------------------------

        private async Task StartBrowserIfAllowedAsync()
        {
            if (_browserInitializationStarted || !_protectionApiHealthy)
                return;

            _browserInitializationStarted = true;
            await InitBrowserAsync();
        }

        private async Task InitBrowserAsync()
        {
            try
            {
                // Alle Tabs teilen sich eine WebView2-Umgebung und damit das
                // gleiche Profil, bleiben aber eigene Browseransichten.
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SecureWindowApp", "WebView2");

                _webEnvironment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);

                await CreateTabAsync(HomeUrl, selectTab: true);
            }
            catch (Exception ex)
            {
                _browserInitializationFailed = true;
                ShowBrowserError(
                    "Der eingebettete Browser (WebView2) konnte nicht gestartet werden.\n\n" +
                    "Bitte zuerst \"Start-SecureWindowApp.bat\" verwenden oder die " +
                    "\"WebView2 Runtime\" von Microsoft installieren:\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                    "Details: " + ex.Message);
                BrowserInitializationCompleted?.Invoke(false);
            }
        }

        private async Task CreateTabAsync(string initialUrl, bool selectTab)
        {
            if (_webEnvironment == null || _tabCreationInProgress)
                return;

            _tabCreationInProgress = true;
            BrowserTabState? state = null;
            try
            {
                bool isFirstTab = _tabs.Count == 0;
                var webView = new WebView2
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Visibility = Visibility.Collapsed
                };
                state = new BrowserTabState
                {
                    WebView = webView
                };

                // Jeder WebView2-HwndHost wird genau einmal in denselben
                // WPF-Host eingefügt. Beim Tabwechsel ändern wir nur noch die
                // Sichtbarkeit und hängen native Fenster nicht ständig um.
                _tabs.Add(state);
                BrowserContentHost.Children.Add(webView);
                if (selectTab)
                    BrowserTabStrip.SelectedItem = state;

                UpdateSelectedTabUi();
                await webView.EnsureCoreWebView2Async(_webEnvironment);
                var core = webView.CoreWebView2;

                webView.ZoomFactorChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                        UpdateZoomText();
                };

                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;

                core.NavigationStarting += (_, args) => OnNavigationStarting(state, args);
                core.DownloadStarting += (_, args) =>
                {
                    // Der Standard-Downloaddialog ist ein separates,
                    // ungeschuetztes Fenster. Downloads werden daher im
                    // Sicherheitsprofil vollstaendig abgebrochen.
                    args.Cancel = true;
                    args.Handled = true;
                };

                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.print = function () { return false; };");

                // Popups werden blockiert, damit keine ungeschuetzten separaten
                // Browserfenster entstehen. Normale Navigation bleibt moeglich.
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.ProcessFailed += (_, args) => OnWebViewProcessFailed(state!, args);

                core.SourceChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state) && !AddressBox.IsKeyboardFocusWithin)
                        SetAddressBoxText(core.Source);
                };
                core.HistoryChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                        UpdateNavButtons();
                };
                core.DocumentTitleChanged += (_, _) =>
                {
                    state.Title = GetTabTitle(core.DocumentTitle);
                    if (ReferenceEquals(CurrentTab, state))
                        UpdateWindowTitle(core.DocumentTitle);
                };
                core.IsDocumentPlayingAudioChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                    {
                        _audioPlaying = core.IsDocumentPlayingAudio;
                        UpdateMuteButton();
                    }
                };
                core.IsMutedChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                        UpdateMuteButton();
                };
                core.NavigationCompleted += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                    {
                        StopButton.IsEnabled = false;
                        UpdateNavButtons();
                    }
                };

                state.IsInitialized = true;
                state.IsReadyForClose = true;
                state.ErrorMessage = null;
                state.Title = "Neuer Tab";
                core.Navigate(initialUrl);

                if (isFirstTab)
                {
                    _browserInitializationFailed = false;
                    BrowserInitializationCompleted?.Invoke(true);
                }

                UpdateSelectedTabUi();
            }
            catch (Exception ex)
            {
                if (state == null)
                {
                    _browserInitializationFailed = true;
                    BrowserInitializationCompleted?.Invoke(false);
                }
                else
                {
                    state.InitializationFailed = true;
                    state.IsReadyForClose = true;
                    state.ErrorMessage =
                        "Dieser Tab konnte nicht gestartet werden.\n\nDetails: " + ex.Message;
                    if (_tabs.Contains(state) && !BrowserContentHost.Children.Contains(state.WebView))
                        BrowserContentHost.Children.Add(state.WebView);

                    if (_tabs.Count == 1)
                    {
                        _browserInitializationFailed = true;
                        BrowserInitializationCompleted?.Invoke(false);
                    }

                    UpdateSelectedTabUi();
                }
            }
            finally
            {
                _tabCreationInProgress = false;

                UpdateSelectedTabUi();
            }
        }

        private static string GetTabTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "Neuer Tab";

            return title.Length <= 24 ? title : title[..24] + "…";
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BrowserTabState state)
                CloseTab(state);
        }

        private void CloseTab(BrowserTabState state)
        {
            if (!state.IsReadyForClose)
                return;

            if (_tabs.Count == 1)
            {
                SettingsMessage.Text = "Der letzte Tab kann nicht geschlossen werden.";
                return;
            }

            int index = _tabs.IndexOf(state);
            bool wasSelected = ReferenceEquals(CurrentTab, state);
            BrowserContentHost.Children.Remove(state.WebView);
            _tabs.Remove(state);

            try
            {
                state.WebView.Dispose();
            }
            catch
            {
                // Ein bereits beendeter WebView2-Controller darf das
                // Schließen eines Tabs nicht zum App-Absturz machen.
            }

            if (wasSelected && _tabs.Count > 0)
                BrowserTabStrip.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];

            UpdateSelectedTabUi();
        }

        private void BrowserTabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != BrowserTabStrip)
                return;

            UpdateSelectedTabUi();
        }

        private void UpdateSelectedTabUi()
        {
            var state = CurrentTab;
            if (state == null)
            {
                NewTabButton.IsEnabled = !_tabCreationInProgress &&
                    _webEnvironment != null && _protectionApiHealthy;
                foreach (var tab in _tabs)
                    tab.WebView.Visibility = Visibility.Collapsed;
                MuteButton.IsEnabled = false;
                UpdateZoomText();
                UpdateNavButtons();
                return;
            }

            NewTabButton.IsEnabled = !_tabCreationInProgress &&
                _webEnvironment != null && _protectionApiHealthy;
            bool showTabError = state.InitializationFailed;
            foreach (var tab in _tabs)
            {
                bool isVisible = ReferenceEquals(tab, state) &&
                    !tab.InitializationFailed && !_blockedByProtectionFailure;
                tab.WebView.Visibility = isVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_blockedByProtectionFailure)
            {
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            ErrorText.Visibility = showTabError ? Visibility.Visible : Visibility.Collapsed;
            if (showTabError)
                ErrorText.Text = state.ErrorMessage ?? "Dieser Tab konnte nicht gestartet werden.";

            if (state.IsInitialized && state.WebView.CoreWebView2 != null)
            {
                SetAddressBoxText(state.WebView.CoreWebView2.Source);
                UpdateWindowTitle(state.WebView.CoreWebView2.DocumentTitle);
            }

            _audioPlaying = state.WebView.CoreWebView2?.IsDocumentPlayingAudio == true;
            UpdateMuteButton();
            UpdateZoomText();
            UpdateNavButtons();
        }

        private void OnNavigationStarting(BrowserTabState state, CoreWebView2NavigationStartingEventArgs args)
        {
            if (ReferenceEquals(CurrentTab, state))
                StopButton.IsEnabled = true;

            if (!IsAllowedNavigation(args.Uri))
                args.Cancel = true;
        }

        private static bool IsAllowedNavigation(string? address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return false;

            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateNavButtons()
        {
            var tab = CurrentTab;
            BackButton.IsEnabled = tab?.WebView.CoreWebView2 != null && tab.WebView.CanGoBack;
            ForwardButton.IsEnabled = tab?.WebView.CoreWebView2 != null && tab.WebView.CanGoForward;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab?.WebView.CoreWebView2 != null && CurrentTab.WebView.CanGoBack)
                CurrentTab.WebView.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab?.WebView.CoreWebView2 != null && CurrentTab.WebView.CanGoForward)
                CurrentTab.WebView.GoForward();
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentCoreWebView2?.Reload();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.WebView.Stop();
            StopButton.IsEnabled = false;
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentCoreWebView2?.Navigate(HomeUrl);
        }

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            NavigateTo(ResolveInput(AddressBox.Text));
            CurrentTab?.WebView.Focus();
        }

        private void NavigateTo(string address)
        {
            try
            {
                CurrentCoreWebView2?.Navigate(address);
            }
            catch (Exception ex)
            {
                SettingsMessage.Text = "Navigation konnte nicht gestartet werden: " + ex.Message;
            }
        }

        private void SetAddressBoxText(string? address)
        {
            if (_addressBoxUpdating || AddressBox.IsKeyboardFocusWithin)
                return;

            try
            {
                _addressBoxUpdating = true;
                string value = address ?? string.Empty;
                if (!string.Equals(AddressBox.Text, value, StringComparison.Ordinal))
                    AddressBox.Text = value;
            }
            finally
            {
                _addressBoxUpdating = false;
            }
        }

        private void OnWebViewProcessFailed(
            BrowserTabState state,
            CoreWebView2ProcessFailedEventArgs args)
        {
            string details =
                $"{args.ProcessFailedKind}; Grund: {args.Reason}; " +
                $"Prozess: {args.ProcessDescription}; Exit-Code: {args.ExitCode}";
            LogWebViewProcessFailure(details);

            void ShowFailure()
            {
                if (state.InitializationFailed)
                    return;

                state.InitializationFailed = true;
                state.IsReadyForClose = true;
                state.ErrorMessage =
                    "Der WebView2-Browserprozess wurde beendet oder ist abgestürzt.\n\n" +
                    "Bitte diesen Tab schließen und einen neuen Tab öffnen.\n\n" +
                    "Details: " + details;

                if (ReferenceEquals(CurrentTab, state))
                {
                    ErrorText.Visibility = Visibility.Visible;
                    ErrorText.Text = state.ErrorMessage;
                }

                UpdateSelectedTabUi();
            }

            if (Dispatcher.CheckAccess())
                ShowFailure();
            else
                Dispatcher.BeginInvoke(new Action(ShowFailure));
        }

        private void LogWebViewProcessFailure(string details)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_webView2DiagnosticsFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(
                    _webView2DiagnosticsFilePath,
                    $"[{DateTime.Now:O}] {details}{Environment.NewLine}");
            }
            catch
            {
                // Ein Diagnose-Log darf die Browserfunktion nicht stoeren.
            }
        }

        private void UpdateWindowTitle(string? documentTitle)
        {
            Title = string.IsNullOrWhiteSpace(documentTitle)
                ? "Sicheres Fenster"
                : $"{documentTitle} – Sicheres Fenster";
        }

        private void UpdateMuteButton()
        {
            var core = CurrentCoreWebView2;
            MuteButton.IsEnabled = core != null;
            if (core == null)
            {
                MuteButton.Content = "🔊";
                MuteButton.ToolTip = "Ton stummschalten";
                return;
            }

            bool muted = core.IsMuted;
            MuteButton.Content = muted ? "🔇" : "🔊";
            MuteButton.ToolTip = muted
                ? "Ton einschalten"
                : (_audioPlaying ? "Ton stummschalten (Audio aktiv)" : "Ton stummschalten");
        }

        private void ShowBrowserError(string message)
        {
            foreach (var tab in _tabs)
                tab.WebView.Visibility = Visibility.Collapsed;

            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = message;
            NewTabButton.IsEnabled = false;
            MuteButton.IsEnabled = false;
            UpdateNavButtons();
        }

        /// <summary>
        /// Macht aus der Eingabe eine Adresse: Mit http(s):// unveraendert,
        /// sieht es wie ein Hostname aus (Punkt, keine Leerzeichen) wird https://
        /// vorangestellt, alles andere wird in der Suchmaschine gesucht.
        /// </summary>
        private static string ResolveInput(string raw)
        {
            string text = raw.Trim();
            if (text.Length == 0)
                return HomeUrl;

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return text;

            bool looksLikeHost = !text.Contains(' ') &&
                (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));

            return looksLikeHost
                ? "https://" + text
                : SearchUrl + Uri.EscapeDataString(text);
        }

        // ------------------------------------------------------------------
        // Statusanzeige
        // ------------------------------------------------------------------

        private void RefreshStatusUi()
        {
            // Den tatsächlichen Zustand dieses HWND erneut prüfen. Dadurch wird
            // ein fehlgeschlagener SetWindowDisplayAffinity-Aufruf nicht als
            // erfolgreicher Schutz angezeigt.
            var hwnd = new WindowInteropHelper(this).Handle;
            HandleProtectionResult(CaptureProtection.Apply(hwnd));
        }

        private void HandleProtectionResult(CaptureProtection.Result result)
        {
            _protectionApiHealthy = result.Success;
            ProtectionCheckBox.IsChecked = CaptureProtection.IsEnabled;
            UpdateStatusUi(result);

            if (!result.Success)
            {
                _blockedByProtectionFailure = true;
                foreach (var tab in _tabs)
                    tab.WebView.Visibility = Visibility.Collapsed;

                ErrorText.Visibility = Visibility.Visible;
                ErrorText.Text =
                    "Sicherheitsfehler: Der Bildschirmaufnahmeschutz konnte nicht " +
                    $"aktiviert werden (Win32-Fehler {result.Win32Error}).\n\n" +
                    "Der Browser bleibt aus Sicherheitsgründen gesperrt.";
                BrowserInitializationCompleted?.Invoke(false);
                return;
            }

            if (_blockedByProtectionFailure)
            {
                _blockedByProtectionFailure = false;

                if (_browserInitializationStarted && !_browserInitializationFailed)
                    UpdateSelectedTabUi();
                else if (IsLoaded)
                    _ = StartBrowserIfAllowedAsync();
            }
        }

        private void UpdateStatusUi(CaptureProtection.Result result)
        {
            if (!result.Success)
            {
                ProtectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)); // rot
                ProtectionIndicator.ToolTip = $"Aufnahmeschutz nicht aktiv (Fehlercode {result.Win32Error})";
            }
            else if (!result.ProtectionEnabled)
            {
                ProtectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)); // rot
                ProtectionIndicator.ToolTip = "Aufnahmeschutz deaktiviert – in den Einstellungen aktivieren";
            }
            else if (result.Success)
            {
                ProtectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)); // grün
                ProtectionIndicator.ToolTip = result.UsedExcludeFromCapture
                    ? "Aufnahmeschutz aktiv"
                    : "Aufnahmeschutz aktiv – eingeschränkter Windows-Modus";
            }
        }

        public sealed class BrowserTabState : INotifyPropertyChanged
        {
            public WebView2 WebView { get; init; } = null!;
            private string _title = "Neuer Tab";
            public string Title
            {
                get => _title;
                set
                {
                    if (string.Equals(_title, value, StringComparison.Ordinal))
                        return;

                    _title = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
                }
            }

            public bool IsInitialized { get; set; }
            public bool InitializationFailed { get; set; }
            public string? ErrorMessage { get; set; }

            private bool _isReadyForClose;
            public bool IsReadyForClose
            {
                get => _isReadyForClose;
                set
                {
                    if (_isReadyForClose == value)
                        return;

                    _isReadyForClose = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadyForClose)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public sealed class Favorite
        {
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
        }
    }
}
