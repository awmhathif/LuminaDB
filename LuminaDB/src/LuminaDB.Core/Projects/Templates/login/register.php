<?php
require __DIR__ . '/auth.php';

$errors = [];
$name = '';
$email = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $name = trim($_POST['name'] ?? '');
    $email = trim($_POST['email'] ?? '');
    $password = $_POST['password'] ?? '';
    $confirm = $_POST['confirm'] ?? '';

    if ($name === '') {
        $errors[] = 'Enter your name.';
    }
    if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
        $errors[] = 'Enter a valid email address.';
    }
    if (strlen($password) < 8) {
        $errors[] = 'Use a password of at least 8 characters.';
    }
    if ($password !== $confirm) {
        $errors[] = 'The two passwords are different.';
    }

    if (!$errors) {
        try {
            $stmt = db()->prepare('INSERT INTO users (name, email, password_hash) VALUES (?, ?, ?)');
            // password_hash() salts and hashes; never store the password itself.
            $stmt->execute([$name, $email, password_hash($password, PASSWORD_DEFAULT)]);
            sign_in((int)db()->lastInsertId());
            header('Location: index.php');
            exit;
        } catch (PDOException $ex) {
            if ((int)($ex->errorInfo[1] ?? 0) === 1062) {
                $errors[] = 'There is already an account with that email. Sign in instead.';
            } else {
                throw $ex;
            }
        }
    }
}

page_start('Create account');
?>
<h1>Create an account</h1>
<p class="lead">Already registered? <a href="login.php">Sign in</a>.</p>
<?php foreach ($errors as $error): ?><div class="error"><?= e($error) ?></div><?php endforeach; ?>
<form class="card" method="post" action="register.php">
    <input type="hidden" name="csrf" value="<?= e(csrf_token()) ?>">
    <label>Name <input type="text" name="name" value="<?= e($name) ?>" required maxlength="100"></label>
    <label>Email <input type="email" name="email" value="<?= e($email) ?>" required maxlength="150"></label>
    <label>Password <input type="password" name="password" required minlength="8"></label>
    <label>Confirm password <input type="password" name="confirm" required minlength="8"></label>
    <button type="submit">Create account</button>
</form>
<?php
page_end();
