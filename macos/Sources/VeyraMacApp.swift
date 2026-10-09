import AppKit
import Combine
import SwiftUI
import WebKit

@main
@MainActor
struct VeyraMacApp: App {
    var body: some Scene {
        WindowGroup("Veyra") {
            BrowserWindow().frame(minWidth: 820, minHeight: 540)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1200, height: 780)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("New tab") { NotificationCenter.default.post(name: .veyraNewTab, object: nil) }
                    .keyboardShortcut("t", modifiers: .command)
            }
            CommandMenu("Navigation") {
                Button("Focus address bar") { NotificationCenter.default.post(name: .veyraFocusAddressBar, object: nil) }
                    .keyboardShortcut("l", modifiers: .command)
                Button("Reopen closed tab") { NotificationCenter.default.post(name: .veyraRestoreTab, object: nil) }
                    .keyboardShortcut("t", modifiers: [.command, .shift])
                Button("Close tab") { NotificationCenter.default.post(name: .veyraCloseTab, object: nil) }
                    .keyboardShortcut("w", modifiers: .command)
                Button("Bookmark page") { NotificationCenter.default.post(name: .veyraBookmark, object: nil) }
                    .keyboardShortcut("d", modifiers: .command)
                Button("Reload page") { NotificationCenter.default.post(name: .veyraReload, object: nil) }
                    .keyboardShortcut("r", modifiers: .command)
            }
        }
    }
}

@MainActor
private struct BrowserWindow: View {
    @StateObject private var browser = BrowserModel()
    var body: some View {
        SharedBrowserShell(browser: browser)
            .ignoresSafeArea()
            .onAppear(perform: browser.start)
            .onDisappear(perform: browser.saveSessionNow)
    }
}

// Only this trusted, bundled WKWebView receives a native command bridge.
@MainActor
private final class ShellWebView: WKWebView {
    var mouseDownEvent: NSEvent?
    override func mouseDown(with event: NSEvent) {
        mouseDownEvent = event
        super.mouseDown(with: event)
    }
}

@MainActor
private final class ShellHost: NSView {
    let shell: ShellWebView
    var content: WKWebView?
    var overlay = false
    private var configuredWindow: NSWindow?

    init(shell: ShellWebView) {
        self.shell = shell
        super.init(frame: .zero)
        addSubview(shell)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let window, window !== configuredWindow else { return }
        configuredWindow = window
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.titlebarSeparatorStyle = .none
        window.styleMask.insert(.fullSizeContentView)
        window.isMovableByWindowBackground = false
        window.minSize = NSSize(width: 820, height: 540)
        for control in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            window.standardWindowButton(control)?.isHidden = true
        }
    }
    override func layout() {
        super.layout()
        shell.frame = bounds
        content?.frame = NSRect(x: 0, y: 0, width: bounds.width, height: max(0, bounds.height - 104))
    }
    func show(_ view: WKWebView?) {
        if content !== view {
            content?.removeFromSuperview()
            content = view
            if let view { addSubview(view, positioned: .above, relativeTo: shell) }
            needsLayout = true
        }
        content?.isHidden = overlay
    }
}

@MainActor
private struct SharedBrowserShell: NSViewRepresentable {
    @ObservedObject var browser: BrowserModel
    func makeCoordinator() -> Coordinator { Coordinator(browser) }
    func makeNSView(context: Context) -> ShellHost {
        let configuration = WKWebViewConfiguration()
        // Keep appearance/shortcut preferences separate from browsing-data deletion.
        configuration.websiteDataStore = .nonPersistent()
        configuration.userContentController.add(context.coordinator, name: "veyra")
        let shell = ShellWebView(frame: .zero, configuration: configuration)
        shell.navigationDelegate = context.coordinator
        let host = ShellHost(shell: shell)
        context.coordinator.attach(host)
        if let url = Bundle.main.url(forResource: "index", withExtension: "html", subdirectory: "ui") {
            context.coordinator.trustedURL = url
            shell.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        } else {
            shell.loadHTMLString("<h1>Veyra interface assets are missing.</h1>", baseURL: nil)
        }
        return host
    }
    func updateNSView(_ nsView: ShellHost, context: Context) { context.coordinator.update() }
    static func dismantleNSView(_ nsView: ShellHost, coordinator: Coordinator) {
        coordinator.stop()
        nsView.shell.configuration.userContentController.removeScriptMessageHandler(forName: "veyra")
        nsView.content?.removeFromSuperview()
    }

