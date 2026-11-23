<?php
// HTML student list with add / edit / delete forms (POST-redirect-GET), driver switch mysqli|pdo.
declare(strict_types=1);
require __DIR__ . '/lib.php';

$driver = ($_GET['driver'] ?? 'mysqli') === 'pdo' ? 'pdo' : 'mysqli';
$repo = new Students($driver);
$msg = '';
try {
    if ($_SERVER['REQUEST_METHOD'] === 'POST') {
        $op = $_POST['op'] ?? '';
        if ($op === 'add' && trim($_POST['name'] ?? '') !== '') {
            $repo->create(trim($_POST['name']), trim($_POST['course'] ?? ''));
        } elseif ($op === 'edit') {
            $repo->update((int)$_POST['id'], trim($_POST['name'] ?? ''), trim($_POST['course'] ?? ''));
        } elseif ($op === 'delete') {
            $repo->delete((int)$_POST['id']);
        }
        header('Location: index.php?driver=' . $driver, true, 303);
        exit;
    }
    $rows = $repo->all();
} catch (Throwable $e) {
    $rows = [];
    $msg = 'Database error: ' . $e->getMessage();
}
?><!doctype html>
<html lang="si">
<head><meta charset="utf-8"><title>Students (<?= h($driver) ?>)</title></head>
<body>
<h1>Students — driver: <?= h($driver) ?></h1>
<p><a href="?driver=mysqli">mysqli</a> | <a href="?driver=pdo">PDO</a> |
   <a href="session.php">session counter</a> | <a href="upload.php">upload</a> |
   <a href="api.php">JSON API</a> | <a href="students">pretty URL</a></p>
<?php if ($msg): ?><p style="color:red"><?= h($msg) ?></p><?php endif; ?>
<table border="1" cellpadding="4">
<tr><th>ID</th><th>Name</th><th>Course</th><th>By</th><th></th></tr>
<?php foreach ($rows as $r): ?>
<tr>
  <td><?= (int)$r['id'] ?></td>
  <td><?= h($r['name']) ?></td>
  <td><?= h($r['course']) ?></td>
  <td><?= h($r['created_by']) ?></td>
  <td>
    <form method="post" style="display:inline">
      <input type="hidden" name="op" value="edit"><input type="hidden" name="id" value="<?= (int)$r['id'] ?>">
      <input name="name" value="<?= h($r['name']) ?>"><input name="course" value="<?= h($r['course']) ?>">
      <button>Save</button>
    </form>
    <form method="post" style="display:inline">
      <input type="hidden" name="op" value="delete"><input type="hidden" name="id" value="<?= (int)$r['id'] ?>">
      <button>Delete</button>
    </form>
  </td>
</tr>
<?php endforeach; ?>
</table>
<h2>Add</h2>
<form method="post">
  <input type="hidden" name="op" value="add">
  <input name="name" placeholder="නම / பெயர் / name" required maxlength="200">
  <input name="course" placeholder="course">
  <button>Add</button>
</form>
<p>Rows: <?= count($rows) ?> · PHP <?= h(PHP_VERSION) ?> (<?= h(PHP_SAPI) ?>)</p>
</body>
</html>
