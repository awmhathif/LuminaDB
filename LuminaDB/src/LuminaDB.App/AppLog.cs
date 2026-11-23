using System;
using System.IO;
using System.Text;

namespace LuminaDB.App
{
    /// <summary>logs\app.log: what the app itself did and any unexpected errors (for the Doctor and bug reports).</summary>
    public static class AppLog
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init(string logsFolder)
        {
            try
            {
                Directory.CreateDirectory(logsFolder);
                _path = System.IO.Path.Combine(logsFolder, "app.log");
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static string LogFile => _path;

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

        private static void Write(string level, string message)
        {
            if (_path == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
