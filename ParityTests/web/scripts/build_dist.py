"""Build the trimmed, minimal bundle into ../dist and report raw + zipped sizes.

usage: python -I build_dist.py
Layout produced (what the Windows app would ship):
  dist/php/        PHP 8.6.0 NTS: php.exe, php-cgi.exe, php8.dll, 10 extensions + their dependency DLLs,
                   VC++ 14.51 runtime (vcruntime140.dll, vcruntime140_1.dll, msvcp140.dll), php.ini
  dist/caddy/      caddy.exe + Caddyfile
  dist/phpmyadmin/ phpMyAdmin 5.2.3 trimmed (no setup/, doc/, examples/, js/src, unused themes/locales)
  dist/apache/     (alternative) Apache 2.4.71 trimmed to the modules our httpd-parity.conf uses
Zips go to ../dist-zips/.
"""
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.normpath(os.path.join(HERE, ".."))
BUNDLE = os.path.join(WEB, "bundle")
DIST = os.path.join(WEB, "dist")
ZIPS = os.path.join(WEB, "dist-zips")

EXTS = ["mysqli", "pdo_mysql", "mbstring", "openssl", "curl", "fileinfo", "gd", "intl", "zip", "sodium"]
# dependency DLLs per extension, taken from the PE import scan (results/pe_php.txt)
EXT_DEPS = {
    "openssl": ["libcrypto-4-x64.dll", "libssl-4-x64.dll"],
    "curl": ["libcrypto-4-x64.dll", "libssl-4-x64.dll", "libssh2.dll", "nghttp2.dll", "brotlidec.dll", "brotlicommon.dll"],
    "intl": ["icuuc77.dll", "icuin77.dll", "icudt77.dll"],
    "sodium": ["libsodium.dll"],
}
PHP_CORE = ["php.exe", "php-cgi.exe", "php8.dll", "license.txt", "readme-redist-bins.txt",
            "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"]
PMA_DROP = ["setup", "doc", "examples", "js/src", "themes/metro", "themes/original", "themes/bootstrap",
            "config.sample.inc.php", "show_config_errors.php", "babel.config.json", "package.json", "yarn.lock",
            "composer.json", "composer.lock", "CONTRIBUTING.md"]
PMA_KEEP_LOCALES = {"si", "ta", "en_GB"}
APACHE_MODULES = ["mod_mime.so", "mod_dir.so", "mod_authz_core.so", "mod_authz_host.so", "mod_alias.so",
                  "mod_rewrite.so", "mod_log_config.so", "mod_headers.so", "mod_setenvif.so", "mod_env.so",
                  "mod_proxy.so", "mod_proxy_fcgi.so"]


def du(path):
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            total += os.path.getsize(os.path.join(root, f))
    return total


def zipdir(src, dst):
    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _, files in os.walk(src):
            for f in files:
                fp = os.path.join(root, f)
                z.write(fp, os.path.relpath(fp, os.path.dirname(src)))
    return os.path.getsize(dst)


def mb(n):
    return "%.1f MB" % (n / 1048576)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    for d in (DIST, ZIPS):
        if os.path.isdir(d):
            shutil.rmtree(d)
        os.makedirs(d)
    # --- php ---
    src = os.path.join(BUNDLE, "php86")
    dst = os.path.join(DIST, "php")
    os.makedirs(os.path.join(dst, "ext"))
    files = set(PHP_CORE)
    for e in EXTS:
        files.update(EXT_DEPS.get(e, []))
    for f in sorted(files):
        shutil.copy2(os.path.join(src, f), os.path.join(dst, f))
    for e in EXTS:
        shutil.copy2(os.path.join(src, "ext", "php_%s.dll" % e), os.path.join(dst, "ext"))
    shutil.copy2(os.path.join(src, "php.ini"), os.path.join(dst, "php.ini"))
    # --- phpMyAdmin ---
    pma = os.path.join(DIST, "phpmyadmin")
    shutil.copytree(os.path.join(BUNDLE, "phpmyadmin"), pma)
    for rel in PMA_DROP:
        p = os.path.join(pma, rel)
        if os.path.isdir(p):
            shutil.rmtree(p)
        elif os.path.exists(p):
            os.remove(p)
    for loc in os.listdir(os.path.join(pma, "locale")):
        if loc not in PMA_KEEP_LOCALES:
            shutil.rmtree(os.path.join(pma, "locale", loc))
    # --- caddy ---
    cd = os.path.join(DIST, "caddy")
    os.makedirs(cd)
    shutil.copy2(os.path.join(BUNDLE, "caddy", "caddy.exe"), cd)
    shutil.copy2(os.path.join(BUNDLE, "caddy", "Caddyfile"), cd)
    # --- apache (alternative) ---
    ad = os.path.join(DIST, "apache")
    asrc = os.path.join(BUNDLE, "apache")
    shutil.copytree(os.path.join(asrc, "bin"), os.path.join(ad, "bin"),
                    ignore=shutil.ignore_patterns("*.pdb", "ab.exe", "abs.exe", "ApacheMonitor.exe", "htdbm.exe",
                                                  "htcacheclean.exe", "httxt2dbm.exe", "logresolve.exe",
                                                  "rotatelogs.exe", "wintty.exe", "openssl.exe", "iconv",
                                                  "lua5*.dll", "curl.exe", "*.pl"))
    os.makedirs(os.path.join(ad, "modules"))
    for m in APACHE_MODULES:
        shutil.copy2(os.path.join(asrc, "modules", m), os.path.join(ad, "modules"))
    os.makedirs(os.path.join(ad, "conf"))
    os.makedirs(os.path.join(ad, "logs"))
    for f in ("httpd-parity.conf", "mime.types"):
        shutil.copy2(os.path.join(asrc, "conf", f), os.path.join(ad, "conf"))
    # --- sizes ---
    rows = []
    for name in ("php", "phpmyadmin", "caddy", "apache"):
        p = os.path.join(DIST, name)
        rows.append((name, du(p), zipdir(p, os.path.join(ZIPS, name + ".zip"))))
    print("%-12s %12s %12s" % ("component", "raw", "zipped"))
    for n, r, z in rows:
        print("%-12s %12s %12s" % (n, mb(r), mb(z)))
    caddy_total = sum(r for n, r, _ in rows if n != "apache"), sum(z for n, _, z in rows if n != "apache")
    apache_total = sum(r for n, r, _ in rows if n != "caddy"), sum(z for n, _, z in rows if n != "caddy")
    print("TOTAL php+pma+caddy   raw=%s zipped=%s" % (mb(caddy_total[0]), mb(caddy_total[1])))
    print("TOTAL php+pma+apache  raw=%s zipped=%s (apache mod_proxy_fcgi; mod_php would need the TS build instead)"
          % (mb(apache_total[0]), mb(apache_total[1])))
    icu = sum(os.path.getsize(os.path.join(DIST, "php", f)) for f in EXT_DEPS["intl"])
    print("of which intl/ICU DLLs: %s raw (dropping intl saves this)" % mb(icu))
    print("for reference: full PHP zip 37.8 MB, phpMyAdmin zip 15.7 MB, Caddy zip 17.5 MB, Apache zip 13.8 MB")


if __name__ == "__main__":
    main()
