using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LuminaDB.Core.Sql;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>
    /// The mistakes people really make, and what the Notebook should say: what was meant, and the corrected SQL.
    /// </summary>
    public class BeginnerMistakeTests
    {
        private readonly ITestOutputHelper _out;
        public BeginnerMistakeTests(ITestOutputHelper output) { _out = output; }

        private static SchemaSnapshot Shop()
        {
            var s = new SchemaSnapshot();
            var p = s.AddTable("shop", "products", "id int", "name varchar(60)", "price decimal(10,2)");
            p.Columns[0].Key = "PRI";
            p.RowEstimate = 8;
            var o = s.AddTable("shop", "orders", "id int", "product_id int", "qty int");
            o.Columns[0].Key = "PRI";
            s.AddDatabase("college");
            return s;
        }

        private static SqlAnalysis Run(string sql, int caret = -1) => new SqlGuard(Shop()).Analyze(sql, "shop", caret);

        private static SqlIssue First(string sql, string code = null)
        {
            var a = Run(sql);
            var bad = a.Issues.Where(i => i.Severity == IssueSeverity.Bad && (code == null || i.Code == code)).ToList();
            Assert.True(bad.Count > 0, "no issue" + (code != null ? " " + code : "") + " for: " + sql + "\nfound: " + string.Join("\n", a.Issues));
            return bad[0];
        }

        private static string Fixed(string sql, string code = null, int option = -1)
        {
            var i = First(sql, code);
            var fixes = option < 0 ? i.Fixes : i.MoreFixes[option].Fixes;
            Assert.NotEmpty(fixes);
            return TextFix.Apply(sql, fixes);
        }

        private static void Clean(string sql)
        {
            var bad = Run(sql).Issues.Where(i => i.Severity == IssueSeverity.Bad).ToList();
            Assert.True(bad.Count == 0, sql + "\n" + string.Join("\n", bad));
        }

        // ------------------------------------------------------------------ the screenshot

        private const string Screenshot =
            "USE shop;\nSELECT *\nFROM products;\nDELETE price;\nFROM products;";

        [Fact]
        public void A_clause_on_its_own_line_blames_the_semicolon_above_not_a_spelling()
        {
            var a = Run(Screenshot);
            Assert.DoesNotContain(a.Issues, i => i.Message.Contains("Did you mean DROP"));
            var cut = a.Issues.Single(i => i.Code == "cut");
            Assert.Equal(Screenshot.LastIndexOf("FROM", StringComparison.Ordinal), cut.Start);
            Assert.Contains("line 4", cut.Message);
            Assert.Equal("Remove the ;", cut.FixLabel);
            Assert.Equal("USE shop;\nSELECT *\nFROM products;\nDELETE price\nFROM products;", TextFix.Apply(Screenshot, cut.Fixes));
        }

        [Fact]
        public void Delete_of_a_column_offers_what_was_meant()
        {
            var i = First(Screenshot, "delete-column");
            Assert.Contains("price is a column of products, not a table", i.Message);
            Assert.Equal("Empty price", i.FixLabel);
            Assert.Equal(new[] { "Delete whole rows", "Remove the column" }, i.MoreFixes.Select(m => m.Label));
            Assert.EndsWith("UPDATE products SET price = NULL;\nFROM products;", TextFix.Apply(Screenshot, i.Fixes));

            // with the ; removed it reads DELETE price FROM products: same three readings, WHERE kept
            const string joined = "DELETE price FROM products WHERE id = 3;";
            Assert.Equal("UPDATE products SET price = NULL WHERE id = 3;", Fixed(joined, "delete-column"));
            Assert.Equal("DELETE FROM products WHERE id = 3;", Fixed(joined, "delete-column", 0));
            Assert.Equal("ALTER TABLE products DROP COLUMN price;", Fixed(joined, "delete-column", 1));
        }

        [Fact]
        public void A_broken_delete_never_reads_as_a_harmless_summary()
        {
            var a = Run("DELETE price;", 3);
            Assert.True(a.HasErrors);
            Assert.DoesNotContain("from .", a.Summary);
            Assert.Equal("Deletes rows, but no table is named yet.", a.Summary);
        }

        [Fact]
        public void The_error_sheet_uses_the_whole_script()
        {
            var a = Run(Screenshot);
            var failed = a.Statements.Last();
            var e = ErrorExplainer.Explain(1064, "You have an error in your SQL syntax; check the manual that corresponds to your MariaDB server version for the right syntax to use near 'FROM products' at line 1", failed, Shop(), "shop");
            ErrorExplainer.UseScriptCheck(e, a, failed);
            Assert.Equal("Most likely: FROM belongs to the command above: the ; on line 4 ended it too early.", e.Body);
            Assert.Equal(5, e.Line);
            Assert.Equal("USE shop;\nSELECT *\nFROM products;\nDELETE price\nFROM products;", TextFix.Apply(Screenshot, e.Fixes));

            // 1064 for "DELETE price" itself: the column reading, not "ends too early"
            var del = a.Statements.Single(x => x.Text.StartsWith("DELETE", StringComparison.Ordinal));
            var e2 = ErrorExplainer.Explain(1064, "You have an error in your SQL syntax; check the manual that corresponds to your MariaDB server version for the right syntax to use near '' at line 1", del, Shop(), "shop");
            ErrorExplainer.UseScriptCheck(e2, a, del);
            Assert.Contains("price is a column of products", e2.Body);
            Assert.DoesNotContain("ends too early", e2.Body);
        }

        [Fact]
        public void Unknown_column_error_for_text_without_quotes_says_quote_it()
        {
            const string sql = "SELECT * FROM products WHERE name = Tea;";
            var a = Run(sql);
            var e = ErrorExplainer.Explain(1054, "Unknown column 'Tea' in 'where clause'", a.Statements[0], Shop(), "shop");
            ErrorExplainer.UseScriptCheck(e, a, a.Statements[0]);
            Assert.Contains("put it in quotes: 'Tea'", e.Body);
            Assert.Equal("SELECT * FROM products WHERE name = 'Tea';", TextFix.Apply(sql, e.Fixes));
        }

        // ------------------------------------------------------------------ DELETE

        [Theory]
        [InlineData("DELETE products;", "from", "DELETE FROM products;")]
        [InlineData("DELETE * FROM products WHERE id = 2;", "shape", "DELETE FROM products WHERE id = 2;")]
        [InlineData("DELETE prodcts;", "from", "DELETE FROM products;")]
        public void Delete_shapes(string sql, string code, string expected) => Assert.Equal(expected, Fixed(sql, code));

        [Fact]
        public void Delete_from_without_a_table_lists_the_tables()
        {
            var i = First("DELETE FROM WHERE id = 1;", "shape");
            Assert.Contains("Which table?", i.Message);
            Assert.Contains("orders, products", i.Message);
        }

        // ------------------------------------------------------------------ UPDATE

        [Theory]
        [InlineData("UPDATE products price = 5 WHERE id = 1;", "set", "UPDATE products SET price = 5 WHERE id = 1;")]
        [InlineData("UPDATE products SET price = 5 AND name = 'Tea' WHERE id = 1;", "set-and", "UPDATE products SET price = 5, name = 'Tea' WHERE id = 1;")]
        [InlineData("UPDATE SET price = 5 WHERE id = 1;", "shape", "UPDATE products SET price = 5 WHERE id = 1;")]
        [InlineData("UPDATE products SET price 5 WHERE id = 1;", "set", "UPDATE products SET price = 5 WHERE id = 1;")]
        [InlineData("UPDATE products SET name = Tea WHERE id = 1;", "text-quote", "UPDATE products SET name = 'Tea' WHERE id = 1;")]
        [InlineData("UPDATE products WHERE id = 1 SET price = 5;", "order", "UPDATE products SET price = 5 WHERE id = 1;")]
        public void Update_shapes(string sql, string code, string expected) => Assert.Equal(expected, Fixed(sql, code));

        [Fact]
        public void Between_and_in_set_is_not_mistaken_for_two_changes()
        {
            Clean("UPDATE products SET price = price * 2 WHERE price BETWEEN 1 AND 5;");
            Clean("UPDATE products SET price = IF(price > 1 AND price < 5, 3, price) WHERE id = 1;");
        }

        // ------------------------------------------------------------------ INSERT

        [Theory]
        [InlineData("INSERT INTO products (name, price) ('Tea', 450);", "values", "INSERT INTO products (name, price) VALUES ('Tea', 450);")]
        [InlineData("INSERT INTO products (3, 'Tea', 450);", "values", "INSERT INTO products VALUES (3, 'Tea', 450);")]
        [InlineData("INSERT INTO products VALUES 3, 'Tea', 450;", "values", "INSERT INTO products VALUES (3, 'Tea', 450);")]
        [InlineData("INSERT INTO products 3, 'Tea', 450;", "values", "INSERT INTO products VALUES (3, 'Tea', 450);")]
        [InlineData("INSERT INTO products VALUES (3, Tea, 450);", "text-quote", "INSERT INTO products VALUES (3, 'Tea', 450);")]
        [InlineData("INSERT INTO products (name) VALUES (O'Brien);", null, null)]
        public void Insert_shapes(string sql, string code, string expected)
        {
            if (expected == null) { Assert.True(Run(sql).HasErrors); return; }   // a stray quote: the unclosed-quote check covers it
            Assert.Equal(expected, Fixed(sql, code));
        }

        [Fact]
        public void Insert_with_column_list_only_asks_for_values()
        {
            var i = First("INSERT INTO products (name, price);", "values");
            Assert.Contains("Now add the values", i.Message);
        }

        // ------------------------------------------------------------------ SELECT

        [Theory]
        [InlineData("SELECT FROM products;", "shape", "SELECT * FROM products;")]
        [InlineData("SELECT name price FROM products;", "comma", "SELECT name, price FROM products;")]
        [InlineData("SELECT name, price FROM products ORDER BY name WHERE price > 100;", "order", "SELECT name, price FROM products WHERE price > 100 ORDER BY name;")]
        [InlineData("SELECT * FROM products LIMIT 5 ORDER BY price;", "order", "SELECT * FROM products ORDER BY price LIMIT 5;")]
        [InlineData("SELECT * FROM products ORDER price;", "by", "SELECT * FROM products ORDER BY price;")]
        [InlineData("SELECT qty, COUNT(*) FROM orders GROUP qty;", "by", "SELECT qty, COUNT(*) FROM orders GROUP BY qty;")]
        [InlineData("SELECT * FROM products WHERE name == 'Tea';", "equals", "SELECT * FROM products WHERE name = 'Tea';")]
        [InlineData("SELECT * FROM products WHERE name = Tea;", "text-quote", "SELECT * FROM products WHERE name = 'Tea';")]
        [InlineData("SELECT name WHERE price > 5;", "shape", "SELECT name FROM products WHERE price > 5;")]
        public void Select_shapes(string sql, string code, string expected) => Assert.Equal(expected, Fixed(sql, code));

        [Fact]
        public void Text_without_quotes_also_offers_the_close_column()
        {
            var i = First("SELECT * FROM products WHERE name = nme;", "text-quote");
            Assert.Equal("Use column name", i.MoreFixes.Single().Label);
        }

        [Theory]
        [InlineData("SELECT * FROM products WHERE;", "empty", "WHERE needs a condition")]
        [InlineData("SELECT * FROM products WHERE price > 100 AND;", "dangling", "stops right after AND")]
        [InlineData("SELECT * FROM products WHERE price > ORDER BY name;", "dangling", "missing after >")]
        [InlineData("SELECT * FROM products LIMIT;", "empty", "LIMIT needs a number")]
        [InlineData("SELECT;", "shape", "SELECT what?")]
        public void Incomplete_commands_say_what_is_missing(string sql, string code, string words) =>
            Assert.Contains(words, First(sql, code).Message);

        [Fact]
        public void Unfinished_command_being_typed_is_left_alone()
        {
            const string sql = "SELECT * FROM products WHERE";
            Assert.DoesNotContain(Run(sql, sql.Length).Issues, i => i.Code == "empty");
            const string upd = "UPDATE products SET";
            Assert.DoesNotContain(Run(upd, upd.Length).Issues, i => i.Severity == IssueSeverity.Bad);
        }

        // ------------------------------------------------------------------ CREATE / DROP / ALTER

        [Theory]
        [InlineData("CREATE products (id INT);", "object", "CREATE TABLE products (id INT);")]
        [InlineData("CREATE shop2;", "object", "CREATE DATABASE shop2;")]
        [InlineData("CREATE TABLE t (id, name);", "type", "CREATE TABLE t (id INT, name VARCHAR(100));")]
        [InlineData("CREATE TABLE t (id INT, price);", "type", "CREATE TABLE t (id INT, price DECIMAL(10,2));")]
        [InlineData("CREATE TABLE t (id INT, name VARCHR(50));", "type", "CREATE TABLE t (id INT, name VARCHAR(50));")]
        [InlineData("CREATE TABLE t (id INT, name VARCHAR);", "type", "CREATE TABLE t (id INT, name VARCHAR(100));")]
        [InlineData("CREATE TABLE t (id INT AUTO_INCREMENT, name VARCHAR(20));", "type", "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(20));")]
        [InlineData("DROP products;", "object", "DROP TABLE products;")]
        [InlineData("DROP college;", "object", "DROP DATABASE college;")]
        [InlineData("DROP COLUMN price;", "object", "ALTER TABLE products DROP COLUMN price;")]
        [InlineData("ALTER products ADD stock INT;", "object", "ALTER TABLE products ADD stock INT;")]
        public void Schema_shapes(string sql, string code, string expected) => Assert.Equal(expected, Fixed(sql, code));

        [Fact]
        public void Half_fixes_are_not_offered()
        {
            // CREATE TABLE shop2; (no columns) isn't valid either, so only DATABASE is offered
            Assert.Empty(First("CREATE shop2;", "object").MoreFixes);
            // products has 3 columns: adding VALUES alone would still fail, so the note explains and offers no button
            var i = First("INSERT INTO products ('Tea', 450);", "values");
            Assert.False(i.HasFix);
            Assert.Contains("Missing VALUES", i.Message);
            // with brackets around (VALUES ...), it is offered when the row fits
            Assert.Equal("CREATE TABLE products (id INT);", Fixed("CREATE products (id INT);", "object"));
        }

        // ------------------------------------------------------------------ cut commands

        [Theory]
        [InlineData("SELECT * FROM products;\nWHERE id = 1;", "SELECT * FROM products\nWHERE id = 1;")]
        [InlineData("UPDATE products;\nSET price = 5 WHERE id = 1;", "UPDATE products\nSET price = 5 WHERE id = 1;")]
        [InlineData("INSERT INTO products (name, price);\nVALUES ('Tea', 450);", "INSERT INTO products (name, price)\nVALUES ('Tea', 450);")]
        [InlineData("SELECT * FROM products;\nORDER BY price;", "SELECT * FROM products\nORDER BY price;")]
        public void A_semicolon_that_cut_a_command_is_found(string sql, string expected) => Assert.Equal(expected, Fixed(sql, "cut"));

        [Fact]
        public void A_clause_at_the_very_start_gets_its_command()
        {
            Assert.Equal("SELECT * FROM products;", Fixed("FROM products;", "cut"));
            Assert.Equal("INSERT INTO products VALUES (1, 'Tea', 2);", Fixed("INTO products VALUES (1, 'Tea', 2);", "cut"));
        }

        [Fact]
        public void Set_and_values_on_their_own_are_still_commands()
        {
            Clean("SET @x = 5;\nSELECT @x;");
            Clean("SET NAMES utf8mb4;");
            Clean("VALUES (1, 2), (3, 4);");
            Clean("SELECT 1;\nSET @a = 2;");
        }

        // ------------------------------------------------------------------ rule 1: safe fixes only

        [Theory]
        [InlineData("DORP TABLE products;", "DROP")]
        [InlineData("DELTE FROM products WHERE id = 1;", "DELETE")]
        [InlineData("TRUNCAT products;", "TRUNCATE")]
        public void A_spelling_guess_never_becomes_a_one_click_destructive_command(string sql, string word)
        {
            var i = First(sql, "command");
            Assert.Contains("If you meant " + word, i.Message);
            Assert.False(i.HasFix);
        }

        [Theory]
        [InlineData("FROM products;")]
        [InlineData("WHERE id = 1;")]
        [InlineData("SELECT * FROM products;\nFROM products;")]
        [InlineData("ORDER BY name;")]
        public void Clause_words_are_never_read_as_drop(string sql)
        {
            foreach (var i in Run(sql).Issues)
            {
                Assert.DoesNotContain("DROP", i.Message);
                Assert.DoesNotContain(i.Fixes.Concat(i.MoreFixes.SelectMany(m => m.Fixes)), f => f.Text.Contains("DROP") || f.Text.Contains("DELETE") || f.Text.Contains("TRUNCATE"));
            }
        }

        /// <summary>Every Fix offered anywhere in these scripts leaves fewer mistakes, and nothing new where it went in.</summary>
        [Theory]
        [InlineData(Screenshot)]
        [InlineData("DELETE price FROM products WHERE id = 3;")]
        [InlineData("UPDATE SET price = 5 WHERE id = 1;\nSELECT name price FROM products ORDER BY name WHERE price > 1;")]
        [InlineData("INSERT INTO products (name, price) ('Tea', 450);\nINSERT INTO products VALUES (3, Tea, 450);")]
        [InlineData("CREATE TABLE t (id INT AUTO_INCREMENT, name VARCHR);\nDROP COLUMN price;\nSELECT * FROM products WHERE name == Tea;")]
        [InlineData("SELECT * FROM products;\nWHERE id = 1;\nUPDATE products price = 5 AND name = 'x';")]
        public void Every_offered_fix_makes_the_script_better(string sql)
        {
            var guard = new SqlGuard(Shop());
            var a = guard.Analyze(sql, "shop");
            int bad = a.Issues.Count(i => i.Severity == IssueSeverity.Bad);
            int tried = 0;
            foreach (var i in a.Issues.Where(i => i.Severity == IssueSeverity.Bad))
                foreach (var fixes in new[] { i.Fixes }.Concat(i.MoreFixes.Select(m => m.Fixes)).Where(f => f.Count > 0))
                {
                    tried++;
                    var after = guard.Analyze(TextFix.Apply(sql, fixes), "shop").Issues.Count(x => x.Severity == IssueSeverity.Bad);
                    Assert.True(after < bad, i + " → " + TextFix.Apply(sql, fixes));
                }
            Assert.True(tried > 0);
            _out.WriteLine(tried + " fixes tried");
        }

        [Fact]
        public void A_fix_that_would_not_help_is_not_offered()
        {
            // the column guess for "nme" would give a valid name, but "Tea" has no close column: only quoting is offered
            var i = First("SELECT * FROM products WHERE name = Tea;", "text-quote");
            Assert.Empty(i.MoreFixes);
            Assert.True(i.HasFix);
        }

        // ------------------------------------------------------------------ no false alarms

        [Theory]
        [InlineData("SELECT p.name, o.qty FROM orders o JOIN products p ON p.id = o.product_id WHERE o.qty > 1 ORDER BY p.name LIMIT 5;")]
        [InlineData("SELECT name AS title, price total FROM products;")]
        [InlineData("SELECT name n FROM products;")]
        [InlineData("SELECT COUNT(*) AS c, qty FROM orders GROUP BY qty HAVING COUNT(*) > 1 ORDER BY c DESC;")]
        [InlineData("SELECT * FROM products WHERE name LIKE 'T%' AND price BETWEEN 1 AND 500;")]
        [InlineData("SELECT * FROM products WHERE id IN (SELECT product_id FROM orders WHERE qty > 2);")]
        [InlineData("SELECT EXTRACT(YEAR FROM NOW());")]
        [InlineData("SELECT * FROM products WHERE price IS NOT NULL;")]
        [InlineData("SELECT name, GROUP_CONCAT(id ORDER BY id) FROM products GROUP BY name;")]
        [InlineData("SELECT name, ROW_NUMBER() OVER (PARTITION BY name ORDER BY price) AS r FROM products;")]
        [InlineData("SELECT * FROM products LIMIT 5 OFFSET 10;")]
        [InlineData("SELECT * FROM products ORDER BY price DESC LIMIT 3;")]
        [InlineData("SELECT * FROM products FOR UPDATE;")]
        [InlineData("SELECT name FROM products UNION SELECT 'x' ORDER BY 1;")]
        [InlineData("DELETE FROM products WHERE id = 3;")]
        [InlineData("DELETE o FROM orders o JOIN products p ON p.id = o.product_id WHERE p.price > 5;")]
        [InlineData("DELETE FROM orders ORDER BY id LIMIT 2;")]
        [InlineData("UPDATE products SET price = price * 1.1, name = CONCAT(name, '!') WHERE id = 1;")]
        [InlineData("UPDATE orders o JOIN products p ON p.id = o.product_id SET o.qty = 2 WHERE p.id = 1;")]
        [InlineData("INSERT INTO products (name, price) VALUES ('Tea', 450), ('Milk', 390);")]
        [InlineData("INSERT INTO products VALUES (NULL, 'Tea', 450);")]
        [InlineData("INSERT INTO products SET name = 'Tea', price = 2;")]
        [InlineData("INSERT INTO products (name, price) SELECT name, price FROM products;")]
        [InlineData("INSERT INTO products (id, name, price) VALUES (1, 'Tea', 4) ON DUPLICATE KEY UPDATE price = VALUES(price);")]
        [InlineData("INSERT INTO products (name, price) VALUES ('Tea', NOW() + INTERVAL 1 DAY);")]
        [InlineData("CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(60) NOT NULL, price DECIMAL(10,2), made DATE, note TEXT);")]
        [InlineData("CREATE TABLE IF NOT EXISTS t2 (id INT, PRIMARY KEY (id), KEY k (id)) ENGINE=InnoDB AUTO_INCREMENT=5;")]
        [InlineData("CREATE TABLE t3 LIKE products;")]
        [InlineData("CREATE TABLE t4 AS SELECT * FROM products;")]
        [InlineData("CREATE TABLE t5 (id SERIAL, flag BOOLEAN, kind ENUM('a','b'), data JSON, at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);")]
        [InlineData("CREATE DATABASE IF NOT EXISTS shop3;")]
        [InlineData("CREATE OR REPLACE VIEW v AS SELECT name FROM products;")]
        [InlineData("CREATE INDEX ix ON products (name);")]
        [InlineData("CREATE UNIQUE INDEX ux ON products (name);")]
        [InlineData("CREATE USER 'app'@'localhost' IDENTIFIED BY 'pw';")]
        [InlineData("DROP TABLE IF EXISTS t;")]
        [InlineData("DROP DATABASE IF EXISTS shop3;")]
        [InlineData("DROP TEMPORARY TABLE IF EXISTS t;")]
        [InlineData("DROP INDEX ix ON products;")]
        [InlineData("DROP USER IF EXISTS 'app'@'localhost';")]
        [InlineData("ALTER TABLE products ADD COLUMN stock INT DEFAULT 0, MODIFY name VARCHAR(80);")]
        [InlineData("ALTER TABLE products DROP COLUMN price;")]
        [InlineData("ALTER USER 'root'@'localhost' IDENTIFIED BY 'x';")]
        [InlineData("ALTER IGNORE TABLE products ADD UNIQUE (name);")]
        [InlineData("USE college;")]
        [InlineData("SHOW TABLES;")]
        [InlineData("SELECT * FROM products WHERE price >= 100 AND price <= 500;")]
        [InlineData("SELECT * FROM products WHERE price > -1;")]
        [InlineData("SELECT price - 1 AS cheaper FROM products;")]
        // complex but correct: CTEs, subqueries, JOIN deletes and updates, INSERT ... SELECT
        [InlineData("WITH cheap AS (SELECT id, name FROM products WHERE price < 300) SELECT c.name, o.qty FROM cheap c JOIN orders o ON o.product_id = c.id;")]
        [InlineData("WITH RECURSIVE n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 5) SELECT i FROM n;")]
        [InlineData("WITH t AS (SELECT product_id FROM orders) DELETE FROM products WHERE id IN (SELECT product_id FROM t);")]
        [InlineData("SELECT name, (SELECT COUNT(*) FROM orders o WHERE o.product_id = p.id) AS times FROM products p;")]
        [InlineData("SELECT * FROM products p WHERE EXISTS (SELECT 1 FROM orders o WHERE o.product_id = p.id AND o.qty > 1);")]
        [InlineData("SELECT x.name FROM (SELECT name, price FROM products WHERE price > 1) AS x ORDER BY x.price;")]
        [InlineData("SELECT p.name, SUM(o.qty) AS sold FROM products p LEFT JOIN orders o ON o.product_id = p.id GROUP BY p.name HAVING SUM(o.qty) > 2 ORDER BY sold DESC LIMIT 3;")]
        [InlineData("DELETE p, o FROM products p JOIN orders o ON o.product_id = p.id WHERE p.price = 0;")]
        [InlineData("DELETE FROM orders WHERE product_id IN (SELECT id FROM products WHERE price > 1000);")]
        [InlineData("DELETE orders FROM orders LEFT JOIN products ON products.id = orders.product_id WHERE products.id IS NULL;")]
        [InlineData("UPDATE products p JOIN orders o ON o.product_id = p.id SET p.price = p.price + 1, o.qty = o.qty - 1 WHERE o.id = 2;")]
        [InlineData("UPDATE products SET price = (SELECT MAX(qty) FROM orders) WHERE id = 1;")]
        [InlineData("UPDATE products SET price = CASE WHEN price > 100 AND name = 'Tea' THEN price * 0.9 ELSE price END;")]
        [InlineData("INSERT INTO orders (product_id, qty) SELECT id, 1 FROM products WHERE price < 300;")]
        [InlineData("INSERT INTO orders (product_id, qty) SELECT p.id, COUNT(*) FROM products p JOIN orders o ON o.product_id = p.id GROUP BY p.id;")]
        [InlineData("INSERT INTO orders (product_id, qty) VALUES ((SELECT id FROM products WHERE name = 'Tea'), 2);")]
        [InlineData("REPLACE INTO products (id, name, price) VALUES (1, 'Tea', 460);")]
        [InlineData("SELECT * FROM products WHERE name IN ('Tea', 'Milk') AND price NOT BETWEEN 1 AND 2 OR price IS NULL;")]
        [InlineData("SELECT name FROM products WHERE id = 1 UNION ALL SELECT name FROM products WHERE id = 2 ORDER BY name LIMIT 1;")]
        [InlineData("SELECT DISTINCT name FROM products;")]
        [InlineData("SELECT COUNT(DISTINCT name) FROM products;")]
        [InlineData("CREATE TABLE orders2 (id INT AUTO_INCREMENT PRIMARY KEY, product_id INT NOT NULL, qty INT DEFAULT 1, FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE CASCADE);")]
        [InlineData("CREATE VIEW v2 AS SELECT p.name, o.qty FROM products p JOIN orders o ON o.product_id = p.id;")]
        public void Correct_sql_gets_no_complaint(string sql) => Clean(sql);

        [Fact]
        public void Real_scripts_get_no_new_complaints()
        {
            string root = null;
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "ParityTests", "database", "scripts"))) { root = d.FullName; break; }
            var files = new List<string>();
            if (root != null)
            {
                var s = Path.Combine(root, "ParityTests", "database", "scripts");
                files.AddRange(new[] { "A_everyday.sql", "B_advanced.sql", "F_gui.sql", "D_phpmyadmin_export.sql", "D_verify.sql", "V03_unicode_default.sql" }
                    .Select(f => Path.Combine(s, f)).Where(File.Exists));
                files.AddRange(Directory.GetFiles(Path.Combine(s, "rewritten"), "118_*.sql"));
                files.AddRange(Directory.GetFiles(Path.Combine(root, "LuminaDB", "src", "LuminaDB.Core", "Projects", "Templates"), "*.sql", SearchOption.AllDirectories));
            }
            if (files.Count == 0) { _out.WriteLine("ParityTests folder not found: skipped"); return; }
            var shapeCodes = new HashSet<string> { "cut", "shape", "from", "set", "set-and", "values", "text-quote", "object", "type", "empty", "order", "dangling", "by", "equals", "delete-column" };
            var found = new List<string>();
            foreach (var f in files)
            {
                var sql = File.ReadAllText(f);
                foreach (var i in new SqlGuard(SchemaSnapshot.Empty).Analyze(sql, "test").Issues.Where(x => shapeCodes.Contains(x.Code)))
                    found.Add(Path.GetFileName(f) + " line " + SqlStatement.LineOf(sql, i.Start) + ": " + i + "  «" + sql.Substring(i.Start, Math.Min(60, sql.Length - i.Start)).Replace("\n", " ") + "»");
            }
            _out.WriteLine(files.Count + " files checked");
            Assert.True(found.Count == 0, string.Join("\n", found));
        }
    }
}
