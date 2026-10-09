using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
        private const int MaxTabCount = 12;
        private static readonly TimeSpan SessionSaveDelay = TimeSpan.FromMilliseconds(750);

        private TrayIconManager? _tray;
        private CoreWebView2Environment? _webEnvironment;
        private string? _browserExecutableFolder;
        private readonly ObservableCollection<BrowserTabState> _tabs = new();
        private bool _browserInitializationStarted;
        private bool _browserInitializationFailed;
        private bool _blockedByProtectionFailure;
        private bool _protectionApiHealthy;
        private bool _tabCreationInProgress;
        private bool _addressBoxUpdating;
        private readonly string _favoritesFilePath;
        private readonly string _sessionFilePath;
        private readonly string _webView2DiagnosticsFilePath;
        private readonly string _webViewUserDataFolder;
        private readonly List<Favorite> _favorites = new();
        private readonly Stack<ClosedTabState> _closedTabs = new();
        private readonly DispatcherTimer _sessionSaveTimer;
        private readonly SessionStore _sessionStore;
        private bool _updatingFavorites;
        private bool _sessionSavePending;
        private bool _tabActivityUpdateQueued;
        private bool _tabActivityUpdateRunning;
        private int _tabActivityGeneration;
        private bool _audioPlaying;
        private bool _browserRecoveryInProgress;
        private int _browserRecoveryAttempts;
        private bool _useSoftwareRendering;
        private TaskCompletionSource<bool>? _browserExitSignal;
        private int _sessionRestoreGeneration;
        private Point _tabDragStartPoint;
        private BrowserTabState? _draggedTab;
        private bool _isClosing;

        public event Action<bool>? BrowserInitializationCompleted;

        public ObservableCollection<BrowserTabState> Tabs => _tabs;

        private BrowserTabState? CurrentTab => BrowserTabStrip.SelectedItem as BrowserTabState;

        private CoreWebView2? CurrentCoreWebView2
        {
            get
            {
                BrowserTabState? tab = CurrentTab;
                if (tab?.InitializationFailed == true)
                    return null;

                try
                {
                    return tab?.WebView.CoreWebView2;
                }
                catch (COMException)
                {
                    return null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        private static bool TryGetCore(BrowserTabState? tab, out CoreWebView2? core)
        {
            core = null;
            if (tab == null || tab.InitializationFailed || tab.IsDisposed)
                return false;

            try
            {
                core = tab.WebView.CoreWebView2;
                return core != null;
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void DisableBrowserActions()
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            ReloadButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            HomeButton.IsEnabled = false;
            MuteButton.IsEnabled = false;
        }

        public MainWindow()
        {
            string dataDirectory = App.IsSmokeTest
                ? Path.Combine(Path.GetTempPath(), "VeyraSmoke", Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureWindowApp");
            Directory.CreateDirectory(dataDirectory);
            _favoritesFilePath = Path.Combine(dataDirectory, "favorites.json");
            _sessionFilePath = Path.Combine(dataDirectory, "session.json");
            _sessionStore = new SessionStore(_sessionFilePath);
            _webView2DiagnosticsFilePath = Path.Combine(dataDirectory, "webview2-process-failures.log");
            _webViewUserDataFolder = Path.Combine(dataDirectory, "WebView2");

            InitializeComponent();

            _sessionSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = SessionSaveDelay
            };
            _sessionSaveTimer.Tick += SessionSaveTimer_Tick;

            LoadFavorites();
            RefreshFavorites();

            // Hält die Statusanzeige synchron mit der Einstellung im Fenster.
            CaptureProtection.EnabledChanged += OnProtectionEnabledChanged;

            // Tray-Symbol: "Fenster zeigen/verstecken" und "Beenden".
            if (!App.IsSmokeTest)
                _tray = new TrayIconManager(ToggleVisibility, Close);

            Loaded += async (_, _) =>
            {
                await InitializeShellAsync();
                if (_shellReady) await StartBrowserIfAllowedAsync();
            };
            IsVisibleChanged += (_, _) => RequestTabActivityUpdate();

            Closed += (_, _) =>
            {
                _isClosing = true;
                _sessionSaveTimer.Stop();
                SaveSessionNow();
                CaptureProtection.EnabledChanged -= OnProtectionEnabledChanged;
                _tray?.Dispose();

                foreach (var tab in new List<BrowserTabState>(_tabs))
                {
                    tab.MarkDisposed();
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

            // Keep standard minimize behavior on both platforms.
            PublishShellState();
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
            SetZoom(GetCurrentZoomFactor() - 0.1);
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(GetCurrentZoomFactor() + 0.1);
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(1.0);
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            CoreWebView2? core = CurrentCoreWebView2;
            if (tab == null || core == null)
                return;

            try
            {
                core.IsMuted = !core.IsMuted;
                UpdateMuteButton();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
        }

        private async void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (_webEnvironment == null || !_protectionApiHealthy || _tabCreationInProgress)
                return;

            if (_tabs.Count >= MaxTabCount)
            {
                SettingsMessage.Text = $"Es sind maximal {MaxTabCount} Tabs möglich.";
                return;
            }

            await CreateTabAsync(HomeUrl, selectTab: true);
        }

        private void SetZoom(double zoomFactor)
        {
            BrowserTabState? tab = CurrentTab;
            if (tab == null || tab.InitializationFailed)
                return;

            try
            {
                tab.WebView.ZoomFactor = Math.Clamp(zoomFactor, 0.5, 2.0);
                UpdateZoomText();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
        }

        private void UpdateZoomText()
        {
            double zoom = GetCurrentZoomFactor();
            ZoomText.Text = $"{Math.Round(zoom * 100):0} %";
        }

        private double GetCurrentZoomFactor()
        {
            double zoom = 1.0;
            BrowserTabState? tab = CurrentTab;
            if (tab != null && !tab.InitializationFailed)
            {
                try
                {
                    zoom = tab.WebView.ZoomFactor;
                }
                catch (COMException)
                {
                    zoom = 1.0;
                }
                catch (InvalidOperationException)
                {
                    zoom = 1.0;
                }
            }

            return zoom;
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
            BrowserTabState? tab = CurrentTab;
            CoreWebView2? core = CurrentCoreWebView2;
            if (tab == null || core == null)
            {
                SettingsMessage.Text = "Diese Seite kann nicht als Favorit gespeichert werden.";
                return;
            }

            try
            {
                string url = core.Source;
                if (!IsAllowedNavigation(url))
                {
                    SettingsMessage.Text = "Diese Seite kann nicht als Favorit gespeichert werden.";
                    return;
                }

                string title = core.DocumentTitle;
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
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
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

        private async void DuplicateTabButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            string? url = tab?.IsHomePage == true ? HomeUrl : null;
            try
            {
                if (tab?.IsHomePage != true)
                    url = CurrentCoreWebView2?.Source;
            }
            catch (COMException ex)
            {
                if (tab != null)
                    HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                if (tab != null)
                    HandleWebViewAccessFailure(tab, ex);
            }

            if (_webEnvironment == null || !_protectionApiHealthy || !IsAllowedNavigation(url))
            {
                SettingsMessage.Text = "Der aktuelle Tab ist noch nicht bereit.";
                return;
            }

            if (_tabs.Count >= MaxTabCount)
            {
                SettingsMessage.Text = $"Es sind maximal {MaxTabCount} Tabs möglich.";
                return;
            }

            await CreateTabAsync(url!, selectTab: true);
            SettingsMessage.Text = "Tab dupliziert.";
        }

        private async void RestoreClosedTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (_closedTabs.Count == 0)
            {
                SettingsMessage.Text = "Es gibt keinen geschlossenen Tab zum Wiederherstellen.";
                return;
            }

            if (_webEnvironment == null || !_protectionApiHealthy)
                return;

            if (_tabs.Count >= MaxTabCount)
            {
                SettingsMessage.Text = $"Es sind maximal {MaxTabCount} Tabs möglich.";
                return;
            }

            ClosedTabState closedTab = _closedTabs.Pop();
            await CreateTabAsync(closedTab.Url, selectTab: true);
            SettingsMessage.Text = "Geschlossener Tab wiederhergestellt.";
        }

        private void CloseOtherTabsButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? selected = CurrentTab;
            if (selected == null)
                return;

            foreach (var tab in new List<BrowserTabState>(_tabs))
            {
                if (!ReferenceEquals(tab, selected) && tab.IsReadyForClose)
                    CloseTab(tab, addToClosedTabs: false);
            }

            SettingsMessage.Text = "Die anderen Tabs wurden geschlossen.";
        }

        private void ClearSessionButton_Click(object sender, RoutedEventArgs e)
        {
            _sessionSaveTimer.Stop();
            _sessionSavePending = false;

            try
            {
                _sessionStore.Clear();
                SettingsMessage.Text = "Die gespeicherte Sitzung wurde gelöscht. Neue Sitzungen werden erst beim nächsten App-Start gespeichert.";
            }
            catch (Exception ex)
            {
                SettingsMessage.Text = "Die Sitzung konnte nicht gelöscht werden: " + ex.Message;
            }
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

        private List<string> LoadSession()
        {
            try
            {
                if (!File.Exists(_sessionFilePath))
                    return new List<string>();

                var urls = JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(_sessionFilePath));
                if (urls == null)
                    return new List<string>();

                var validUrls = new List<string>();
                foreach (string? url in urls)
                {
                    if (!string.IsNullOrWhiteSpace(url) && IsAllowedNavigation(url))
                        validUrls.Add(url);

                    if (validUrls.Count == 12)
                        break;
                }

                return validUrls;
            }
            catch
            {
                // Eine beschädigte Sitzung darf den Browserstart nicht verhindern.
                return new List<string>();
            }
        }

        private void RequestSessionSave()
        {
            if (_isClosing)
            {
                SaveSessionNow();
                return;
            }

            _sessionSavePending = true;
            _sessionSaveTimer.Stop();
            _sessionSaveTimer.Start();
        }

        private async void SessionSaveTimer_Tick(object? sender, EventArgs e)
        {
            _sessionSaveTimer.Stop();

            if (!_sessionSavePending || _isClosing)
                return;

            _sessionSavePending = false;
            try
            {
                await _sessionStore.SaveAsync(GetSessionUrls());
            }
            catch (Exception ex)
            {
                LogWebViewProcessFailure("Sitzungsspeicherung fehlgeschlagen: " + ex.Message);
            }

            if (_sessionSavePending && !_isClosing)
                _sessionSaveTimer.Start();
        }

        private List<string> GetSessionUrls(bool includeFailedTabs = false)
        {
            var urls = new List<string>(_tabs.Count);
            foreach (var tab in _tabs)
            {
                if (tab.IsDisposed || (tab.InitializationFailed && !includeFailedTabs))
                    continue;

                if (tab.IsHomePage)
                {
                    urls.Add(HomeUrl);
                    continue;
                }

                try
                {
                    string? url = tab.LastKnownUrl;
                    if (TryGetCore(tab, out CoreWebView2? core) && core != null)
                        url = core.Source;

                    if (IsAllowedNavigation(url))
                        urls.Add(url!);
                }
                catch (COMException)
                {
                    // Ein abgestürzter Tab wird nicht in die Sitzung geschrieben.
                }
                catch (InvalidOperationException)
                {
                    // Ein bereits freigegebener Controller wird ignoriert.
                }
            }

            return urls;
        }

        private void SaveSessionNow()
        {
            if (_isClosing && _tabs.Count == 0)
                return;

            _sessionSavePending = false;

            try
            {
                _sessionStore.Save(GetSessionUrls());
            }
            catch
            {
                // Sitzungswiederherstellung ist Komfort, kein Grund für einen Absturz.
            }
        }

        private void OnProtectionEnabledChanged(bool _)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            if (Dispatcher.CheckAccess())
            {
                RefreshStatusUi();
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(RefreshStatusUi));
            }
            catch (InvalidOperationException)
            {
                // Der Dispatcher kann waehrend des App-Endes bereits beendet sein.
            }
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
                _webEnvironment = await CreateWebEnvironmentAsync(allowProfileRecovery: true);
                _webEnvironment.BrowserProcessExited += OnBrowserProcessExited;

                List<string> sessionUrls = LoadSession();
                if (sessionUrls.Count == 0)
                {
                    await CreateTabAsync(HomeUrl, selectTab: true);
                }
                else
                {
                    await CreateTabAsync(sessionUrls[0], selectTab: true);

                    if (sessionUrls.Count > 1 && !_browserInitializationFailed && !App.IsSmokeTest)
                    {
                        int restoreGeneration = _sessionRestoreGeneration;
                        _ = RestoreSessionTabsAsync(
                            sessionUrls.GetRange(1, sessionUrls.Count - 1),
                            restoreGeneration);
                    }
                }
            }
            catch (Exception ex)
            {
                _browserInitializationFailed = true;
                ShowBrowserError(
                    "Der eingebettete Browser (WebView2) konnte nicht gestartet werden.\n\n" +
                    "Bitte das gesamte portable Veyra-ZIP in einen lokalen Ordner entpacken " +
                    "und Veyra.exe von dort starten. Der Ordner WebView2Runtime muss neben der EXE bleiben.\n\n" +
                    "Details: " + ex.Message);
                BrowserInitializationCompleted?.Invoke(false);
            }
        }

        private async Task<CoreWebView2Environment> CreateWebEnvironmentAsync(bool allowProfileRecovery)
        {
            CoreWebView2EnvironmentOptions? options = _useSoftwareRendering
                ? new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-gpu" }
                : null;

            try
            {
                return await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: _browserExecutableFolder,
                    userDataFolder: _webViewUserDataFolder,
                    options: options);
            }
            catch (Exception firstException) when (allowProfileRecovery)
            {
                LogWebViewProcessFailure(
                    "WebView2-Profil konnte nicht geöffnet werden: " + firstException.Message);

                if (!TryQuarantineWebViewProfile(out string? backupPath))
                    throw;

                try
                {
                    CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: _browserExecutableFolder,
                        userDataFolder: _webViewUserDataFolder,
                        options: options);

                    SettingsMessage.Text = string.IsNullOrEmpty(backupPath)
                        ? "Das WebView2-Profil wurde neu erstellt."
                        : "Das beschädigte WebView2-Profil wurde gesichert und neu erstellt.";
                    return environment;
                }
                catch (Exception recoveryException)
                {
                    LogWebViewProcessFailure(
                        "WebView2-Profil-Recovery fehlgeschlagen: " + recoveryException.Message);
                    throw;
                }
            }
        }

        private bool TryQuarantineWebViewProfile(out string? backupPath)
        {
            backupPath = null;
            if (!Directory.Exists(_webViewUserDataFolder))
                return true;

            string? parent = Path.GetDirectoryName(_webViewUserDataFolder);
            if (string.IsNullOrEmpty(parent))
                return false;

            string baseName = Path.GetFileName(_webViewUserDataFolder);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                string candidate = Path.Combine(
                    parent,
                    $"{baseName}.backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{attempt}");
                try
                {
                    Directory.Move(_webViewUserDataFolder, candidate);
                    backupPath = candidate;
                    return true;
                }
                catch (IOException)
                {
                    // Ein Namenskonflikt wird mit dem naechsten Suffix versucht.
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            return false;
        }

        private async void OnBrowserProcessExited(
            object? sender,
            CoreWebView2BrowserProcessExitedEventArgs _)
        {
            _browserExitSignal?.TrySetResult(true);

            if (_isClosing || _browserRecoveryInProgress || _browserRecoveryAttempts >= 1)
                return;

            try
            {
                await RecoverBrowserAsync(browserProcessAlreadyExited: true);
            }
            catch (Exception ex)
            {
                _browserRecoveryInProgress = false;
                _browserInitializationFailed = true;
                LogWebViewProcessFailure("WebView2-Recovery fehlgeschlagen: " + ex);
                ShowBrowserError(
                    "Der eingebettete Browserprozess konnte nicht wiederhergestellt werden.\n\n" +
                    "Bitte die App neu starten.\n\nDetails: " + ex.Message);
            }
        }

        private void RequestGpuRecovery()
        {
            if (_isClosing || _browserRecoveryInProgress || _browserRecoveryAttempts >= 1)
                return;

            _useSoftwareRendering = true;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            try
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(async () =>
                    {
                        try
                        {
                            await RecoverBrowserAsync(browserProcessAlreadyExited: false);
                        }
                        catch (Exception ex)
                        {
                            _browserRecoveryInProgress = false;
                            _browserInitializationFailed = true;
                            LogWebViewProcessFailure("GPU-Recovery fehlgeschlagen: " + ex);
                            ShowBrowserError(
                                "Der Browser konnte nach einem Grafikfehler nicht wiederhergestellt werden.\n\n" +
                                "Bitte die App neu starten.\n\nDetails: " + ex.Message);
                        }
                    }));
            }
            catch (InvalidOperationException)
            {
                // Der Dispatcher kann waehrend des App-Endes bereits beendet sein.
            }
        }

        private async Task RecoverBrowserAsync(bool browserProcessAlreadyExited)
        {
            if (_isClosing || _browserRecoveryInProgress || _browserRecoveryAttempts >= 1)
                return;

            _browserRecoveryInProgress = true;
            _browserRecoveryAttempts++;
            _sessionRestoreGeneration++;

            List<string> sessionUrls = GetSessionUrls(includeFailedTabs: true);
            if (sessionUrls.Count == 0)
                sessionUrls.Add(HomeUrl);

            _sessionSaveTimer.Stop();
            _sessionSavePending = false;

            if (!browserProcessAlreadyExited)
            {
                _browserExitSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Laufende Tab-Erstellung darf den alten Controller nicht mehr
            // verwenden, bevor die Recovery den Host leert.
            for (int attempt = 0; attempt < 40 && _tabCreationInProgress; attempt++)
                await Task.Delay(50);

            DisposeAllTabs();

            if (!browserProcessAlreadyExited && _browserExitSignal != null)
            {
                Task completedExitSignal = await Task.WhenAny(
                    _browserExitSignal.Task,
                    Task.Delay(TimeSpan.FromSeconds(3)));
                if (completedExitSignal == _browserExitSignal.Task)
                    _browserExitSignal = null;
            }

            if (_webEnvironment != null)
                _webEnvironment.BrowserProcessExited -= OnBrowserProcessExited;

            _browserExitSignal = null;
            _webEnvironment = null;
            _browserInitializationFailed = false;
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = "Der Browser wird wiederhergestellt …";

            _webEnvironment = await CreateWebEnvironmentAsync(allowProfileRecovery: true);
            _webEnvironment.BrowserProcessExited += OnBrowserProcessExited;

            await CreateTabAsync(sessionUrls[0], selectTab: true, allowDuringRecovery: true);
            if (!_browserInitializationFailed && sessionUrls.Count > 1 && !App.IsSmokeTest)
            {
                int restoreGeneration = _sessionRestoreGeneration;
                _ = RestoreSessionTabsAsync(
                    sessionUrls.GetRange(1, sessionUrls.Count - 1),
                    restoreGeneration);
            }

            _browserRecoveryInProgress = false;
        }

        private void DisposeAllTabs()
        {
            foreach (var tab in new List<BrowserTabState>(_tabs))
            {
                BrowserContentHost.Children.Remove(tab.WebView);
                tab.MarkDisposed();
                try
                {
                    tab.WebView.Dispose();
                }
                catch
                {
                    // Die Recovery darf nicht an einem bereits beendeten
                    // WebView2-Controller scheitern.
                }
            }

            _tabs.Clear();
            BrowserTabStrip.SelectedItem = null;
        }

        private async Task CreateTabAsync(
            string initialUrl,
            bool selectTab,
            bool allowDuringRecovery = false)
        {
            if (_webEnvironment == null ||
                _tabCreationInProgress ||
                (_browserRecoveryInProgress && !allowDuringRecovery))
                return;

            if (_tabs.Count >= MaxTabCount)
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
                    WebView = webView,
                    LastKnownUrl = initialUrl,
                    IsHomePage = IsHomePageUrl(initialUrl)
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
                webView.PreviewKeyDown += (_, e) => HandleBrowserAccelerator(e);
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
                core.Settings.IsStatusBarEnabled = false;

                core.NavigationStarting += (_, args) => OnNavigationStarting(state, args);
                core.DownloadStarting += (_, args) =>
                {
                    // Der Standard-Downloaddialog ist ein separates,
                    // ungeschuetztes Fenster. Downloads werden daher im
                    // Sicherheitsprofil vollstaendig abgebrochen.
                    args.Cancel = true;
                    args.Handled = true;
                    if (ReferenceEquals(CurrentTab, state))
                        SettingsMessage.Text = "Downloads sind im Sicherheitsprofil deaktiviert.";
                };

                core.PermissionRequested += (_, args) =>
                {
                    // Kamera, Mikrofon, Standort, Benachrichtigungen und andere
                    // privilegierte Web-APIs bekommen keine Berechtigung.
                    args.State = CoreWebView2PermissionState.Deny;
                    args.SavesInProfile = false;
                    args.Handled = true;
                    if (ReferenceEquals(CurrentTab, state))
                        SettingsMessage.Text = "Berechtigungsanfrage der Website wurde blockiert.";
                };

                core.ScreenCaptureStarting += (_, args) =>
                {
                    // Auch die Web-API getDisplayMedia darf keine eigene
                    // Bildschirmaufnahme aus dem Browser heraus starten.
                    args.Cancel = true;
                    args.Handled = true;
                };

                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.print = function () { return false; };");

                // target="_blank" wird in einen geschützten neuen Tab übernommen;
                // unbekannte oder gefährliche Protokolle bleiben blockiert.
                core.NewWindowRequested += (_, args) =>
                {
                    if (IsAllowedNavigation(args.Uri) && !_tabCreationInProgress)
                        _ = CreateTabAsync(args.Uri, selectTab: true);
                    else if (ReferenceEquals(CurrentTab, state))
                        SettingsMessage.Text = "Dieses Popup wurde aus Sicherheitsgründen blockiert.";

                    args.Handled = true;
                };
                core.ProcessFailed += (_, args) => OnWebViewProcessFailed(state!, args);

                core.SourceChanged += (_, _) =>
                {
                    if (!ReferenceEquals(CurrentTab, state) || state.InitializationFailed || state.IsDisposed || AddressBox.IsKeyboardFocusWithin)
                        return;

                    try
                    {
                        string source = core.Source;
                        if (state.IsHomePage && source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                        {
                            state.LastKnownUrl = HomeUrl;
                            SetAddressBoxText(string.Empty);
                            return;
                        }

                        state.LastKnownUrl = source;
                        SetAddressBoxText(source);
                    }
                    catch (COMException ex)
                    {
                        HandleWebViewAccessFailure(state, ex);
                    }
                    catch (InvalidOperationException ex)
                    {
                        HandleWebViewAccessFailure(state, ex);
                    }
                };
                core.HistoryChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                        UpdateNavButtons();
                };
                core.DocumentTitleChanged += (_, _) =>
                {
                    if (state.InitializationFailed || state.IsDisposed)
                        return;

                    try
                    {
                        string title = core.DocumentTitle;
                        state.Title = GetTabTitle(title);
                        if (ReferenceEquals(CurrentTab, state))
                            UpdateWindowTitle(title);
                    }
                    catch (COMException ex)
                    {
                        HandleWebViewAccessFailure(state, ex);
                    }
                    catch (InvalidOperationException ex)
                    {
                        HandleWebViewAccessFailure(state, ex);
                    }
                };
                core.IsDocumentPlayingAudioChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state) && !state.InitializationFailed && !state.IsDisposed)
                    {
                        try
                        {
                            _audioPlaying = core.IsDocumentPlayingAudio;
                            UpdateMuteButton();
                        }
                        catch (COMException ex)
                        {
                            HandleWebViewAccessFailure(state, ex);
                        }
                        catch (InvalidOperationException ex)
                        {
                            HandleWebViewAccessFailure(state, ex);
                        }
                    }
                };
                core.IsMutedChanged += (_, _) =>
                {
                    if (ReferenceEquals(CurrentTab, state))
                        UpdateMuteButton();
                };
                core.NavigationCompleted += (_, _) =>
                {
                    state.IsLoading = false;
                    if (ReferenceEquals(CurrentTab, state) && !state.InitializationFailed && !state.IsDisposed)
                    {
                        StopButton.IsEnabled = false;
                        UpdateNavButtons();
                    }

                    if (!state.InitializationFailed && !state.IsDisposed)
                    {
                        try
                        {
                            string source = core.Source;
                            if (IsAllowedNavigation(source))
                                state.LastKnownUrl = source;
                        }
                        catch (COMException ex)
                        {
                            HandleWebViewAccessFailure(state, ex);
                        }
                        catch (InvalidOperationException ex)
                        {
                            HandleWebViewAccessFailure(state, ex);
                        }
                    }

                    RequestSessionSave();
                };

                state.IsInitialized = true;
                state.IsReadyForClose = true;
                state.ErrorMessage = null;
                state.Title = "Neuer Tab";
                if (!state.IsHomePage)
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

        private async Task RestoreSessionTabsAsync(List<string> sessionUrls, int restoreGeneration)
        {
            try
            {
                foreach (string url in sessionUrls)
                {
                    if (_isClosing || restoreGeneration != _sessionRestoreGeneration)
                        return;

                    // Sitzungstabs erst dann anlegen, wenn WPF keine Eingaben
                    // oder sichtbaren UI-Arbeiten mehr abarbeitet. So konkurriert
                    // der Start weiterer Chromium-Ansichten nicht mit der ersten
                    // Interaktion nach dem App-Start.
                    await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

                    while (_tabCreationInProgress &&
                           !_isClosing &&
                           restoreGeneration == _sessionRestoreGeneration)
                        await Task.Delay(50);

                    if (_isClosing ||
                        restoreGeneration != _sessionRestoreGeneration ||
                        _tabs.Count >= MaxTabCount)
                        return;

                    await CreateTabAsync(url, selectTab: false);
                }
            }
            catch (Exception ex)
            {
                LogWebViewProcessFailure("Sitzungswiederherstellung fehlgeschlagen: " + ex.Message);
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

        private void CloseTab(BrowserTabState state, bool addToClosedTabs = true)
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
            string? lastKnownUrl = state.IsHomePage ? HomeUrl : null;
            try
            {
                if (!state.IsHomePage)
                {
                    lastKnownUrl = state.LastKnownUrl;
                    if (TryGetCore(state, out CoreWebView2? core) && core != null)
                        lastKnownUrl = core.Source;
                }
            }
            catch (COMException)
            {
                // Ein abgestürzter Tab kann trotzdem sicher geschlossen werden.
            }
            catch (InvalidOperationException)
            {
                // Ein bereits freigegebener Controller hat keine Wiederherstellungs-URL.
            }
            string closedTitle = state.Title;

            if (addToClosedTabs && IsAllowedNavigation(lastKnownUrl))
            {
                _closedTabs.Push(new ClosedTabState
                {
                    Title = closedTitle,
                    Url = lastKnownUrl!
                });
            }

            BrowserContentHost.Children.Remove(state.WebView);
            state.MarkDisposed();
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

            RequestSessionSave();
            UpdateSelectedTabUi();
        }

        private void BrowserTabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != BrowserTabStrip)
                return;

            UpdateSelectedTabUi();
        }

        private void BrowserTabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tabDragStartPoint = e.GetPosition(BrowserTabStrip);
            _draggedTab = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as BrowserTabState;
        }

        private void BrowserTabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedTab == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point current = e.GetPosition(BrowserTabStrip);
            if (Math.Abs(current.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            BrowserTabState dragged = _draggedTab;
            _draggedTab = null;
            DragDrop.DoDragDrop(BrowserTabStrip, dragged, DragDropEffects.Move);
        }

        private void BrowserTabStrip_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(BrowserTabState)))
                return;

            var dragged = e.Data.GetData(typeof(BrowserTabState)) as BrowserTabState;
            var target = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as BrowserTabState;
            if (dragged == null || target == null || ReferenceEquals(dragged, target))
                return;

            int oldIndex = _tabs.IndexOf(dragged);
            int targetIndex = _tabs.IndexOf(target);
            if (oldIndex >= 0 && targetIndex >= 0)
                _tabs.Move(oldIndex, targetIndex);

            RequestSessionSave();
        }

        private static T? FindVisualParent<T>(DependencyObject? child)
            where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                    return parent;

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private void UpdateSelectedTabUi()
        {
            var state = CurrentTab;
            if (state == null)
            {
                NewTabPage.Visibility = Visibility.Collapsed;
                NewTabButton.IsEnabled = !_tabCreationInProgress &&
                    _webEnvironment != null && _protectionApiHealthy;
                foreach (var tab in _tabs)
                    tab.WebView.Visibility = Visibility.Collapsed;
                DisableBrowserActions();
                UpdateZoomText();
                RequestTabActivityUpdate();
                return;
            }

            NewTabButton.IsEnabled = !_tabCreationInProgress &&
                _webEnvironment != null && _protectionApiHealthy;
            bool showTabError = state.InitializationFailed;
            foreach (var tab in _tabs)
            {
                bool isVisible = ReferenceEquals(tab, state) &&
                    !tab.InitializationFailed && !tab.IsHomePage && !_blockedByProtectionFailure;
                tab.WebView.Visibility = isVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            NewTabPage.Visibility = state.IsHomePage && !state.InitializationFailed && !_blockedByProtectionFailure
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_blockedByProtectionFailure)
            {
                ErrorText.Visibility = Visibility.Visible;
                DisableBrowserActions();
                RequestTabActivityUpdate();
                return;
            }

            ErrorText.Visibility = showTabError ? Visibility.Visible : Visibility.Collapsed;
            if (showTabError)
            {
                ErrorText.Text = state.ErrorMessage ?? "Dieser Tab konnte nicht gestartet werden.";
                DisableBrowserActions();
                RequestTabActivityUpdate();
                return;
            }

            if (!state.IsInitialized || !TryGetCore(state, out CoreWebView2? core) || core == null)
            {
                DisableBrowserActions();
                RequestTabActivityUpdate();
                return;
            }

            if (state.IsHomePage)
            {
                SetAddressBoxText(string.Empty);
                UpdateWindowTitle("Neuer Tab");
                _audioPlaying = false;
                ReloadButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
                HomeButton.IsEnabled = true;
                UpdateMuteButton();
                MuteButton.IsEnabled = false;
                UpdateZoomText();
                RequestTabActivityUpdate();
                return;
            }

            try
            {
                SetAddressBoxText(core.Source);
                UpdateWindowTitle(core.DocumentTitle);
                _audioPlaying = core.IsDocumentPlayingAudio;
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(state, ex);
                return;
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(state, ex);
                return;
            }

            ReloadButton.IsEnabled = true;
            HomeButton.IsEnabled = true;
            UpdateMuteButton();
            UpdateZoomText();
            UpdateNavButtons();
            RequestTabActivityUpdate();
        }

        private void RequestTabActivityUpdate()
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            _tabActivityGeneration++;
            if (_tabActivityUpdateQueued || _tabActivityUpdateRunning)
                return;

            _tabActivityUpdateQueued = true;
            try
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(async () =>
                    {
                        _tabActivityUpdateQueued = false;
                        if (_tabActivityUpdateRunning || _isClosing)
                            return;

                        _tabActivityUpdateRunning = true;
                        int generation = _tabActivityGeneration;
                        try
                        {
                            await ApplyTabActivityAsync(generation);
                        }
                        catch (Exception ex)
                        {
                            LogWebViewProcessFailure("Tab-Aktivitaetsverwaltung fehlgeschlagen: " + ex.Message);
                        }
                        finally
                        {
                            _tabActivityUpdateRunning = false;
                            if (generation != _tabActivityGeneration && !_isClosing)
                                RequestTabActivityUpdate();
                        }
                    }));
            }
            catch (InvalidOperationException)
            {
                _tabActivityUpdateQueued = false;
            }
        }

        private async Task ApplyTabActivityAsync(int generation)
        {
            BrowserTabState? activeTab = CurrentTab;
            bool windowIsVisible = IsVisible && WindowState != WindowState.Minimized;

            foreach (var tab in new List<BrowserTabState>(_tabs))
            {
                if (generation != _tabActivityGeneration || _isClosing)
                    return;

                try
                {
                    if (tab.InitializationFailed || tab.IsDisposed)
                        continue;

                    CoreWebView2? core = tab.WebView.CoreWebView2;
                    if (core == null)
                        continue;

                    bool shouldBeActive = windowIsVisible &&
                        ReferenceEquals(tab, activeTab) &&
                        tab.WebView.IsVisible;

                    if (shouldBeActive)
                    {
                        if (core.IsSuspended)
                            core.Resume();
                    }
                    else if (!core.IsSuspended && !core.IsDocumentPlayingAudio)
                    {
                        // WebView2 pausiert damit Timer/Animationen und kann
                        // Renderer-Speicher an das Betriebssystem zurueckgeben.
                        // Aktive Hintergrundwiedergabe bleibt bewusst unberuehrt.
                        await core.TrySuspendAsync();
                    }
                }
                catch (COMException)
                {
                    // Ein Tab kann waehrend des Suspendierens geschlossen oder
                    // vom WebView2-Prozess beendet worden sein.
                }
                catch (InvalidOperationException)
                {
                    // Der Controller kann waehrend eines Tabwechsels noch in
                    // einem ungueltigen Sichtbarkeitszustand sein oder bereits
                    // waehrend des asynchronen Suspendierens geschlossen werden.
                }
            }
        }

        private void OnNavigationStarting(BrowserTabState state, CoreWebView2NavigationStartingEventArgs args)
        {
            state.IsLoading = true;
            bool internalHomeDocument = state.IsHomePage &&
                args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
            if (ReferenceEquals(CurrentTab, state))
                StopButton.IsEnabled = !internalHomeDocument;

            if (!internalHomeDocument && !IsAllowedNavigation(args.Uri))
                args.Cancel = true;
            else if (!internalHomeDocument)
                state.LastKnownUrl = args.Uri;
        }

        private static bool IsHomePageUrl(string? address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return false;

            return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                uri.Host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase) &&
                (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/") &&
                string.IsNullOrEmpty(uri.Query);
        }

        private static bool IsAllowedNavigation(string? address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return false;

            return !string.IsNullOrWhiteSpace(uri.Host) &&
                   (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateNavButtons()
        {
            var tab = CurrentTab;
            if (!TryGetCore(tab, out _) || tab == null)
            {
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
                return;
            }

            try
            {
                BackButton.IsEnabled = tab.WebView.CanGoBack;
                ForwardButton.IsEnabled = tab.WebView.CanGoForward;
            }
            catch (COMException)
            {
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
            }
            catch (InvalidOperationException)
            {
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            if (!TryGetCore(tab, out _) || tab == null)
                return;

            try
            {
                if (tab.WebView.CanGoBack)
                    tab.WebView.GoBack();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            if (!TryGetCore(tab, out _) || tab == null)
                return;

            try
            {
                if (tab.WebView.CanGoForward)
                    tab.WebView.GoForward();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            CoreWebView2? core = CurrentCoreWebView2;
            if (tab == null || core == null)
                return;

            try
            {
                core.Reload();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            if (tab == null || tab.InitializationFailed)
                return;

            try
            {
                tab.WebView.Stop();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }

            StopButton.IsEnabled = false;
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserTabState? tab = CurrentTab;
            CoreWebView2? core = CurrentCoreWebView2;
            if (tab == null || core == null)
                return;

            try
            {
                tab.IsHomePage = true;
                tab.LastKnownUrl = HomeUrl;
                StartPageSearchBox.Clear();
                UpdateSelectedTabUi();
                core.Navigate("about:blank");
                RequestSessionSave();
            }
            catch (COMException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
            catch (InvalidOperationException ex)
            {
                HandleWebViewAccessFailure(tab, ex);
            }
        }

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            if (string.IsNullOrWhiteSpace(AddressBox.Text))
            {
                HomeButton_Click(sender, e);
                return;
            }
            NavigateTo(ResolveInput(AddressBox.Text));
        }

        private void StartPageSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            SearchFromStartPage();
        }

        private void StartPageSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchFromStartPage();
        }

        private void SearchFromStartPage()
        {
            if (string.IsNullOrWhiteSpace(StartPageSearchBox.Text))
                return;

            NavigateTo(SearchUrl + Uri.EscapeDataString(StartPageSearchBox.Text.Trim()));
            StartPageSearchBox.Clear();
        }

        private void StartPageShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string address)
                NavigateTo(address);
        }

        private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Das native WebView2-Fenster gibt beim Wechsel in die WPF-TextBox
            // den Fokus ab. Die Auswahl erst nach dem Fokuswechsel anzuwenden
            // vermeidet verschachtelte WPF-/WebView2-Fokusverarbeitung.
            try
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() =>
                    {
                        if (!_isClosing && AddressBox.IsKeyboardFocusWithin)
                            AddressBox.SelectAll();
                    }));
            }
            catch (InvalidOperationException)
            {
                // Der Dispatcher kann waehrend des App-Endes bereits beendet sein.
            }
        }

        private void NavigateTo(string address)
        {
            try
            {
                BrowserTabState? tab = CurrentTab;
                CoreWebView2? core = CurrentCoreWebView2;
                if (tab == null || core == null)
                    return;

                if (string.IsNullOrWhiteSpace(address))
                    return;

                tab.IsHomePage = false;
                StartPageSearchBox.Clear();
                UpdateSelectedTabUi();
                core.Navigate(address);
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
            if (WebViewFailurePolicy.RequiresTabRecreation(args.ProcessFailedKind))
                MarkTabAsFailed(state, details);
        }

        private void HandleWebViewAccessFailure(BrowserTabState state, Exception exception)
        {
            if (_isClosing || state.InitializationFailed || state.IsDisposed)
                return;

            string details =
                $"WebView2-Zugriff fehlgeschlagen: {exception.GetType().Name}; " +
                $"Nachricht: {exception.Message}";
            LogWebViewProcessFailure(details);
            MarkTabAsFailed(state, details);
        }

        private void MarkTabAsFailed(BrowserTabState state, string details)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            void ShowFailure()
            {
                if (_isClosing || state.InitializationFailed || state.IsDisposed)
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
                    DisableBrowserActions();
                }

                UpdateSelectedTabUi();
            }

            if (Dispatcher.CheckAccess())
                ShowFailure();
            else
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(ShowFailure));
                }
                catch (InvalidOperationException)
                {
                    // Der Dispatcher kann waehrend des App-Endes bereits beendet sein.
                }
            }
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
                ? "Veyra"
                : $"{documentTitle} – Veyra";
        }

        private void UpdateMuteButton()
        {
            BrowserTabState? tab = CurrentTab;
            if (!TryGetCore(tab, out CoreWebView2? core) || core == null)
            {
                MuteButton.IsEnabled = false;
                MuteButton.Content = "🔊";
                MuteButton.ToolTip = "Ton stummschalten";
                return;
            }

            CoreWebView2 activeCore = core;
            MuteButton.IsEnabled = true;
            try
            {
                bool muted = activeCore.IsMuted;
                MuteButton.Content = muted ? "🔇" : "🔊";
                MuteButton.ToolTip = muted
                    ? "Ton einschalten"
                    : (_audioPlaying ? "Ton stummschalten (Audio aktiv)" : "Ton stummschalten");
            }
            catch (COMException)
            {
                MuteButton.IsEnabled = false;
            }
            catch (InvalidOperationException)
            {
                MuteButton.IsEnabled = false;
            }
        }

        private void ShowBrowserError(string message)
        {
            NewTabPage.Visibility = Visibility.Collapsed;
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
            public string Id { get; } = Guid.NewGuid().ToString();
            public bool IsPinned { get; set; }
            public bool IsLoading { get; set; }
            public WebView2 WebView { get; init; } = null!;
            public string? LastKnownUrl { get; set; }
            public bool IsHomePage { get; set; }
            public bool IsDisposed { get; private set; }

            public void MarkDisposed() => IsDisposed = true;

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

        private sealed class ClosedTabState
        {
            public string Title { get; init; } = "Neuer Tab";
            public string Url { get; init; } = HomeUrl;
        }
    }
}
