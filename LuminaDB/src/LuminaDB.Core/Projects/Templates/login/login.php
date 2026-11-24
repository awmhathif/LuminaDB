<?php
require __DIR__ . '/auth.php';

if (current_user() !== null) {
    header('Location: index.php');
    exit;
}

$error = null;
$email = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $email = trim($_POST['email'] ?? '');
    $password = $_POST['password'] ?? '';

    $stmt = db()->prepare('SELECT id, password_hash FROM users WHERE email = ?');
    $stmt->execute([$email]);
    $row = $stmt->fetch();

    // The same message for "no such user" and "wrong password", so nobody can find out which emails exist.
    if ($row && password_verify($password, $row['password_hash'])) {
        if (password_needs_rehash($row['password_hash'], PASSWORD_DEFAULT)) {
            db()->prepare('UPDATE users SET password_hash = ? WHERE id = ?')
                ->execute([password_hash($password, PASSWORD_DEFAULT), $row['id']]);
        }
        sign_in((int)$row['id']);
        header('Location: index.php');
        exit;
    }
    $error = 'That email and password don\'t match.';
}

page_start('Sign in');
?>
<h1>Sign in</h1>
<p class="lead">No account yet? <a href="register.php">Create one</a>.</p>
<?php if ($error): ?><div class="error"><?= e($error) ?></div><?php endif; ?>
<form class="card" method="post" action="login.php">
    <input type="hidden" name="csrf" value="<?= e(csrf_token()) ?>">
    <label>Email <input type="email" name="email" value="<?= e($email) ?>" required autofocus></label>
    <label>Password <input type="password" name="password" required></label>
    <button type="submit">Sign in</button>
</form>
<?php
page_end();
