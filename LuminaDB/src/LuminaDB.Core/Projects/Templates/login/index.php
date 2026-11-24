<?php
require __DIR__ . '/auth.php';
$user = require_login();

page_start('Dashboard');
?>
<h1>Hello, <?= e($user['name']) ?></h1>
<p class="lead">You are signed in as <?= e($user['email']) ?>.</p>
<div class="card">
    <p>Member since <?= e(date('d M Y', strtotime($user['created_at']))) ?>.</p>
    <p class="muted">Pages that call <code>require_login()</code> are only for signed-in users. Build your app from here.</p>
</div>
<form method="post" action="logout.php">
    <input type="hidden" name="csrf" value="<?= e(csrf_token()) ?>">
    <button type="submit">Sign out</button>
</form>
<?php
page_end();
