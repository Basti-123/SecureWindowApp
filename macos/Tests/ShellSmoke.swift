import AppKit
import Darwin
import WebKit

@MainActor
final class ShellSmokeRunner: NSObject, WKNavigationDelegate, WKScriptMessageHandler {
    var view: WKWebView!
    var messages: [[String: String]] = []
    var completed = false

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        if let body = message.body as? [String: String] { messages.append(body) }
    }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in
            do {
                _ = try await webView.callAsyncJavaScript("await document.fonts.ready; return true;", arguments: [:], in: nil, contentWorld: .page)
                let result = try await webView.evaluateJavaScript(self.tests)
                guard let passed = result as? [String], passed.count == 23 else { throw SmokeError.invalidResult }
                // postMessage delivery is asynchronous; flush through a native/WebKit round trip.
                _ = try await webView.evaluateJavaScript("document.title")
                guard messages.contains(where: { $0["action"] == "ready" }),
                      messages.contains(where: { $0["action"] == "navigate" && $0["value"] == "https://www.google.com/search?q=hello%20%26%20world" }),
                      messages.contains(where: { $0["action"] == "pinTab" && $0["value"] == "two" }),
                      messages.contains(where: { $0["action"] == "closeTab" && $0["value"] == "two" }) else { throw SmokeError.missingCommand }
                print("Shared shell smoke test passed (23 DOM assertions, native command bridge, bundled font).")
                if CommandLine.arguments.count == 4 {
                    let directory = URL(fileURLWithPath: CommandLine.arguments[3], isDirectory: true)
                    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                    _ = try await webView.evaluateJavaScript(Self.snapshotSetup)
                    for theme in ["light", "dark"] {
                        _ = try await webView.evaluateJavaScript("window.veyra.receive({type:'preferences',preferences:{'veyra.theme':'\(theme)'}})")
                        let image = try await webView.takeSnapshot(configuration: nil)
                        guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
                              let png = bitmap.representation(using: .png, properties: [:]) else { throw SmokeError.invalidResult }
                        try png.write(to: directory.appendingPathComponent("shell-\(theme).png"))
                    }
                }
                completed = true
                NSApplication.shared.stop(nil)
                Darwin.exit(EXIT_SUCCESS)
            } catch {
                fputs("Shared shell smoke test failed: \(error)\n", stderr)
                Darwin.exit(EXIT_FAILURE)
            }
        }
    }
    enum SmokeError: Error { case invalidResult, missingCommand }
    var tests = ""
    static let snapshotSetup = #"""
    document.documentElement.style.setProperty('--duration','0ms');
    document.activeElement.blur();
    document.getElementById('address').value='';
    document.getElementById('search').value='';
    document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
    window.veyra.receive({type:'preferences',preferences:{'veyra.shortcuts':[
      {title:'Google',url:'https://www.google.com'}, {title:'YouTube',url:'https://www.youtube.com'},
      {title:'Gmail',url:'https://mail.google.com'}, {title:'Maps',url:'https://maps.google.com'},
      {title:'Drive',url:'https://drive.google.com'}, {title:'Calendar',url:'https://calendar.google.com'},
      {title:'GitHub',url:'https://github.com'}, {title:'Wikipedia',url:'https://wikipedia.org'}
    ]}});
    window.veyra.receive({type:'state',state:{tabs:[{id:'docs',title:'Design notes',home:false},{id:'new',title:'New tab',home:true}],selected:'new',home:true,address:'',canBack:false,canForward:false,loading:false,ready:true,favorites:[],capture:'unavailable',message:'',error:''}});
    """#
}

@main
@MainActor
struct ShellSmoke {
    static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        guard CommandLine.arguments.count == 3 || CommandLine.arguments.count == 4 else { Darwin.exit(EXIT_FAILURE) }
        let url = URL(fileURLWithPath: CommandLine.arguments[1])
        let runner = ShellSmokeRunner()
        runner.tests = (try? String(contentsOfFile: CommandLine.arguments[2], encoding: .utf8)) ?? ""
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.userContentController.add(runner, name: "veyra")
        runner.view = WKWebView(frame: NSRect(x: 0, y: 0, width: 1200, height: 780), configuration: config)
        runner.view.navigationDelegate = runner
        runner.view.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        DispatchQueue.main.asyncAfter(deadline: .now() + 25) {
            if !runner.completed { fputs("Shared shell smoke test timed out.\n", stderr); Darwin.exit(EXIT_FAILURE) }
        }
        app.run()
    }
}
