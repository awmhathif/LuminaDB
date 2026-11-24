<?php
require __DIR__ . '/config.php';

$status = 'not checked';
try {
    $version = db()->query('SELECT VERSION()')->fetchColumn();
    $status = 'connected to ' . DB_NAME . ' (' . $version . ')';
} catch (PDOException $ex) {
    $status = 'not connected: ' . $ex->getMessage();
}
?>
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{PROJECT}}</title>
    <link rel="stylesheet" href="style.css">
</head>
<body>
<main>
    <h1>{{PROJECT}}</h1>
    <p class="lead">Your project is running. Edit <code>index.php</code> and refresh to see the change.</p>
    <div class="card">
        <p><strong>Database:</strong> <?= e($status) ?></p>
        <p class="muted">Settings are in <code>config.php</code>. Use <code>db()</code> to run queries, and <code>e()</code> when you echo text.</p>
    </div>
</main>
</body>
</html>
