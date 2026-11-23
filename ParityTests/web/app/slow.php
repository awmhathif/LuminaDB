<?php
// Simulates a slow request (e.g. a big phpMyAdmin import) to show head-of-line blocking.
$s = max(0, min(10, (int)($_GET['s'] ?? 2)));
sleep($s);
header('Content-Type: application/json');
echo json_encode(['slept' => $s, 'pid' => getmypid()]);
