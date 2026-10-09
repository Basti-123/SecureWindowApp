# Shared Veyra browser interface

`index.html`, `shell.css`, and `shell.js` are the UI for **both** desktop builds. The marketing site under `website/` is independent.

The shared shell owns the title bar, window buttons, tabs, navigation toolbar, address/search resolution, Google new-tab page, themes, local suggestions, bookmarks menu, and editable eight-shortcut grid. The bundled Inter variable font (SIL Open Font License in `fonts/OFL.txt`) and SVG icons eliminate native font/icon dependencies. Tokens and the Light/Dark/System definitions appear first in `shell.css`. Application chrome is exactly 48 + 56 CSS pixels; adapters position browsing content below 104 native logical pixels at 100% zoom.

Windows embeds these files into the single-file EXE and materializes them in the application's local data directory. `BrowserShell.cs` hosts the trusted shell in a separate WebView2 profile and forwards commands to the existing browser/session/capture-protection controller. The collapsed XAML controls temporarily preserve that controller's state and handlers; visible UI and focus order come entirely from the shared shell. `WindowChrome` provides native edge resizing without a native title bar.

macOS copies the same `ui/` directory into the application bundle. `SharedBrowserShell` hosts it in a dedicated WKWebView, while `ShellHost` places the selected browsing WKWebView above it. The native title bar buttons are hidden. Shell preferences use UserDefaults; browsing data uses WebKit's website data store. Deleting cookies/cache therefore preserves appearance and shortcuts on both platforms.

Adapters send `{type: "state", state: {...}}`; the shell sends `{action, value}`. State is sampled every 120 ms and sent only when changed. The shell preserves input focus and does not replace the tab DOM unless tab state changes. Windows uses the [WebView2 message bridge](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/communicate-btwn-web-native); macOS uses [WKScriptMessageHandler](https://developer.apple.com/documentation/webkit/wkscriptmessagehandler). Only the bundled top-level shell can issue privileged commands. Browsing views do not receive the bridge. CSP blocks remote shell scripts and network connections. User titles/addresses are inserted as text.

CSS `-webkit-app-region` declarations mark draggable and interactive regions. Since ordinary WebView2/WKWebView hosts do not implement Electron's drag handling, the adapters also translate blank-title-bar mouse events into native window movement. Buttons stay at the same right-hand positions on both platforms.

Keyboard: Ctrl/Cmd+L focuses the omnibox; T creates a tab; Shift+T reopens a closed tab; W closes a tab; D bookmarks; R reloads. Tab selection supports arrow keys, Home/End, and Delete. Closing the final tab returns Home. Dialogs trap Tab/Shift+Tab, restore focus, and close with Escape. Suggestions support Up/Down/Enter/Escape. Reduced motion disables animation. Tabs scroll horizontally and can be reordered; pin/unpin is available in the tab menu or by secondary click. Pinning currently lasts for the open session; the existing session files retain URL lists.

The Google search form always submits an encoded Google query. The omnibox resolves HTTP/HTTPS URLs, bare hosts, localhost, and searches in shared JavaScript, avoiding differences between native address resolvers. Suggestions use local history (up to 50 entries) and bookmarks; they do not contact a remote autocomplete service. The extension area explicitly reports that extensions are unavailable in these embedded browsers.

## Verification

- `dotnet build` cross-compiles the Windows host on macOS. It does **not** exercise Windows window movement, resizing, capture protection, input routing, or WebView2 rendering.
- On Windows, `Veyra.exe --smoke-test` uses an isolated temporary data directory and checks browser/capture initialization plus the shared shell's DOM and bridge. The existing Windows CI job runs this mode.
- `./macos/build.sh` builds arm64 and x86_64 and runs address-resolution, WebKit initialization, and shared-shell tests.
- `tests/shell-smoke.js` is the same assertion suite used by the Windows smoke mode and `macos/Tests/ShellSmoke.swift`: eight shortcuts/four columns, bundled font, chrome height, all themes, modal keyboard loops, Escape, shortcut editing, loading/pinned states, autocomplete, and native command messages. Tests never navigate to a remote site or clear user data.
- For a standalone preview: `python3 -m http.server 8765 --directory ui`, then open `http://localhost:8765`. Desktop-only actions are simulated in this preview.

## Limits on parity

This is one shared application UI, not a claim of pixel-identical rendering between Chromium and WebKit. Font rasterization, OS scaling, focus routing, resizing, websites, and native permission behavior still require runtime checks on both operating systems. Compare matching 1200×780 and 820×540 logical viewports, all three themes, 100%/125%/200% Windows scaling and Retina/non-Retina macOS displays, overflow/pinned/loading tabs, keyboard navigation, overlays, and window controls.

Native browser surfaces are temporarily hidden while shell panels or suggestions are open. A local in-memory page snapshot supplies the background so overlays remain usable above native child-window surfaces on both platforms. The background stays frozen until the overlay closes; the selected live page is then restored without reloading. If native snapshot capture fails, the shell falls back to its theme background. No snapshot files are written to disk by the application. The shell itself uses custom scrollbars; third-party sites keep their own document styling. Windows capture exclusion and native tab audio mute remain available. macOS shows those capabilities as unavailable; no equivalent protection or audio capability is claimed. No 100% functional or visual parity guarantee is possible with the current two different browser engines and OS security APIs.