    @MainActor
    final class Coordinator: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        let browser: BrowserModel
        weak var host: ShellHost?
        var trustedURL: URL?
        private var ready = false
        private var timer: Timer?
        private var observers: [NSObjectProtocol] = []
        private var lastState = ""
        private var restoredFrame: NSRect?
        private var mouseMonitor: Any?
        private var overlayGeneration = 0
        init(_ browser: BrowserModel) { self.browser = browser }
        func attach(_ host: ShellHost) {
            self.host = host
            mouseMonitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown) { [weak host] event in
                if event.window === host?.window { host?.shell.mouseDownEvent = event }
                return event
            }
            timer = Timer.scheduledTimer(withTimeInterval: 0.12, repeats: true) { [weak self] _ in
                Task { @MainActor in self?.update() }
            }
            for name in [Notification.Name.veyraNewTab, .veyraFocusAddressBar, .veyraRestoreTab, .veyraCloseTab, .veyraBookmark, .veyraReload] {
                observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] notification in
                    Task { @MainActor in self?.command(notification.name) }
                })
            }
        }
        func stop() { timer?.invalidate(); if let mouseMonitor { NSEvent.removeMonitor(mouseMonitor) }; observers.forEach(NotificationCenter.default.removeObserver); observers.removeAll() }
        private func command(_ name: Notification.Name) {
            // WindowGroup commands only affect the focused window.
            guard host?.window?.isKeyWindow == true else { return }
            switch name {
            case .veyraNewTab: browser.addTab()
            case .veyraFocusAddressBar:
                host?.window?.makeFirstResponder(host?.shell)
                host?.shell.evaluateJavaScript("window.veyra.receive({type:'focusAddress'})")
            case .veyraRestoreTab: browser.restoreClosedTab()
            case .veyraCloseTab: if let tab = browser.selectedTab { browser.closeTab(tab) }
            case .veyraBookmark: browser.addFavorite()
            case .veyraReload: browser.reload()
            default: break
            }
            update()
        }
        func update() {
            guard let host else { return }
            let selected = browser.selectedTab
            host.window?.title = selected?.isStartPage == false ? "\(selected?.title ?? "") – Veyra" : "Veyra"
            host.show(selected?.isStartPage == false ? selected?.makeWebView() : nil)
            guard ready else { return }
            let state: [String: Any] = [
                "tabs": browser.tabs.map { tab -> [String: Any] in
                    ["id": tab.id.uuidString, "title": tab.title, "home": tab.isStartPage, "pinned": tab.isPinned,
                     "loading": tab.webView?.isLoading == true && !tab.isStartPage]
                },
                "selected": selected?.id.uuidString ?? "", "home": selected?.isStartPage ?? true,
                "address": selected?.address ?? "", "canBack": browser.canGoBack && selected?.isStartPage == false,
                "canForward": browser.canGoForward && selected?.isStartPage == false,
                "loading": browser.isLoading && selected?.isStartPage == false, "ready": selected != nil,
                "favorites": browser.favorites.map { ["title": $0.title, "url": $0.url] },
                "canMute": false, "muted": false, "capture": "unavailable", "message": browser.message, "error": ""
            ]
            guard let data = try? JSONSerialization.data(withJSONObject: ["type": "state", "state": state], options: [.sortedKeys]),
                  let json = String(data: data, encoding: .utf8), json != lastState else { return }
            lastState = json
            host.shell.evaluateJavaScript("window.veyra.receive(\(json))")
        }
        func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame,
                  message.frameInfo.request.url == trustedURL,
                  let body = message.body as? [String: String], let action = body["action"],
                  let value = body["value"], value.count <= 65536, let host else { return }
            let tab = browser.tabs.first { $0.id.uuidString == value }
            switch action {
            case "ready":
                ready = true
                var preferences: [String: Any] = [:]
                for key in ["veyra.theme", "veyra.shortcuts", "veyra.history"] {
                    if let data = UserDefaults.standard.data(forKey: key),
                       let value = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) { preferences[key] = value }
                }
                if let data = try? JSONSerialization.data(withJSONObject: ["type": "preferences", "preferences": preferences]),
                   let json = String(data: data, encoding: .utf8) { host.shell.evaluateJavaScript("window.veyra.receive(\(json))") }
            case "preference":
                if let data = value.data(using: .utf8), let preference = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let key = preference["key"] as? String, ["veyra.theme", "veyra.shortcuts", "veyra.history"].contains(key),
                   let value = preference["value"], let stored = try? JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed]) {
                    UserDefaults.standard.set(stored, forKey: key)
                }
            case "overlay":
                let opening = value == "true"
                guard opening != host.overlay else { return }
                host.overlay = opening
                overlayGeneration += 1
                let generation = overlayGeneration
                if opening, let view = host.content {
                    Task { @MainActor [weak self, weak host] in
                        guard let image = try? await view.takeSnapshot(configuration: nil),
                              let self, let host, host.overlay, generation == self.overlayGeneration,
                              let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
                              let png = bitmap.representation(using: .png, properties: [:]),
                              let data = try? JSONSerialization.data(withJSONObject: ["type": "pagePreview", "image": "data:image/png;base64," + png.base64EncodedString()]),
                              let json = String(data: data, encoding: .utf8) else { return }
                        _ = try? await host.shell.evaluateJavaScript("window.veyra.receive(\(json))")
                    }
                }
                host.content?.isHidden = host.overlay
            case "drag": if let event = host.shell.mouseDownEvent { host.window?.performDrag(with: event) }
            case "minimize": host.window?.miniaturize(nil)
            case "maximize":
                if let window = host.window {
                    if let frame = restoredFrame { window.setFrame(frame, display: true); restoredFrame = nil }
                    else if let frame = window.screen?.visibleFrame { restoredFrame = window.frame; window.setFrame(frame, display: true) }
                }
            case "closeWindow": browser.saveSessionNow(); NSApplication.shared.terminate(nil)
            case "newTab": browser.addTab()
            case "selectTab": if let tab { browser.select(tab.id) }
            case "closeTab": if let tab { browser.closeTab(tab) }
            case "navigate": browser.addressText = value; browser.navigateAddress()
            case "back": browser.goBack()
            case "forward": browser.goForward()
            case "reload": browser.reload()
            case "stop": browser.stopLoading()
            case "home": browser.goHome()
            case "bookmark": browser.addFavorite()
            case "removeFavorite": browser.removeFavorite(value)
            case "duplicate": browser.duplicateTab()
            case "restore": browser.restoreClosedTab()
            case "closeOthers": browser.closeOtherTabs()
            case "clearSession": browser.clearSession()
            case "pinTab": if let tab { browser.pinTab(tab) }
            case "moveTab":
                if let data = value.data(using: .utf8), let move = try? JSONSerialization.jsonObject(with: data) as? [String: String] {
                    browser.moveTab(move["id"] ?? "", before: move["before"] ?? "")
                }
            case "zoomIn": browser.zoom(0.1)
            case "zoomOut": browser.zoom(-0.1)
            case "zoomReset": browser.selectedTab?.webView?.pageZoom = 1
            case "clearData": browser.clearBrowsingData()
            default: break
            }
            update()
        }
        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            decisionHandler(navigationAction.request.url == trustedURL ? .allow : .cancel)
        }
    }
}

