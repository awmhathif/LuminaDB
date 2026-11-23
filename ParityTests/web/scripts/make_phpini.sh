#!/bin/sh
# usage: make_phpini.sh <phpdir> <tmpdir>   -> writes <phpdir>/php.ini from php.ini-development
P="$1"; TMP="$2"; mkdir -p "$TMP"
sed -e 's#^;extension_dir = "ext"#extension_dir = "ext"#' \
    -e 's#^;\(extension=\(mysqli\|pdo_mysql\|mbstring\|openssl\|curl\|fileinfo\|gd\|intl\|zip\|sodium\)\)$#\1#' \
    -e "s#^;upload_tmp_dir =.*#upload_tmp_dir = \"$TMP\"#" \
    -e "s#^;session.save_path = \"/tmp\"#session.save_path = \"$TMP\"#" \
    -e 's#^upload_max_filesize = .*#upload_max_filesize = 64M#' \
    -e 's#^post_max_size = .*#post_max_size = 64M#' \
    -e 's#^memory_limit = .*#memory_limit = 256M#' \
    -e 's#^;date.timezone =.*#date.timezone = Asia/Colombo#' \
    "$P/php.ini-development" > "$P/php.ini"
grep -nE '^(extension|extension_dir|upload_tmp_dir|session.save_path|date.timezone)' "$P/php.ini"
