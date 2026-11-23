<?php
// Reports extensions + a functional smoke test of each (used by every server test).
header('Content-Type: application/json; charset=utf-8');
$r = [];
foreach (['mysqli','pdo_mysql','mbstring','openssl','curl','fileinfo','gd','intl','zip','sodium'] as $e) { $r[$e] = extension_loaded($e); }
$func = [
  'mbstring' => mb_strlen('ශ්‍රී ලංකා') ,
  'openssl'  => strlen(openssl_random_pseudo_bytes(16)),
  'curl'     => curl_version()['version'] ?? null,
  'gd'       => (function(){ $i = imagecreatetruecolor(10,10); ob_start(); imagepng($i); return strlen(ob_get_clean()); })(),
  'intl'     => (new NumberFormatter('si_LK', NumberFormatter::CURRENCY))->formatCurrency(1234.5, 'LKR'),
  'zip'      => class_exists('ZipArchive'),
  'sodium'   => strlen(sodium_crypto_secretbox_keygen()),
];
echo json_encode(['php' => PHP_VERSION, 'sapi' => PHP_SAPI, 'loaded' => $r, 'functional' => $func,
                  'server' => $_SERVER['SERVER_SOFTWARE'] ?? ''], JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT);
