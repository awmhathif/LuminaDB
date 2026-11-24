<?php
// CRUD starter: list, add, edit and delete students. Every query uses prepared statements, every form has a
// CSRF token, and every change redirects afterwards (POST-redirect-GET) so a refresh never repeats it.
require __DIR__ . '/config.php';
session_start();

if (empty($_SESSION['csrf'])) {
    $_SESSION['csrf'] = bin2hex(random_bytes(16));
}

$errors = [];
$editing = null;
$form = ['name' => '', 'email' => '', 'course' => ''];

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    if (!hash_equals($_SESSION['csrf'], $_POST['csrf'] ?? '')) {
        http_response_code(400);
        exit('The form expired. Go back and try again.');
    }
    $action = $_POST['action'] ?? '';
    $id = (int)($_POST['id'] ?? 0);

    if ($action === 'delete' && $id > 0) {
        $stmt = db()->prepare('DELETE FROM students WHERE id = ?');
        $stmt->execute([$id]);
        $_SESSION['flash'] = 'Student deleted.';
        header('Location: index.php');
        exit;
    }

    $form = [
        'name' => trim($_POST['name'] ?? ''),
        'email' => trim($_POST['email'] ?? ''),
        'course' => trim($_POST['course'] ?? ''),
    ];
    if ($form['name'] === '') {
        $errors[] = 'Enter a name.';
    }
    if (!filter_var($form['email'], FILTER_VALIDATE_EMAIL)) {
        $errors[] = 'Enter a valid email address.';
    }

    if (!$errors) {
        try {
            if ($action === 'update' && $id > 0) {
                $stmt = db()->prepare('UPDATE students SET name = ?, email = ?, course = ? WHERE id = ?');
                $stmt->execute([$form['name'], $form['email'], $form['course'], $id]);
                $_SESSION['flash'] = 'Student updated.';
            } else {
                $stmt = db()->prepare('INSERT INTO students (name, email, course) VALUES (?, ?, ?)');
                $stmt->execute([$form['name'], $form['email'], $form['course']]);
                $_SESSION['flash'] = 'Student added.';
            }
            header('Location: index.php');
            exit;
        } catch (PDOException $ex) {
            // 1062 = duplicate entry: the email column is UNIQUE.
            if ((int)($ex->errorInfo[1] ?? 0) === 1062) {
                $errors[] = 'Another student already uses that email.';
            } else {
                throw $ex;
            }
        }
    }
    if ($action === 'update') {
        $editing = $id;
    }
}

if (isset($_GET['edit']) && !$errors) {
    $stmt = db()->prepare('SELECT * FROM students WHERE id = ?');
    $stmt->execute([(int)$_GET['edit']]);
    $row = $stmt->fetch();
    if ($row) {
        $editing = (int)$row['id'];
        $form = ['name' => $row['name'], 'email' => $row['email'], 'course' => $row['course']];
    }
}

$flash = $_SESSION['flash'] ?? null;
unset($_SESSION['flash']);
$students = db()->query('SELECT id, name, email, course, created_at FROM students ORDER BY id DESC')->fetchAll();
?>
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Students · {{PROJECT}}</title>
    <link rel="stylesheet" href="style.css">
</head>
<body>
<main>
    <h1>Students</h1>
    <p class="lead"><?= count($students) ?> in the <code><?= e(DB_NAME) ?></code> database.</p>

    <?php if ($flash): ?><div class="ok"><?= e($flash) ?></div><?php endif; ?>
    <?php foreach ($errors as $error): ?><div class="error"><?= e($error) ?></div><?php endforeach; ?>

    <form class="card" method="post" action="index.php">
        <input type="hidden" name="csrf" value="<?= e($_SESSION['csrf']) ?>">
        <input type="hidden" name="action" value="<?= $editing ? 'update' : 'create' ?>">
        <input type="hidden" name="id" value="<?= (int)$editing ?>">
        <label>Name <input type="text" name="name" value="<?= e($form['name']) ?>" required maxlength="100"></label>
        <label>Email <input type="email" name="email" value="<?= e($form['email']) ?>" required maxlength="150"></label>
        <label>Course <input type="text" name="course" value="<?= e($form['course']) ?>" maxlength="100"></label>
        <button type="submit"><?= $editing ? 'Save changes' : 'Add student' ?></button>
        <?php if ($editing): ?> <a href="index.php">Cancel</a><?php endif; ?>
    </form>

    <table>
        <thead><tr><th>Name</th><th>Email</th><th>Course</th><th>Added</th><th></th></tr></thead>
        <tbody>
        <?php foreach ($students as $s): ?>
            <tr>
                <td><?= e($s['name']) ?></td>
                <td><?= e($s['email']) ?></td>
                <td><?= e($s['course']) ?></td>
                <td class="muted"><?= e(date('d M Y', strtotime($s['created_at']))) ?></td>
                <td>
                    <a href="index.php?edit=<?= (int)$s['id'] ?>">Edit</a>
                    <form class="inline" method="post" action="index.php" onsubmit="return confirm('Delete <?= e($s['name']) ?>?')">
                        <input type="hidden" name="csrf" value="<?= e($_SESSION['csrf']) ?>">
                        <input type="hidden" name="action" value="delete">
                        <input type="hidden" name="id" value="<?= (int)$s['id'] ?>">
                        <button class="link" type="submit">Delete</button>
                    </form>
                </td>
            </tr>
        <?php endforeach; ?>
        <?php if (!$students): ?><tr><td colspan="5" class="muted">No students yet. Add one above.</td></tr><?php endif; ?>
        </tbody>
    </table>
</main>
</body>
</html>