@MainActor
private final class BrowserModel: ObservableObject {
    @Published private(set) var tabs: [BrowserTab] = []
    @Published var selectedTabID: UUID?
    @Published var addressText = ""
    @Published var startPageQuery = ""
    @Published private(set) var favorites: [Favorite] = []
    @Published private(set) var canGoBack = false
    @Published private(set) var canGoForward = false
    @Published private(set) var isLoading = false
    @Published var showMessage = false
    @Published var message = ""

    private var closedTabs: [URL?] = []
    private let maxTabCount = 12
    private let homeURL = URL(string: "https://www.google.com")!
    private var hasStarted = false
    private var sessionSaveTask: Task<Void, Never>?

    private var supportDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Veyra", isDirectory: true)
    }

    private var favoritesFile: URL { supportDirectory.appendingPathComponent("favorites.json") }
    private var sessionFile: URL { supportDirectory.appendingPathComponent("session.json") }

    var selectedTab: BrowserTab? {
        tabs.first { $0.id == selectedTabID }
    }

    func start() {
        guard !hasStarted else { return }
        hasStarted = true
        loadFavorites()

        let restoredURLs = loadSession()
        if restoredURLs.isEmpty {
            addTab()
            return
        }

        for url in restoredURLs.prefix(maxTabCount) {
            addTab(url: url, select: selectedTabID == nil)
        }
    }

    func addTab(url: URL? = nil, select: Bool = true) {
        guard tabs.count < maxTabCount else {
            present("Veyra unterstützt höchstens \(maxTabCount) Tabs.")
            return
        }

        let initialURL = url.flatMap { isHomeURL($0) ? nil : $0 }
        let tab = BrowserTab(initialURL: initialURL)
        tab.onOpenNewTab = { [weak self] url in self?.addTab(url: url) }
        tab.onStateChange = { [weak self, weak tab] in
            guard let self, let tab else { return }
            self.tabDidChange(tab)
        }
        tabs.append(tab)

        if select || selectedTabID == nil {
            selectedTabID = tab.id
            syncAddressField()
        }

        requestSessionSave()
    }

    func select(_ id: UUID) {
        guard tabs.contains(where: { $0.id == id }) else { return }
        selectedTabID = id
        syncAddressField()
    }

    func closeTab(_ tab: BrowserTab, remember: Bool = true) {
        guard tabs.count > 1, let index = tabs.firstIndex(where: { $0.id == tab.id }) else {
            if let onlyTab = selectedTab { onlyTab.showStartPage(); syncAddressField(); requestSessionSave() }
            return
        }

        if remember { closedTabs.append(tab.isStartPage ? nil : URL(string: tab.address)) }
        let wasSelected = tab.id == selectedTabID
        tab.dispose()
        tabs.remove(at: index)

        if wasSelected {
            selectedTabID = tabs[min(index, tabs.count - 1)].id
            syncAddressField()
        }
        requestSessionSave()
    }

    func removeFavorite(_ url: String) { favorites.removeAll { $0.url == url }; saveFavorites() }
    func duplicateTab() { addTab(url: selectedTab.flatMap { $0.isStartPage ? nil : URL(string: $0.address) }) }
    func restoreClosedTab() {
        guard tabs.count < maxTabCount, !closedTabs.isEmpty else { return }
        addTab(url: closedTabs.removeLast())
    }
    func closeOtherTabs() {
        guard let selected = selectedTab else { return }
        for tab in tabs where tab.id != selected.id { closeTab(tab, remember: false) }
    }
    func clearSession() {
        sessionSaveTask?.cancel()
        try? FileManager.default.removeItem(at: sessionFile)
        present("Saved session cleared. Open tabs will be saved on the next change or exit.")
    }
    func pinTab(_ tab: BrowserTab) {
        tab.isPinned.toggle()
        tabs = tabs.filter { $0.isPinned } + tabs.filter { !$0.isPinned }
        requestSessionSave()
    }
    func moveTab(_ id: String, before targetID: String) {
        guard let source = tabs.firstIndex(where: { $0.id.uuidString == id }),
              let target = tabs.firstIndex(where: { $0.id.uuidString == targetID }),
              source != target, tabs[source].isPinned == tabs[target].isPinned else { return }
        let tab = tabs.remove(at: source)
        tabs.insert(tab, at: target - (source < target ? 1 : 0))
        requestSessionSave()
    }
    func zoom(_ delta: Double) {
        guard let view = selectedTab?.webView else { return }
        view.pageZoom = min(2, max(0.5, view.pageZoom + delta))
    }

    func navigateAddress() {
        guard let url = AddressResolver.resolveInput(addressText) else {
            present("Die Adresse konnte nicht verarbeitet werden.")
            return
        }
        if let selectedTab {
            selectedTab.navigate(to: url)
        } else {
            addTab(url: url)
        }
    }

    func searchFromStartPage(_ input: String) {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        var components = URLComponents(string: "https://www.google.com/search")!
        components.queryItems = [URLQueryItem(name: "q", value: trimmed)]
        if let url = components.url { selectedTab?.navigate(to: url) }
        startPageQuery = ""
    }

    func openStartPageShortcut(_ address: String) {
        guard let url = URL(string: address), BrowserTab.isAllowed(url) else { return }
        selectedTab?.navigate(to: url)
    }

    func goBack() { selectedTab?.webView?.goBack() }
    func goForward() { selectedTab?.webView?.goForward() }

    func reloadOrStop() {
        guard let tab = selectedTab, let webView = tab.webView else { return }
        if webView.isLoading { webView.stopLoading() } else { webView.reload() }
        tabDidChange(tab)
    }

    func reload() { selectedTab?.webView?.reload() }
    func stopLoading() {
        selectedTab?.webView?.stopLoading()
        if let selectedTab { tabDidChange(selectedTab) }
    }

    func goHome() { selectedTab?.showStartPage() }

    func syncAddressField() {
        guard let tab = selectedTab else {
            addressText = ""
            canGoBack = false
            canGoForward = false
            isLoading = false
            return
        }
        addressText = tab.address
        canGoBack = tab.webView?.canGoBack ?? false
        canGoForward = tab.webView?.canGoForward ?? false
        isLoading = tab.webView?.isLoading ?? false
    }

    func addFavorite() {
        guard let tab = selectedTab, let url = URL(string: tab.address), BrowserTab.isAllowed(url) else {
            present("Diese Seite kann nicht als Favorit gespeichert werden.")
            return
        }

        let title = tab.title.isEmpty ? url.absoluteString : tab.title
        if let index = favorites.firstIndex(where: { $0.url == url.absoluteString }) {
            favorites[index] = Favorite(title: title, url: url.absoluteString)
        } else {
            favorites.append(Favorite(title: title, url: url.absoluteString))
        }
        saveFavorites()
        present("Favorit gespeichert.")
    }

    func openFavorite(_ favorite: Favorite) {
        guard let url = URL(string: favorite.url), BrowserTab.isAllowed(url) else { return }
        if let selectedTab { selectedTab.navigate(to: url) } else { addTab(url: url) }
    }

    func clearFavorites() {
        favorites.removeAll()
        saveFavorites()
    }

    func clearBrowsingData() {
        let store = WKWebsiteDataStore.default()
        let types = WKWebsiteDataStore.allWebsiteDataTypes()
        store.fetchDataRecords(ofTypes: types) { [weak self] records in
            store.removeData(ofTypes: types, for: records) {
                DispatchQueue.main.async {
                    self?.present("Cookies, Cache und Websitedaten wurden gelöscht.")
                }
            }
        }
    }

    func saveSessionNow() {
        sessionSaveTask?.cancel()
        let urls = tabs.map { $0.address.isEmpty ? homeURL.absoluteString : $0.address }
        do {
            try FileManager.default.createDirectory(at: supportDirectory, withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(urls)
            try data.write(to: sessionFile, options: .atomic)
        } catch {
            present("Die Sitzung konnte nicht gespeichert werden.")
        }
    }

    private func tabDidChange(_ tab: BrowserTab) {
        if tab.id == selectedTabID {
            addressText = tab.address
            canGoBack = tab.webView?.canGoBack ?? false
            canGoForward = tab.webView?.canGoForward ?? false
            isLoading = tab.webView?.isLoading ?? false
        }
        requestSessionSave()
    }

    private func requestSessionSave() {
        sessionSaveTask?.cancel()
        sessionSaveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(750))
            guard let self, !Task.isCancelled else { return }
            self.saveSessionNow()
        }
    }

    private func loadFavorites() {
        guard let data = try? Data(contentsOf: favoritesFile),
              let decoded = try? JSONDecoder().decode([Favorite].self, from: data) else { return }
        favorites = decoded.filter { favorite in
            guard let url = URL(string: favorite.url) else { return false }
            return BrowserTab.isAllowed(url)
        }
    }

    private func saveFavorites() {
        do {
            try FileManager.default.createDirectory(at: supportDirectory, withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(favorites)
            try data.write(to: favoritesFile, options: .atomic)
        } catch {
            present("Die Favoriten konnten nicht gespeichert werden.")
        }
    }

    private func loadSession() -> [URL] {
        guard let data = try? Data(contentsOf: sessionFile),
              let strings = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return strings.compactMap(URL.init(string:)).filter(BrowserTab.isAllowed)
    }

    private func present(_ text: String) {
        message = text
        showMessage = true
    }

    private func isHomeURL(_ url: URL) -> Bool {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.scheme?.lowercased() == "https",
              components.host?.lowercased() == "www.google.com" else { return false }
        return components.path.isEmpty || components.path == "/"
    }
}

