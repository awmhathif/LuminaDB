<?php
// File upload form + handler. Stores into ./uploads with a random safe name, returns JSON for API clients.
declare(strict_types=1);
$dir = __DIR__ . '/uploads';
if (!is_dir($dir)) {
    mkdir($dir, 0775, true);
}
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    header('Content-Type: application/json; charset=utf-8');
    $f = $_FILES['file'] ?? null;
    if (!$f || $f['error'] !== UPLOAD_ERR_OK) {
        http_response_code(400);
        echo json_encode(['ok' => false, 'error' => 'upload error code ' . ($f['error'] ?? 'none')]);
        exit;
    }
    $finfo = new finfo(FILEINFO_MIME_TYPE);
    $mime = $finfo->file($f['tmp_name']);
    $ext = strtolower(pathinfo($f['name'], PATHINFO_EXTENSION));
    $safe = bin2hex(random_bytes(8)) . ($ext !== '' && preg_match('/^[a-z0-9]{1,8}$/', $ext) ? ".$ext" : '');
    if (!move_uploaded_file($f['tmp_name'], "$dir/$safe")) {
        http_response_code(500);
        echo json_encode(['ok' => false, 'error' => 'move_uploaded_file failed']);
        exit;
    }
    echo json_encode(['ok' => true, 'original' => $f['name'], 'stored' => $safe, 'size' => $f['size'],
                      'mime' => $mime, 'sha256' => hash_file('sha256', "$dir/$safe"),
                      'comment' => $_POST['comment'] ?? ''], JSON_UNESCAPED_UNICODE);
    exit;
}
?><!doctype html><html><head><meta charset="utf-8"><title>Upload</title></head><body>
<h1>Upload a file</h1>
<form method="post" enctype="multipart/form-data">
  <input type="file" name="file" required> <input name="comment" placeholder="comment">
  <button>Upload</button>
</form>
<p>upload_max_filesize=<?= htmlspecialchars(ini_get('upload_max_filesize')) ?>, post_max_size=<?= htmlspecialchars(ini_get('post_max_size')) ?></p>
</body></html>
