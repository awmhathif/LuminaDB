using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LuminaDB.Core.Web
{
    /// <summary>Files shipped inside LuminaDB.Core.dll (the error page, project templates).</summary>
    internal static class Embedded
    {
        private static readonly Assembly Asm = typeof(Embedded).Assembly;

        /// <summary>Resource names keep their folder, with forward slashes: templates/crud/index.php.</summary>
        public static string[] Names => Map.Keys.ToArray();

        /// <summary>MSBuild's %(RecursiveDir) gives "templates/crud\index.php"; look names up with forward slashes only.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> Map =
            Asm.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);

        public static byte[] Bytes(string resourceName)
        {
            Map.TryGetValue(resourceName.Replace('\\', '/'), out var actual);
            using (var s = actual == null ? null : Asm.GetManifestResourceStream(actual))
            {
                if (s == null) throw new InvalidOperationException("Missing embedded file " + resourceName);
                using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
            }
        }

        public static string Text(string resourceName)
        {
            var b = Bytes(resourceName);
            // Strip a UTF-8 BOM: PHP prints it before <?php, which breaks header() calls.
            int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(b, skip, b.Length - skip);
        }

        public static bool Exists(string resourceName) => Names.Contains(resourceName);
    }

    /// <summary>The friendly PHP error page (SPEC §5.2), installed with php.ini's auto_prepend_file.</summary>
    public static class ErrorPage
    {
        public const string ResourceName = "web/prepend.php";

        public static string Script(string projectsRoot, string projectLogs, int mariaDbPort) =>
            Embedded.Text(ResourceName)
                .Replace("'__PROJECTS__'", PhpString(projectsRoot.Replace('\\', '/')))
                .Replace("'__LOGS__'", PhpString(projectLogs))
                .Replace("'__DBPORT__'", PhpString(mariaDbPort.ToString(CultureInfo.InvariantCulture)));

        /// <summary>A PHP single-quoted string literal.</summary>
        public static string PhpString(string value) => "'" + (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }
}
