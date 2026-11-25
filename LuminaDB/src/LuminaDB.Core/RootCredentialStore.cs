using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LuminaDB.Core
{
    /// <summary>
    /// Keeps the MariaDB root password so the user never has to type it to start/stop the server.
    /// Encrypted with Windows DPAPI for the current user. On another PC (USB stick) it can't be
    /// decrypted: <see cref="Load"/> returns null and the UI asks for the password once.
    /// An empty password (fresh install) is stored as "no file".
    /// </summary>
    public sealed class RootCredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LuminaDB.root.v1");
        private readonly string _path;
        private readonly object _lock = new object();
        private DateTime _cachedStamp;
        private string _cached;
        private bool _hasCache;

        public RootCredentialStore(string path) { _path = path ?? throw new ArgumentNullException(nameof(path)); }

        public static RootCredentialStore For(AppPaths paths, EngineSetKind kind) =>
            new RootCredentialStore(Path.Combine(paths.Config, "root-" + EngineSet.FolderName(kind) + ".secret"));

        public bool HasStoredPassword => File.Exists(_path);

        /// <summary>The stored password, "" if none was ever set, or null if the file exists but can't be read here.</summary>
        /// <remarks>
        /// Called for every connection (the activity line asks each second), so the decrypted value is kept and the
        /// file is read and decrypted again only when its time stamp changes.
        /// </remarks>
        public string Load()
        {
            if (!File.Exists(_path)) return "";
            try
            {
                var stamp = File.GetLastWriteTimeUtc(_path);
                lock (_lock) if (_hasCache && stamp == _cachedStamp) return _cached;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                var value = Encoding.UTF8.GetString(plain);
                lock (_lock) { _cached = value; _cachedStamp = stamp; _hasCache = true; }
                return value;
            }
            catch (CryptographicException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Save(string password)
        {
            lock (_lock) _hasCache = false;
            if (string.IsNullOrEmpty(password)) { JsonFile.Delete(_path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
    }
}
