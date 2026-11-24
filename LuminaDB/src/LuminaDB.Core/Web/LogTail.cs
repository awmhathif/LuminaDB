using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LuminaDB.Core.Web
{
    /// <summary>
    /// Reads the end of a log that another process is still writing (Caddy, Apache, php-cgi keep theirs open).
    /// Used by the Logs tab: it remembers where it stopped and returns only new lines next time.
    /// </summary>
    public sealed class LogTail
    {
        public const int MaxStartBytes = 256 * 1024;
        private readonly string _path;
        private long _position = -1;
        private string _partial = "";

        public LogTail(string path) { _path = path; }

        public string Path => _path;

        /// <summary>New complete lines since the last call (the first call returns the last ~256 KB).</summary>
        public List<string> ReadNew()
        {
            var result = new List<string>();
            if (!File.Exists(_path)) { _position = 0; _partial = ""; return result; }
            using (var fs = Open(_path))
            {
                long len = fs.Length;
                if (_position < 0) _position = Math.Max(0, len - MaxStartBytes);
                if (len < _position) { _position = 0; _partial = ""; }    // rotated or cleared
                if (len == _position) return result;
                fs.Seek(_position, SeekOrigin.Begin);
                var buf = new byte[len - _position];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                _position += read;
                var text = _partial + Encoding.UTF8.GetString(buf, 0, read);
                int last = text.LastIndexOf('\n');
                if (last < 0) { _partial = text; return result; }
                _partial = text.Substring(last + 1);
                foreach (var line in text.Substring(0, last).Split('\n')) result.Add(line.TrimEnd('\r'));
            }
            return result;
        }

        /// <summary>All lines of a log that may be open elsewhere.</summary>
        public static List<string> ReadAll(string path)
        {
            var result = new List<string>();
            if (!File.Exists(path)) return result;
            using (var fs = Open(path))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null) result.Add(line);
            }
            return result;
        }

        /// <summary>Empties a log another process holds open (truncating works where deleting doesn't).</summary>
        public static void Clear(string path)
        {
            if (!File.Exists(path)) return;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) fs.SetLength(0);
        }

        private static FileStream Open(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }
}
