<?php
// Router for `php -S`: emulates the .htaccess rewrite rules (php -S ignores .htaccess).
$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
if (preg_match('#^/students/?$#', $path)) { require __DIR__ . '/api.php'; return true; }
if (preg_match('#^/students/([0-9]+)/?$#', $path, $m)) { $_GET['id'] = $m[1]; require __DIR__ . '/api.php'; return true; }
return false; // serve file as-is