@MainActor
private final class BrowserTab: NSObject, ObservableObject, Identifiable, WKNavigationDelegate, WKUIDelegate {
    let id = UUID()
    var isPinned = false
    @Published private(set) var title = "New tab"
    @Published private(set) var address = ""
    @Published private(set) var isStartPage: Bool

    private(set) var webView: WKWebView?
    private var pendingURL: URL?
    var onOpenNewTab: ((URL) -> Void)?
    var onStateChange: (() -> Void)?

    init(initialURL: URL?) {
        isStartPage = initialURL == nil
        pendingURL = initialURL
        super.init()
    }

    func makeWebView() -> WKWebView {
        if let webView { return webView }

        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false

        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = self
        view.uiDelegate = self
        view.allowsBackForwardNavigationGestures = true
        webView = view

        if let pendingURL {
            self.pendingURL = nil
            view.load(URLRequest(url: pendingURL))
        }
        return view
    }

    func navigate(to url: URL) {
        guard Self.isAllowed(url) else { return }
        isStartPage = false
        address = url.absoluteString
        if let webView {
            webView.load(URLRequest(url: url))
        } else {
            pendingURL = url
        }
        onStateChange?()
    }

    func showStartPage() {
        isStartPage = true
        address = ""
        title = "New tab"
        pendingURL = nil
        webView?.stopLoading()
        webView?.loadHTMLString("", baseURL: nil)
        onStateChange?()
    }

