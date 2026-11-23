<?php
// Shared helpers for the parity-test student app.
declare(strict_types=1);

const DB_HOST = '127.0.0.1';
const DB_PORT = 3510;
const DB_USER = 'root';
const DB_PASS = '';
const DB_NAME = 'paritytest';

mysqli_report(MYSQLI_REPORT_ERROR | MYSQLI_REPORT_STRICT);

function bootstrap_schema(): void
{
    static $done = false;
    if ($done) {
        return;
    }
    $m = new mysqli(DB_HOST, DB_USER, DB_PASS, '', DB_PORT);
    $m->set_charset('utf8mb4');
    $m->query('CREATE DATABASE IF NOT EXISTS `' . DB_NAME . '` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci');
    $m->select_db(DB_NAME);
    $m->query('CREATE TABLE IF NOT EXISTS students (
        id INT AUTO_INCREMENT PRIMARY KEY,
        name VARCHAR(200) NOT NULL,
        course VARCHAR(100) NOT NULL DEFAULT \'\',
        created_by VARCHAR(10) NOT NULL,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci');
    $m->close();
    $done = true;
}

function db_mysqli(): mysqli
{
    bootstrap_schema();
    $m = new mysqli(DB_HOST, DB_USER, DB_PASS, DB_NAME, DB_PORT);
    $m->set_charset('utf8mb4');
    return $m;
}

function db_pdo(): PDO
{
    bootstrap_schema();
    return new PDO(
        'mysql:host=' . DB_HOST . ';port=' . DB_PORT . ';dbname=' . DB_NAME . ';charset=utf8mb4',
        DB_USER,
        DB_PASS,
        [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION, PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
         PDO::ATTR_EMULATE_PREPARES => false]
    );
}

/** Student repository with two interchangeable drivers, all statements prepared. */
final class Students
{
    public function __construct(private string $driver)
    {
        if (!in_array($driver, ['mysqli', 'pdo'], true)) {
            throw new InvalidArgumentException('driver must be mysqli or pdo');
        }
    }

    public function all(): array
    {
        if ($this->driver === 'pdo') {
            return db_pdo()->query('SELECT id,name,course,created_by FROM students ORDER BY id')->fetchAll();
        }
        $m = db_mysqli();
        $r = $m->query('SELECT id,name,course,created_by FROM students ORDER BY id');
        return $r->fetch_all(MYSQLI_ASSOC);
    }

    public function get(int $id): ?array
    {
        if ($this->driver === 'pdo') {
            $s = db_pdo()->prepare('SELECT id,name,course,created_by FROM students WHERE id = ?');
            $s->execute([$id]);
            $row = $s->fetch();
            return $row === false ? null : $row;
        }
        $m = db_mysqli(); // keep the link referenced: a temporary would close it before execute()
        $s = $m->prepare('SELECT id,name,course,created_by FROM students WHERE id = ?');
        $s->bind_param('i', $id);
        $s->execute();
        $row = $s->get_result()->fetch_assoc();
        return $row ?: null;
    }

    public function create(string $name, string $course): int
    {
        if ($this->driver === 'pdo') {
            $p = db_pdo();
            $s = $p->prepare('INSERT INTO students (name, course, created_by) VALUES (:n, :c, :d)');
            $s->execute([':n' => $name, ':c' => $course, ':d' => 'pdo']);
            return (int)$p->lastInsertId();
        }
        $m = db_mysqli();
        $s = $m->prepare('INSERT INTO students (name, course, created_by) VALUES (?, ?, ?)');
        $d = 'mysqli';
        $s->bind_param('sss', $name, $course, $d);
        $s->execute();
        return (int)$m->insert_id;
    }

    public function update(int $id, string $name, string $course): int
    {
        if ($this->driver === 'pdo') {
            $s = db_pdo()->prepare('UPDATE students SET name = ?, course = ? WHERE id = ?');
            $s->execute([$name, $course, $id]);
            return $s->rowCount();
        }
        $m = db_mysqli(); // keep the link referenced: a temporary would close it before execute()
        $s = $m->prepare('UPDATE students SET name = ?, course = ? WHERE id = ?');
        $s->bind_param('ssi', $name, $course, $id);
        $s->execute();
        return $s->affected_rows;
    }

    public function delete(int $id): int
    {
        if ($this->driver === 'pdo') {
            $s = db_pdo()->prepare('DELETE FROM students WHERE id = ?');
            $s->execute([$id]);
            return $s->rowCount();
        }
        $m = db_mysqli(); // keep the link referenced: a temporary would close it before execute()
        $s = $m->prepare('DELETE FROM students WHERE id = ?');
        $s->bind_param('i', $id);
        $s->execute();
        return $s->affected_rows;
    }
}

function h(?string $s): string
{
    return htmlspecialchars((string)$s, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function json_out(mixed $data, int $code = 200): never
{
    http_response_code($code);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
    exit;
}
