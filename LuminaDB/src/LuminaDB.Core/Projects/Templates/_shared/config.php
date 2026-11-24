<?php
// Database settings for {{PROJECT}}, written by LuminaDB when the project was created.
// LuminaDB shows the same details under Projects > Overview. If you change the root password or the
// server moves to another port, update them here.
declare(strict_types=1);

define('DB_HOST', '127.0.0.1');
define('DB_PORT', {{DB_PORT}});
define('DB_NAME', '{{DB_NAME}}');
define('DB_USER', '{{DB_USER}}');
define('DB_PASS', '{{DB_PASS}}');

/** One shared PDO connection: errors throw, rows come back as arrays, real prepared statements. */
function db(): PDO
{
    static $pdo = null;
    if ($pdo === null) {
        $dsn = 'mysql:host=' . DB_HOST . ';port=' . DB_PORT . ';dbname=' . DB_NAME . ';charset=utf8mb4';
        $pdo = new PDO($dsn, DB_USER, DB_PASS, [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
            PDO::ATTR_EMULATE_PREPARES => false,
        ]);
    }
    return $pdo;
}

/** Escapes text for HTML. Use it on everything you echo that came from a user or the database. */
function e($value): string
{
    return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}
