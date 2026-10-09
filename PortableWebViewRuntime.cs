using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SecureWindowApp
{
    internal static class PortableWebViewRuntime
    {
        public static string? Find(string appDirectory)
        {
            string folder = Path.Combine(appDirectory, "WebView2Runtime");
            if (!Directory.Exists(folder)) return null; // Development builds can use Evergreen.
            if (!File.Exists(Path.Combine(folder, "msedgewebview2.exe")))
                throw new FileNotFoundException("Die portable WebView2-Laufzeit ist unvollständig. Bitte das gesamte Veyra-ZIP erneut entpacken.");
            return folder;
        }

        public static string? Prepare(string appDirectory)
        {
            string? folder = Find(appDirectory);
            if (folder == null || !System.OperatingSystem.IsWindows() ||
                System.OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return folder;

            // Fixed Version 120+ requires AppContainer read/execute permissions
            // on Windows 10. Grant these to this runtime folder, without setup,
            // elevation, or changing permissions on user data or parent folders.
            var directory = new DirectoryInfo(folder);
            DirectorySecurity security = directory.GetAccessControl();
            foreach (string sid in new[] { "S-1-15-2-1", "S-1-15-2-2" })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),
                    FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            return folder;
        }
    }
}
