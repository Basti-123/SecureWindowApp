import AppKit
import Darwin
import Foundation
import WebKit

@main
@MainActor
struct WebKitSmoke {
    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.prohibited)

        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.allowsBackForwardNavigationGestures = true

        guard webView.allowsBackForwardNavigationGestures else {
            fputs("WKWebView initialization failed.\n", stderr)
            Darwin.exit(EXIT_FAILURE)
        }

        print("WKWebView smoke test passed: in-memory view initialized.")
    }
}
