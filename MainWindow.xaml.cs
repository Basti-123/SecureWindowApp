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
        private readonly string _sessionFilePath;
        private readonly string _webView2DiagnosticsFilePath;
        private readonly List<Favorite> _favorites = new();
        private readonly Stack<ClosedTabState> _closedTabs = new();
        private bool _updatingFavorites;
        private bool _audioPlaying;
        private Point _tabDragStartPoint;
        private BrowserTabState? _draggedTab;
        private bool _isClosing;

        public event Action<bool>? BrowserInitializationCompleted;

        public ObservableCollection<BrowserTabState> Tabs => _tabs;

        private BrowserTabState? CurrentTab => BrowserTabStrip.SelectedItem as BrowserTabState;

        private CoreWebView2? CurrentCoreWebView2 => CurrentTab?.WebView.CoreWebView2;

        public MainWindow()
        {
            _favoritesFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SecureWindowApp", "favorites.json");
            _sessionFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SecureWindowApp", "session.json");
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
                _isClosing = true;
                SaveSession();
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

        private async void DuplicateTabButton_Click(object sender, RoutedEventArgs e)
        {
            string? url = CurrentCoreWebView2?.Source;
            if (_webEnvironment == null || !_protectionApiHealthy || !IsAllowedNavigation(url))
            {
                SettingsMessage.Text = "Der aktuelle Tab ist noch nicht bereit.";
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
            try
            {
                if (File.Exists(_sessionFilePath))
                    File.Delete(_sessionFilePath);

                SettingsMessage.Text = "Die gespeicherte Sitzung wurde gelöscht.";
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

        private void SaveSession()
        {
            if (_isClosing && _tabs.Count == 0)
                return;

            try
            {
                var urls = new List<string>();
                foreach (var tab in _tabs)
                {
                    string? url = tab.WebView.CoreWebView2?.Source;
                    if (IsAllowedNavigation(url))
                        urls.Add(url!);
                }

                string? directory = Path.GetDirectoryName(_sessionFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(_sessionFilePath,
                    JsonSerializer.Serialize(urls, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Sitzungswiederherstellung ist Komfort, kein Grund für einen Absturz.
            }
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

                List<string> sessionUrls = LoadSession();
                if (sessionUrls.Count == 0)
                {
                    await CreateTabAsync(HomeUrl, selectTab: true);
                }
                else
                {
                    for (int index = 0; index < sessionUrls.Count; index++)
                        await CreateTabAsync(sessionUrls[index], selectTab: index == 0);
                }
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

                    SaveSession();
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
            string? lastKnownUrl = state.WebView.CoreWebView2?.Source;
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

            SaveSession();
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

            SaveSession();
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

            return !string.IsNullOrWhiteSpace(uri.Host) &&
                   (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
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

        private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
        {
            AddressBox.SelectAll();
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

        private sealed class ClosedTabState
        {
            public string Title { get; init; } = "Neuer Tab";
            public string Url { get; init; } = HomeUrl;
        }
    }
}
