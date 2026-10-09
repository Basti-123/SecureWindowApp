using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SecureWindowApp
{
    internal sealed class SessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _filePath;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _cleared;

        public SessionStore(string filePath) => _filePath = filePath;

        // Clear is ordered with all writes. Queued writes and shutdown saves must
        // not recreate a deleted session for the remainder of this app run.
        public void Clear()
        {
            _writeLock.Wait();
            try
            {
                File.Delete(_filePath);
                _cleared = true;
            }
            finally { _writeLock.Release(); }
        }

        public void Save(List<string> urls)
        {
            _writeLock.Wait();
            try
            {
                if (_cleared) return;
                EnsureDirectory();
                File.WriteAllText(_filePath + ".tmp", JsonSerializer.Serialize(urls, JsonOptions));
                File.Move(_filePath + ".tmp", _filePath, overwrite: true);
            }
            finally { _writeLock.Release(); }
        }

        public async Task SaveAsync(List<string> urls)
        {
            string content = JsonSerializer.Serialize(urls, JsonOptions);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cleared) return;
                EnsureDirectory();
                await File.WriteAllTextAsync(_filePath + ".tmp", content).ConfigureAwait(false);
                File.Move(_filePath + ".tmp", _filePath, overwrite: true);
            }
            finally { _writeLock.Release(); }
        }

        private void EnsureDirectory()
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
    }
}
