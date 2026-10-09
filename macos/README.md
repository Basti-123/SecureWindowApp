# Veyra for macOS

The macOS host uses SwiftUI, AppKit, and WebKit. It renders the same bundled HTML/CSS/JavaScript browser interface as Windows. See [the shared UI architecture and parity limits](../ui/README.md).

## Build

The build script uses the installed Swift command line tools. It creates a universal app bundle for Apple Silicon and Intel Macs running macOS 13 or later:

```sh
./macos/build.sh
open macos/build/Veyra.app
```

The build runs smoke checks for address/search handling, initializing an in-memory `WKWebView`, and loading the shared shell with DOM and bridge assertions. Those checks do not replace interactive testing of the app window.

The local bundle is not signed or notarized for public distribution.

## Platform differences

Tabs, HTTP/HTTPS navigation, search, favorites, and session restore are implemented with `WKWebView`. macOS does not provide a supported equivalent to the Windows `SetWindowDisplayAffinity` capture exclusion used by the Windows app. Veyra for macOS therefore labels screen capture protection as unavailable instead of claiming that it is active.

The macOS app stores its favorites and session under `~/Library/Application Support/Veyra/`. The Windows app remains a separate WPF application and must still be tested on Windows for Windows-specific behavior.
