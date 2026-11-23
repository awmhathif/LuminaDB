<?php
// JSON API:  GET    api.php[?driver=pdo|mysqli][&id=N]
//            POST   api.php?driver=..   (form or JSON body: name, course)
//            PUT    api.php?id=N&driver=.. (JSON body)
//            DELETE api.php?id=N&driver=..
// Pretty URL (via .htaccess / router.php / Caddy rewrite): /students and /students/N
declare(strict_types=1);
require __DIR__ . '/lib.php';

try {
    $driver = $_GET['driver'] ?? 'pdo';
    $repo = new Students($driver);
    $id = isset($_GET['id']) && $_GET['id'] !== '' ? (int)$_GET['id'] : null;
    $method = $_SERVER['REQUEST_METHOD'];
    $body = $_POST;
    if (str_starts_with($_SERVER['CONTENT_TYPE'] ?? '', 'application/json')) {
        $body = json_decode(file_get_contents('php://input') ?: '{}', true, 512, JSON_THROW_ON_ERROR);
    }
    switch ($method) {
        case 'GET':
            if ($id !== null) {
                $row = $repo->get($id);
                $row ? json_out(['ok' => true, 'driver' => $driver, 'student' => $row])
                     : json_out(['ok' => false, 'error' => 'not found'], 404);
            }
            json_out(['ok' => true, 'driver' => $driver, 'students' => $repo->all(),
                      'php' => PHP_VERSION, 'sapi' => PHP_SAPI, 'pid' => getmypid(),
                      'server' => $_SERVER['SERVER_SOFTWARE'] ?? '']);
        case 'POST':
            $name = trim((string)($body['name'] ?? ''));
            if ($name === '' || mb_strlen($name) > 200) {
                json_out(['ok' => false, 'error' => 'name required (1-200 chars)'], 422);
            }
            $newId = $repo->create($name, (string)($body['course'] ?? ''));
            json_out(['ok' => true, 'id' => $newId, 'student' => $repo->get($newId)], 201);
        case 'PUT':
            if ($id === null) {
                json_out(['ok' => false, 'error' => 'id required'], 400);
            }
            $n = $repo->update($id, (string)($body['name'] ?? ''), (string)($body['course'] ?? ''));
            json_out(['ok' => true, 'affected' => $n, 'student' => $repo->get($id)]);
        case 'DELETE':
            if ($id === null) {
                json_out(['ok' => false, 'error' => 'id required'], 400);
            }
            json_out(['ok' => true, 'affected' => $repo->delete($id)]);
        default:
            header('Allow: GET, POST, PUT, DELETE');
            json_out(['ok' => false, 'error' => 'method not allowed'], 405);
    }
} catch (Throwable $e) {
    json_out(['ok' => false, 'error' => get_class($e) . ': ' . $e->getMessage()], 500);
}
