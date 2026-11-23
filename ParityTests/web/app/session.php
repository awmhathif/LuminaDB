<?php
// Session "login" counter: POST user=... logs in, each GET increments a per-session counter.
declare(strict_types=1);
session_start();
header('Content-Type: application/json; charset=utf-8');
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    if (isset($_POST['logout'])) {
        $_SESSION = [];
        session_destroy();
        echo json_encode(['ok' => true, 'logged_out' => true]);
        exit;
    }
    $user = trim((string)($_POST['user'] ?? ''));
    if ($user === '') {
        http_response_code(422);
        echo json_encode(['ok' => false, 'error' => 'user required']);
        exit;
    }
    session_regenerate_id(true);
    $_SESSION['user'] = $user;
    $_SESSION['count'] = 0;
}
if (!isset($_SESSION['user'])) {
    http_response_code(401);
    echo json_encode(['ok' => false, 'error' => 'not logged in']);
    exit;
}
$_SESSION['count'] = ($_SESSION['count'] ?? 0) + 1;
echo json_encode(['ok' => true, 'user' => $_SESSION['user'], 'count' => $_SESSION['count'],
                  'sid_len' => strlen(session_id())], JSON_UNESCAPED_UNICODE);
