<?php
// REST API starter. Every request that isn't a real file comes here (the web server sends it), so the
// routes below see clean URLs:
//   GET    /items          list (optional ?q=name)
//   GET    /items/{id}     one item
//   POST   /items          create  (JSON body: {"name": "...", "price": 10.5, "in_stock": true})
//   PUT    /items/{id}     replace
//   DELETE /items/{id}     delete
// Try it in a browser: open /items. Or from PowerShell:
//   Invoke-RestMethod -Method Post -Uri http://{{PROJECT}}.localhost:{{WEB_PORT}}/items -ContentType application/json -Body '{"name":"Ruler","price":60}'
require __DIR__ . '/config.php';

header('Content-Type: application/json; charset=UTF-8');

function respond(int $status, $body = null): void
{
    http_response_code($status);
    if ($body !== null) {
        echo json_encode($body, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    }
    exit;
}

function read_item(): array
{
    $data = json_decode(file_get_contents('php://input') ?: '', true);
    if (!is_array($data)) {
        respond(400, ['error' => 'Send a JSON object, for example {"name": "Pen", "price": 40}.']);
    }
    $errors = [];
    $name = trim((string)($data['name'] ?? ''));
    if ($name === '' || mb_strlen($name) > 100) {
        $errors['name'] = 'Required, up to 100 characters.';
    }
    $price = $data['price'] ?? 0;
    if (!is_numeric($price) || $price < 0) {
        $errors['price'] = 'Must be a number of 0 or more.';
    }
    if ($errors) {
        respond(422, ['error' => 'Some fields are not valid.', 'fields' => $errors]);
    }
    return ['name' => $name, 'price' => round((float)$price, 2), 'in_stock' => !empty($data['in_stock'] ?? true) ? 1 : 0];
}

function find_item(int $id): array
{
    $stmt = db()->prepare('SELECT id, name, price, in_stock, created_at FROM items WHERE id = ?');
    $stmt->execute([$id]);
    $row = $stmt->fetch();
    if (!$row) {
        respond(404, ['error' => "There is no item $id."]);
    }
    return shape($row);
}

function shape(array $row): array
{
    $row['id'] = (int)$row['id'];
    $row['price'] = (float)$row['price'];
    $row['in_stock'] = (bool)$row['in_stock'];
    return $row;
}

$method = $_SERVER['REQUEST_METHOD'];
$path = rtrim(parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH) ?: '/', '/');

if ($path === '' || $path === '/index.php') {
    respond(200, ['name' => '{{PROJECT}} API', 'endpoints' => ['GET /items', 'GET /items/{id}', 'POST /items', 'PUT /items/{id}', 'DELETE /items/{id}']]);
}

if ($path === '/items') {
    if ($method === 'GET') {
        $q = trim((string)($_GET['q'] ?? ''));
        $stmt = db()->prepare('SELECT id, name, price, in_stock, created_at FROM items WHERE name LIKE ? ORDER BY id');
        $stmt->execute(['%' . $q . '%']);
        respond(200, array_map('shape', $stmt->fetchAll()));
    }
    if ($method === 'POST') {
        $item = read_item();
        db()->prepare('INSERT INTO items (name, price, in_stock) VALUES (?, ?, ?)')
            ->execute([$item['name'], $item['price'], $item['in_stock']]);
        $id = (int)db()->lastInsertId();
        header('Location: /items/' . $id);
        respond(201, find_item($id));
    }
    header('Allow: GET, POST');
    respond(405, ['error' => "$method isn't allowed on /items."]);
}

if (preg_match('#^/items/(\d+)$#', $path, $m)) {
    $id = (int)$m[1];
    if ($method === 'GET') {
        respond(200, find_item($id));
    }
    if ($method === 'PUT') {
        find_item($id);
        $item = read_item();
        db()->prepare('UPDATE items SET name = ?, price = ?, in_stock = ? WHERE id = ?')
            ->execute([$item['name'], $item['price'], $item['in_stock'], $id]);
        respond(200, find_item($id));
    }
    if ($method === 'DELETE') {
        find_item($id);
        db()->prepare('DELETE FROM items WHERE id = ?')->execute([$id]);
        respond(204);
    }
    header('Allow: GET, PUT, DELETE');
    respond(405, ['error' => "$method isn't allowed on /items/$id."]);
}

respond(404, ['error' => "Nothing at $path. Try /items."]);
