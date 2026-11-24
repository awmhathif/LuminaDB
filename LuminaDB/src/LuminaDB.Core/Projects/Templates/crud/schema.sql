CREATE TABLE IF NOT EXISTS students (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(150) NOT NULL,
    course VARCHAR(100) NOT NULL DEFAULT '',
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_students_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO students (name, email, course) VALUES
    ('Nimali Perera', 'nimali@example.com', 'HNDIT'),
    ('Kavin Raj', 'kavin@example.com', 'HNDIT'),
    ('Aisha Fazal', 'aisha@example.com', 'HNDA');
