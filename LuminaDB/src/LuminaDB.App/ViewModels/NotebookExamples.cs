using System.Collections.Generic;

namespace LuminaDB.App.ViewModels
{
    public sealed class NotebookExample
    {
        public NotebookExample(string title, string group, string sql) { Title = title; Group = group; Sql = sql; }
        public string Title { get; }
        /// <summary>"Learn" or "Mistake" (shown as two parts of the menu).</summary>
        public string Group { get; }
        public string Sql { get; }
    }

    /// <summary>Ready-made scripts: a sample database, the usual queries, and the usual mistakes to see the margin notes.</summary>
    public static class NotebookExamples
    {
        public static readonly IReadOnlyList<NotebookExample> All = new List<NotebookExample>
        {
            new NotebookExample("Make the sample college database", "Learn", @"-- Sample college database. Run it all with Ctrl+Shift+Enter.
CREATE DATABASE IF NOT EXISTS college CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE college;

CREATE TABLE IF NOT EXISTS courses (
  id INT AUTO_INCREMENT PRIMARY KEY,
  title VARCHAR(80) NOT NULL,
  credits TINYINT NOT NULL DEFAULT 3
);

CREATE TABLE IF NOT EXISTS students (
  id INT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(100) NOT NULL,
  email VARCHAR(120) UNIQUE,
  course_id INT,
  joined DATE,
  CONSTRAINT fk_students_course FOREIGN KEY (course_id) REFERENCES courses(id)
);

CREATE TABLE IF NOT EXISTS marks (
  student_id INT NOT NULL,
  subject VARCHAR(40) NOT NULL,
  score TINYINT NOT NULL,
  PRIMARY KEY (student_id, subject),
  CONSTRAINT fk_marks_student FOREIGN KEY (student_id) REFERENCES students(id) ON DELETE CASCADE
);

INSERT IGNORE INTO courses (id, title, credits) VALUES
  (1, 'HNDIT', 4), (2, 'Software Engineering', 3), (3, 'Networking', 3);

INSERT IGNORE INTO students (id, name, email, course_id, joined) VALUES
  (1, 'Nimal Perera', 'nimal@college.lk', 1, '2025-01-10'),
  (2, 'Kamala Silva', 'kamala@college.lk', 2, '2025-02-01'),
  (3, 'Ruwan Jayasuriya', 'ruwan@college.lk', 1, '2025-02-14'),
  (4, 'Fathima Rizvi', 'fathima@college.lk', 3, '2025-03-03'),
  (5, 'ශාන්ති ද සිල්වා', 'shanthi@college.lk', 1, NULL);

INSERT IGNORE INTO marks (student_id, subject, score) VALUES
  (1, 'DBMS', 78), (1, 'Java', 65), (2, 'DBMS', 88), (3, 'DBMS', 54), (4, 'Networks', 91), (5, 'DBMS', 70);

SELECT * FROM students;
"),
            new NotebookExample("JOIN two tables", "Learn", @"SELECT s.name, c.title AS course
FROM students s
JOIN courses c ON c.id = s.course_id
ORDER BY s.name;
"),
            new NotebookExample("GROUP BY with an average", "Learn", @"SELECT subject, COUNT(*) AS students, ROUND(AVG(score), 1) AS average
FROM marks
GROUP BY subject
HAVING COUNT(*) >= 1
ORDER BY average DESC;
"),
            new NotebookExample("CREATE TABLE with keys", "Learn", @"CREATE TABLE IF NOT EXISTS payments (
  id INT AUTO_INCREMENT PRIMARY KEY,
  student_id INT NOT NULL,
  amount DECIMAL(10,2) NOT NULL,
  paid_on DATE NOT NULL DEFAULT (CURRENT_DATE),
  CONSTRAINT fk_payments_student FOREIGN KEY (student_id) REFERENCES students(id)
);
"),
            new NotebookExample("Change rows safely (UPDATE with WHERE)", "Learn", @"UPDATE students
SET email = 'nimal.perera@college.lk'
WHERE id = 1;
"),
            new NotebookExample("Misspelt command", "Mistake", @"SELET name FROM students;
"),
            new NotebookExample("Misspelt table", "Mistake", @"SELECT * FROM student;
"),
            new NotebookExample("Misspelt column", "Mistake", @"SELECT nmae, email FROM students;
"),
            new NotebookExample("Missing semicolon", "Mistake", @"SELECT * FROM students
SELECT * FROM courses;
"),
            new NotebookExample("Extra comma", "Mistake", @"SELECT name, email, FROM students;
"),
            new NotebookExample("= NULL instead of IS NULL", "Mistake", @"SELECT name FROM students WHERE joined = NULL;
"),
            new NotebookExample("Wrong number of values", "Mistake", @"INSERT INTO courses (title, credits) VALUES ('Mathematics');
"),
            new NotebookExample("A reserved word as a column name", "Mistake", @"CREATE TABLE orders (id INT PRIMARY KEY, order INT, desc VARCHAR(50));
"),
            new NotebookExample("DELETE without WHERE", "Mistake", @"DELETE FROM marks;
"),
        };

        public const string Welcome = @"-- Welcome to the Notebook.
-- Write SQL here. Mistakes are marked in the margin before you run.
-- Ctrl+Enter runs the command under the cursor; Ctrl+Shift+Enter runs everything.
-- New to SQL? Examples > Make the sample college database.

SELECT VERSION(), NOW();
";
    }
}
