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

        public RootCredentialStore(string path) { _path = path ?? throw new ArgumentNullException(nameof(path)); }

        public static RootCredentialStore For(AppPaths paths, EngineSetKind kind) =>
            new RootCredentialStore(Path.Combine(paths.Config, "root-" + EngineSet.FolderName(kind) + ".secret"));

        public bool HasStoredPassword => File.Exists(_path);

        /// <summary>The stored password, "" if none was ever set, or null if the file exists but can't be read here.</summary>
        public string Load()
        {
            if (!File.Exists(_path)) return "";
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Save(string password)
        {
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
