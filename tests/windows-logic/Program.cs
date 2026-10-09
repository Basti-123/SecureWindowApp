using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using SecureWindowApp;

string directory = Path.Combine(Path.GetTempPath(), "VeyraLogicTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    passed++;
    Console.WriteLine("PASS " + name);
}

try
{
    string file = Path.Combine(directory, "session.json");
    var urls = new List<string> { "https://example.com/", "https://example.org/" };
    var store = new SessionStore(file);
    await store.SaveAsync(urls);
    Check(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file))!.SequenceEqual(urls), "session JSON remains compatible");
    store.Clear();
    store.Save(urls); // The same synchronous save is used on window close.
    await store.SaveAsync(urls);
    Check(!File.Exists(file), "clear survives shutdown and asynchronous saves");

    var nextRun = new SessionStore(file);
    await nextRun.SaveAsync(urls);
    Check(File.Exists(file), "saving resumes on the next app run");
    var writers = Enumerable.Range(0, 20).Select(_ => nextRun.SaveAsync(urls)).ToArray();
    nextRun.Clear();
    await Task.WhenAll(writers);
    Check(!File.Exists(file), "clear also blocks queued writers");
    Check(!File.Exists(file + ".tmp"), "session writes leave no temporary file");

    var nonFatal = new[] {
        CoreWebView2ProcessFailedKind.GpuProcessExited,
        CoreWebView2ProcessFailedKind.UtilityProcessExited,
        CoreWebView2ProcessFailedKind.FrameRenderProcessExited,
        CoreWebView2ProcessFailedKind.SandboxHelperProcessExited,
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive,
        CoreWebView2ProcessFailedKind.UnknownProcessExited
    };
    Check(nonFatal.All(kind => !WebViewFailurePolicy.RequiresTabRecreation(kind)), "recoverable failures preserve tabs");
    Check(WebViewFailurePolicy.RequiresTabRecreation(CoreWebView2ProcessFailedKind.BrowserProcessExited) &&
          WebViewFailurePolicy.RequiresTabRecreation(CoreWebView2ProcessFailedKind.RenderProcessExited), "fatal failures remain visible");

    Check(PortableWebViewRuntime.Find(directory) == null, "development build can use installed runtime");
    string runtime = Path.Combine(directory, "WebView2Runtime");
    Directory.CreateDirectory(runtime);
    bool rejected = false;
    try { PortableWebViewRuntime.Find(directory); } catch (FileNotFoundException) { rejected = true; }
    Check(rejected, "incomplete portable runtime is rejected");
    File.WriteAllText(Path.Combine(runtime, "msedgewebview2.exe"), "test fixture");
    Check(PortableWebViewRuntime.Find(directory) == runtime, "portable runtime is resolved relative to the app");
    Console.WriteLine($"{passed} Windows logic checks passed.");
}
finally { Directory.Delete(directory, recursive: true); }
