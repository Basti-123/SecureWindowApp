using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SecureWindowApp
{
    public partial class MainWindow
    {
        private bool _shellReady;
        private bool _runningShellSmoke;
        private readonly System.Collections.Generic.List<(string Action, string Value)> _shellSmokeCommands = new();
        private bool _shellOverlay;
        private int _overlayGeneration;
        private string _lastShellState = "";
        private DispatcherTimer? _shellStateTimer;
        private readonly TaskCompletionSource<bool> _shellLoaded = new();
        private const string ShellUrl = "https://veyra-ui.invalid/index.html";

        private async Task InitializeShellAsync()
        {
            try
            {
                // Bundle assets inside the single EXE, then materialize them for WebView2's
                // local virtual host. No privileged bridge is attached to browsing tabs.
                string folder = Path.Combine(Path.GetDirectoryName(_favoritesFilePath)!, "Shell");
                var assembly = typeof(MainWindow).Assembly;
                foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("VeyraShell/", StringComparison.Ordinal)))
                {
                    string relative = resource["VeyraShell/".Length..];
                    string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var file = File.Create(path);
                    stream.CopyTo(file);
                }
                _browserExecutableFolder = PortableWebViewRuntime.Prepare(AppContext.BaseDirectory);
                var environment = await CoreWebView2Environment.CreateAsync(_browserExecutableFolder,
                    Path.Combine(Path.GetDirectoryName(_favoritesFilePath)!, "ShellProfile"));
                await ShellView.EnsureCoreWebView2Async(environment);
                var core = ShellView.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping("veyra-ui.invalid", folder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.NavigationStarting += (_, e) => { if (!string.Equals(e.Uri, ShellUrl, StringComparison.Ordinal)) e.Cancel = true; };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.WebMessageReceived += OnShellMessage;
                // Accelerator events belong to the controller, so browsing-view hotkeys are
                // forwarded separately below; the shell handles its own DOM keyboard events.
                core.Navigate(ShellUrl);
                await _shellLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
                _shellStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _shellStateTimer.Tick += (_, _) => PublishShellState();
                _shellStateTimer.Start();
                Closed += (_, _) => { _shellStateTimer.Stop(); ShellView.Dispose(); };
                PublishShellState();
            }
            catch (Exception ex)
            {
                ShellStartupError.Text = "The browser interface could not start. " + ex.Message;
                ShellStartupError.Visibility = Visibility.Visible;
                BrowserInitializationCompleted?.Invoke(false);
            }
        }

        private void PublishShellState()
        {
            if (!_shellReady || _isClosing) return;
            try
            {
                var current = CurrentTab;
                string error = ErrorText.Visibility == Visibility.Visible ? ErrorText.Text : "";
                string json = JsonSerializer.Serialize(new
                {
                    type = "state",
                    state = new
                    {
                        tabs = _tabs.Select(t => new { id = t.Id, title = t.Title, home = t.IsHomePage, pinned = t.IsPinned, loading = t.IsLoading }).ToArray(),
                        selected = current?.Id ?? "", home = current?.IsHomePage ?? true,
                        address = current?.IsHomePage == true ? "" : current?.LastKnownUrl ?? "",
                        canBack = BackButton.IsEnabled && current?.IsHomePage != true,
                        canForward = ForwardButton.IsEnabled && current?.IsHomePage != true,
                        loading = current?.IsLoading ?? false,
                        ready = current?.IsInitialized == true && !current.InitializationFailed && _protectionApiHealthy,
                        favorites = _favorites.Select(f => new { title = f.Title, url = f.Url }).ToArray(),
                        capture = !_protectionApiHealthy ? "unavailable" : CaptureProtection.IsEnabled ? "active" : "disabled",
                        canMute = true, muted = CurrentCoreWebView2?.IsMuted ?? false,
                        message = SettingsMessage.Text, error
                    }
                });
                if (json == _lastShellState) return;
                ShellView.CoreWebView2.PostWebMessageAsJson(json);
                _lastShellState = json;
            }
            catch (COMException) { }
            catch (InvalidOperationException) { }
        }

        private async void OnShellMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!string.Equals(e.Source, ShellUrl, StringComparison.Ordinal)) return;
            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                string action = root.GetProperty("action").GetString() ?? "";
                string value = root.GetProperty("value").GetString() ?? "";
                if (value.Length > 8192) return;
                if (_runningShellSmoke)
                {
                    _shellSmokeCommands.Add((action, value));
                    return;
                }
                var tab = _tabs.FirstOrDefault(t => t.Id == value);
                var args = new RoutedEventArgs();
                switch (action)
                {
                    case "ready": _shellReady = true; _shellLoaded.TrySetResult(true); break;
                    case "overlay":
                        bool opening = value == "true";
                        if (opening == _shellOverlay) break;
                        _shellOverlay = opening;
                        int generation = ++_overlayGeneration;
                        if (opening && _protectionApiHealthy && CurrentTab?.IsHomePage == false && CurrentCoreWebView2 is CoreWebView2 previewCore)
                        {
                            try
                            {
                                using var stream = new MemoryStream();
                                await previewCore.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                                if (_shellOverlay && _protectionApiHealthy && generation == _overlayGeneration && !_isClosing)
                                    ShellView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                                    { type = "pagePreview", image = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()) }));
                            }
                            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException) { }
                        }
                        if (generation == _overlayGeneration)
                            BrowserContentHost.Visibility = _shellOverlay ? Visibility.Collapsed : Visibility.Visible;
                        RequestTabActivityUpdate(); break;
                    case "drag":
                        ReleaseCapture();
                        SendMessage(new WindowInteropHelper(this).Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); break;
                    case "minimize": WindowState = WindowState.Minimized; break;
                    case "maximize": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                    case "closeWindow": Close(); break;
                    case "newTab": NewTabButton_Click(this, args); break;
                    case "selectTab": if (tab != null) BrowserTabStrip.SelectedItem = tab; break;
                    case "closeTab":
                        if (tab != null && _tabs.Count == 1) HomeButton_Click(this, args);
                        else if (tab != null) CloseTab(tab); break;
                    case "pinTab": if (tab != null) { tab.IsPinned = !tab.IsPinned; OrderPinnedTabs(); } break;
                    case "moveTab":
                        using (var move = JsonDocument.Parse(value))
                        {
                            var source = _tabs.FirstOrDefault(t => t.Id == move.RootElement.GetProperty("id").GetString());
                            var target = _tabs.FirstOrDefault(t => t.Id == move.RootElement.GetProperty("before").GetString());
                            if (source != null && target != null && source != target && source.IsPinned == target.IsPinned)
                            {
                                int sourceIndex = _tabs.IndexOf(source), targetIndex = _tabs.IndexOf(target);
                                _tabs.Move(sourceIndex, targetIndex - (sourceIndex < targetIndex ? 1 : 0));
                                RequestSessionSave();
                            }
                        }
                        break;
                    case "navigate":
                        string address = ResolveInput(value);
                        if (_protectionApiHealthy && IsAllowedNavigation(address)) NavigateTo(address); break;
                    case "back": BackButton_Click(this, args); break;
                    case "forward": ForwardButton_Click(this, args); break;
                    case "reload": ReloadButton_Click(this, args); break;
                    case "stop": StopButton_Click(this, args); break;
                    case "home": HomeButton_Click(this, args); break;
                    case "bookmark": AddFavoriteButton_Click(this, args); break;
                    case "mute": MuteButton_Click(this, args); break;
                    case "toggleCapture": CaptureProtection.SetEnabled(!CaptureProtection.IsEnabled); break;
                    case "removeFavorite": _favorites.RemoveAll(f => f.Url == value); SaveFavorites(); RefreshFavorites(); break;
                    case "duplicate": DuplicateTabButton_Click(this, args); break;
                    case "restore": RestoreClosedTabButton_Click(this, args); break;
                    case "closeOthers": CloseOtherTabsButton_Click(this, args); break;
                    case "clearSession": ClearSessionButton_Click(this, args); break;
                    case "zoomIn": ZoomInButton_Click(this, args); break;
                    case "zoomOut": ZoomOutButton_Click(this, args); break;
                    case "zoomReset": ZoomResetButton_Click(this, args); break;
                    case "clearData":
                        if (CurrentCoreWebView2 is CoreWebView2 core)
                        {
                            await core.Profile.ClearBrowsingDataAsync();
                            SettingsMessage.Text = "Cookies, cache and website data cleared.";
                        }
                        break;
                }
                PublishShellState();
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is COMException || ex is ArgumentException)
            { SettingsMessage.Text = "The action could not complete: " + ex.Message; }
        }

        public async Task<bool> VerifyShellAsync()
        {
            try
            {
                _shellStateTimer?.Stop();
                _runningShellSmoke = true;
                using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("VeyraShellTests")!;
                using var reader = new StreamReader(resource);
                string script = await reader.ReadToEndAsync();
                for (int i = 0; i < 100; i++)
                {
                    if (await ShellView.ExecuteScriptAsync("document.fonts.status") == "\"loaded\"") break;
                    await Task.Delay(50);
                }
                string result = await ShellView.ExecuteScriptAsync(script);
                using var parsed = JsonDocument.Parse(result);
                await Task.Delay(100); // Allow posted bridge messages to reach the native dispatcher.
                return parsed.RootElement.ValueKind == JsonValueKind.Array && parsed.RootElement.GetArrayLength() == 23 &&
                    _shellSmokeCommands.Contains(("navigate", "https://www.google.com/search?q=hello%20%26%20world")) &&
                    _shellSmokeCommands.Contains(("pinTab", "two")) && _shellSmokeCommands.Contains(("closeTab", "two"));
            }
            catch (Exception ex)
            {
                File.AppendAllText(_webView2DiagnosticsFilePath, "Shell smoke test failed: " + ex + Environment.NewLine);
                return false;
            }
        }

        private void OrderPinnedTabs()
        {
            var selected = CurrentTab;
            var order = _tabs.OrderByDescending(t => t.IsPinned).ToArray();
            for (int i = 0; i < order.Length; i++) _tabs.Move(_tabs.IndexOf(order[i]), i);
            BrowserTabStrip.SelectedItem = selected;
            RequestSessionSave();
        }

        private void HandleBrowserAccelerator(KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            int key = KeyInterop.VirtualKeyFromKey(e.Key);
            if (key != 0x4C && key != 0x54 && key != 0x57 && key != 0x44 && key != 0x52) return;
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (key == 0x4C)
                {
                    ShellView.Focus();
                    ShellView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"focusAddress\"}");
                }
                if (key == 0x54 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) RestoreClosedTabButton_Click(this, new RoutedEventArgs());
                else if (key == 0x54) NewTabButton_Click(this, new RoutedEventArgs());
                if (key == 0x57 && CurrentTab != null)
                {
                    if (_tabs.Count == 1) HomeButton_Click(this, new RoutedEventArgs());
                    else CloseTab(CurrentTab);
                }
                if (key == 0x52) ReloadButton_Click(this, new RoutedEventArgs());
                if (key == 0x44) AddFavoriteButton_Click(this, new RoutedEventArgs());
            }));
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
