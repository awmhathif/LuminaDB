<?php
// Shared helpers for the login starter: sessions, the signed-in user and CSRF tokens.
require __DIR__ . '/config.php';

session_start([
    'cookie_httponly' => true,
    'cookie_samesite' => 'Lax',
    'use_strict_mode' => true,
]);

function current_user(): ?array
{
    if (empty($_SESSION['user_id'])) {
        return null;
    }
    $stmt = db()->prepare('SELECT id, name, email, created_at, last_login FROM users WHERE id = ?');
    $stmt->execute([$_SESSION['user_id']]);
    return $stmt->fetch() ?: null;
}

function require_login(): array
{
    $user = current_user();
    if ($user === null) {
        header('Location: login.php');
        exit;
    }
    return $user;
}

function sign_in(int $userId): void
{
    // A new session id after login stops session fixation.
    session_regenerate_id(true);
    $_SESSION['user_id'] = $userId;
    db()->prepare('UPDATE users SET last_login = NOW() WHERE id = ?')->execute([$userId]);
}

function csrf_token(): string
{
    if (empty($_SESSION['csrf'])) {
        $_SESSION['csrf'] = bin2hex(random_bytes(16));
    }
    return $_SESSION['csrf'];
}

function check_csrf(): void
{
    if (!hash_equals(csrf_token(), $_POST['csrf'] ?? '')) {
        http_response_code(400);
        exit('The form expired. Go back and try again.');
    }
}

function page_start(string $title): void
{
    echo '<!doctype html><html lang="en"><head><meta charset="utf-8">'
        . '<meta name="viewport" content="width=device-width, initial-scale=1">'
        . '<title>' . e($title) . ' · {{PROJECT}}</title><link rel="stylesheet" href="style.css"></head><body><main>';
}

function page_end(): void
{
    echo '</main></body></html>';
}