    func dispose() {
        webView?.stopLoading()
        webView?.navigationDelegate = nil
        webView?.uiDelegate = nil
        webView = nil
        onOpenNewTab = nil
        onStateChange = nil
    }

    static func isAllowed(_ url: URL) -> Bool {
        guard let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = url.host,
              !host.isEmpty else { return false }
        return true
    }

    private func syncFromWebView(_ view: WKWebView) {
        if let url = view.url, Self.isAllowed(url) { address = url.absoluteString }
        let pageTitle = view.title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        title = pageTitle.isEmpty ? (view.url?.host ?? "New tab") : String(pageTitle.prefix(48))
        onStateChange?()
    }

    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        syncFromWebView(webView)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        syncFromWebView(webView)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        syncFromWebView(webView)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        syncFromWebView(webView)
    }

    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationAction: WKNavigationAction,
        decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
    ) {
        guard let url = navigationAction.request.url, Self.isAllowed(url) else {
            decisionHandler(.cancel)
            return
        }
        decisionHandler(.allow)
    }

    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationResponse: WKNavigationResponse,
        decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void
    ) {
        decisionHandler(navigationResponse.canShowMIMEType ? .allow : .cancel)
    }

    func webView(
        _ webView: WKWebView,
        createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction,
        windowFeatures: WKWindowFeatures
    ) -> WKWebView? {
        guard navigationAction.targetFrame == nil,
              let url = navigationAction.request.url,
              Self.isAllowed(url) else { return nil }
        onOpenNewTab?(url)
        return nil
    }
}

private struct Favorite: Codable, Identifiable {
    var title: String
    var url: String
    var id: String { url }
}

private extension Notification.Name {
    static let veyraRestoreTab = Notification.Name("VeyraRestoreTab")
    static let veyraCloseTab = Notification.Name("VeyraCloseTab")
    static let veyraBookmark = Notification.Name("VeyraBookmark")
    static let veyraReload = Notification.Name("VeyraReload")
    static let veyraNewTab = Notification.Name("VeyraNewTab")
    static let veyraFocusAddressBar = Notification.Name("VeyraFocusAddressBar")
}
