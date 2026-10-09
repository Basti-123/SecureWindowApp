using Microsoft.Web.WebView2.Core;

namespace SecureWindowApp
{
    internal static class WebViewFailurePolicy
    {
        // GPU, utility and helper processes recover inside WebView2. A failed
        // subframe also must not permanently disable the surviving main frame.
        public static bool RequiresTabRecreation(CoreWebView2ProcessFailedKind kind) =>
            kind == CoreWebView2ProcessFailedKind.BrowserProcessExited ||
            kind == CoreWebView2ProcessFailedKind.RenderProcessExited;
    }
}
