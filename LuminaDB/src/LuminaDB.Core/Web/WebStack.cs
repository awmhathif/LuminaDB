using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LuminaDB.Core.Web
{
    /// <summary>
    /// The web half of an engine set on disk: engines\&lt;set&gt;\php, \caddy, \apache, \phpmyadmin, \composer, \templates.
    /// Pieces are optional (Caddy needs Windows 10, the Legacy set has Apache only); <see cref="Missing"/> says what's absent.
    /// </summary>
    public sealed class WebStack
    {
        private WebStack(string phpDir, string caddyDir, string apacheDir, string phpMyAdminDir, string extrasDir)
        {
            PhpDir = phpDir;
            CaddyDir = caddyDir;
            ApacheDir = apacheDir;
            PhpMyAdminDir = phpMyAdminDir;
            ExtrasDir = extrasDir;
        }

        public string PhpDir { get; }
        public string CaddyDir { get; }
        public string ApacheDir { get; }
        public string PhpMyAdminDir { get; }
        /// <summary>Holds composer\composer.phar and templates\wordpress.zip when the package includes them.</summary>
        public string ExtrasDir { get; }

        public string PhpExe => PhpDir == null ? null : Path.Combine(PhpDir, "php.exe");
        public string PhpCgiExe => PhpDir == null ? null : Path.Combine(PhpDir, "php-cgi.exe");
        public string PhpExtDir => PhpDir == null ? null : Path.Combine(PhpDir, "ext");
        public string CaddyExe => CaddyDir == null ? null : Path.Combine(CaddyDir, "caddy.exe");
        public string HttpdExe => ApacheDir == null ? null : Path.Combine(ApacheDir, "bin", "httpd.exe");
        public string ComposerPhar => ExtrasDir == null ? null : Path.Combine(ExtrasDir, "composer", "composer.phar");
        public string WordPressZip => ExtrasDir == null ? null : Path.Combine(ExtrasDir, "templates", "wordpress.zip");

        public bool HasPhp => Exists(PhpCgiExe) && Exists(PhpExe);
        public bool HasCaddy => Exists(CaddyExe);
        public bool HasApache => Exists(HttpdExe) && Exists(Path.Combine(ApacheDir, "modules", "mod_proxy_fcgi.so"));
        public bool HasPhpMyAdmin => PhpMyAdminDir != null && File.Exists(Path.Combine(PhpMyAdminDir, "index.php"));
        public bool HasComposer => Exists(ComposerPhar);
        public bool HasWordPress => Exists(WordPressZip);

        /// <summary>Caddy is the front server when it's there; otherwise Apache serves everything (Legacy and x86 sets).</summary>
        public bool UsesCaddy => HasCaddy;
        public bool CanServe => HasPhp && (HasCaddy || HasApache);

        /// <summary>Plain sentence for the UI when the stack can't run, or null.</summary>
        public string Missing
        {
            get
            {
                if (!HasPhp) return "PHP is missing from this copy of LuminaDB" + (PhpDir == null ? "." : $" ({PhpDir}). Antivirus may have removed it; running the LuminaDB installer again puts it back.");
                if (!HasCaddy && !HasApache) return "The web server is missing from this copy of LuminaDB. Antivirus may have removed it; running the LuminaDB installer again puts it back.";
                return null;
            }
        }

        /// <summary>PHP version from php.exe's version resource, e.g. 8.5.13.</summary>
        public string PhpVersion
        {
            get
            {
                if (!Exists(PhpExe)) return null;
                var v = FileVersionInfo.GetVersionInfo(PhpExe);
                return v.ProductVersion ?? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            }
        }

        /// <summary>Extensions (by short name) that have a DLL in ext\.</summary>
        public IReadOnlyList<string> AvailableExtensions
        {
            get
            {
                if (PhpExtDir == null || !Directory.Exists(PhpExtDir)) return Array.Empty<string>();
                return Directory.GetFiles(PhpExtDir, "php_*.dll")
                    .Select(f => Path.GetFileNameWithoutExtension(f).Substring(4))
                    .Where(n => !n.Contains("test"))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        /// <summary>The normal layout: engines\&lt;set&gt;\php, \caddy, \apache, \phpmyadmin.</summary>
        public static WebStack FromAppRoot(AppPaths paths, EngineSetKind kind)
        {
            var root = Path.Combine(paths.Engines, EngineSet.FolderName(kind));
            return FromFolder(root);
        }

        /// <summary>
        /// A folder that holds php (or php85), caddy, apache and phpmyadmin side by side. Used by the app and by developer
        /// setups (LUMINA_WEB_DIR pointing at ParityTests\web\bundle).
        /// </summary>
        public static WebStack FromFolder(string root)
        {
            string Sub(params string[] names) => names.Select(n => Path.Combine(root, n)).FirstOrDefault(Directory.Exists);
            return new WebStack(Sub("php", "php85", "php82"), Sub("caddy"), Sub("apache"), Sub("phpmyadmin"), root);
        }

        public static WebStack FromFolders(string phpDir, string caddyDir, string apacheDir, string phpMyAdminDir = null, string extrasDir = null) =>
            new WebStack(phpDir, caddyDir, apacheDir, phpMyAdminDir, extrasDir);

        private static bool Exists(string f) => f != null && File.Exists(f);
    }
}
