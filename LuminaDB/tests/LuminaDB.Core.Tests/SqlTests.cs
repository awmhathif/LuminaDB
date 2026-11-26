using System.Linq;
using LuminaDB.Core.Sql;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class SqlLexerAndSplitterTests
    {
        [Fact]
        public void Strings_with_doubled_and_backslash_quotes_stay_one_token()
        {
            var t = SqlLexer.Significant(SqlLexer.Tokenize("SELECT 'O''Brien', 'it\\'s', \"say \"\"hi\"\"\""));
            Assert.Equal(new[] { "'O''Brien'", "'it\\'s'", "\"say \"\"hi\"\"\"" }, t.Where(x => x.Kind == TokenKind.String).Select(x => x.Text));
            Assert.All(t, x => Assert.False(x.Unterminated));
        }

        [Fact]
        public void Dash_dash_needs_a_space_to_be_a_comment()
        {
            var a = SqlLexer.Tokenize("SELECT 5--3");
            Assert.DoesNotContain(a, x => x.Kind == TokenKind.Comment);
            var b = SqlLexer.Tokenize("SELECT 5 -- note\n, 1 # other");
            Assert.Equal(2, b.Count(x => x.Kind == TokenKind.Comment));
        }

        [Fact]
        public void Backtick_names_variables_and_literals()
        {
            var t = SqlLexer.Significant(SqlLexer.Tokenize("SELECT `my table`.`a``b`, @x, @@version, x'4F', 2026_marks, 1.5e3"));
            Assert.Equal("my table", t[1].IdentName);
            Assert.Equal("a`b", t[3].IdentName);
            Assert.Contains(t, x => x.Kind == TokenKind.Variable && x.Text == "@@version");
            Assert.Contains(t, x => x.Kind == TokenKind.String && x.Text == "x'4F'");
            Assert.Contains(t, x => x.Kind == TokenKind.Word && x.Text == "2026_marks");
            Assert.Contains(t, x => x.Kind == TokenKind.Number && x.Text == "1.5e3");
        }

        [Fact]
        public void Unterminated_string_is_flagged()
        {
            var t = SqlLexer.Tokenize("SELECT 'abc");
            Assert.True(t.Last().Unterminated);
        }

        [Fact]
        public void Splits_on_semicolons_outside_strings_and_comments()
        {
            var s = SqlSplitter.Split("SELECT 'a;b';\n-- x; y\nSELECT 2 /* ; */;  \n\n  -- only a comment\n");
            Assert.Equal(2, s.Count);
            Assert.Equal("SELECT 'a;b'", s[0].Text);
            Assert.Equal(3, s[1].StartLine);
            Assert.True(s[1].Terminated);
        }

        [Fact]
        public void Mysqldump_version_comments_are_commands()
        {
            var s = SqlSplitter.Split("/*!40101 SET NAMES utf8mb4 */;\n-- note\nSELECT 1;");
            Assert.Equal(2, s.Count);
            Assert.StartsWith("/*!40101", s[0].Text);
            Assert.Equal("SELECT 1", s[1].Text);
        }

        [Fact]
        public void Delimiter_lines_change_the_separator_and_are_not_run()
        {
            var script = "DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END$$\nDELIMITER ;\nCALL p();";
            var s = SqlSplitter.Split(script);
            Assert.Equal(2, s.Count);
            Assert.StartsWith("CREATE PROCEDURE", s[0].Text);
            Assert.EndsWith("END", s[0].Text);
            Assert.Equal("CALL p()", s[1].Text);
            Assert.Equal("$$", s[0].Delimiter);
        }

        [Fact]
        public void Last_statement_without_semicolon_is_kept_and_caret_finds_it()
        {
            var script = "SELECT 1;\nSELECT 2";
            var s = SqlSplitter.Split(script);
            Assert.False(s[1].Terminated);
            Assert.Same(s[1], SqlSplitter.StatementAt(s, script.Length));
            Assert.Same(s[0], SqlSplitter.StatementAt(s, 3));
        }
    }

    public class SqlFormatterTests
    {
        [Fact]
        public void Tidy_puts_clauses_on_lines_and_capitalises_keywords()
        {
            var r = SqlFormatter.Format("select name, count(*) from students s left join marks m on m.sid=s.id where s.age > 18 and s.city='Kandy' group by name order by name limit 5;");
            Assert.Equal(
                "SELECT name, COUNT(*)\nFROM students s\nLEFT JOIN marks m ON m.sid = s.id\nWHERE s.age > 18\n  AND s.city = 'Kandy'\nGROUP BY name\nORDER BY name\nLIMIT 5;",
                r);
        }

        [Fact]
        public void Tidy_never_changes_strings_comments_or_function_brackets()
        {
            var r = SqlFormatter.Format("select 'from where'  , now() -- keep me\nfrom t");
            Assert.Contains("'from where'", r);
            Assert.Contains("NOW()", r);
            Assert.Contains("-- keep me\n", r);
        }

        [Fact]
        public void Tidy_keeps_procedures_and_delimiters_as_written()
        {
            var script = "DELIMITER //\ncreate procedure p()\nbegin\n  select 1;\nend//\nDELIMITER ;";
            var r = SqlFormatter.Format(script);
            Assert.StartsWith("DELIMITER //\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\nEND//", r);
        }

        [Fact]
        public void Tidy_does_not_break_extract_from_inside_functions()
        {
            var r = SqlFormatter.Format("select extract(year from d) from t");
            Assert.Equal("SELECT EXTRACT(YEAR FROM d)\nFROM t", r);
        }

        [Fact]
        public void Tidy_indents_sub_queries()
        {
            var r = SqlFormatter.Format("select * from a where id in (select aid from b where x = 1)");
            Assert.Equal("SELECT *\nFROM a\nWHERE id IN (SELECT aid\n    FROM b\n    WHERE x = 1)", r);
        }
    }

    public class SqlGuardTests
    {
        private static SchemaSnapshot College()
        {
            var s = new SchemaSnapshot();
            var st = s.AddTable("college", "students", "id int", "name varchar(100)", "email varchar(120)", "course_id int", "age int");
            st.Columns[0].Key = "PRI";
            st.RowEstimate = 1204;
            s.AddTable("college", "courses", "id int", "title varchar(80)", "credits int");
            s.AddTable("college", "order", "id int", "total decimal(10,2)");
            s.AddDatabase("shop");
            return s;
        }

        private static SqlAnalysis Run(string sql, string db = "college", int caret = -1) => new SqlGuard(College()).Analyze(sql, db, caret);

        [Fact]
        public void Clean_query_has_no_issues_and_a_plain_summary()
        {
            var a = Run("SELECT s.name, c.title FROM students s JOIN courses c ON c.id = s.course_id WHERE s.age > 18 ORDER BY s.name;", caret: 3);
            Assert.Empty(a.Issues);
            Assert.Equal("Reads rows from students, joins courses, keeps the matching ones and sorts them. Read-only.", a.Summary);
        }

        [Fact]
        public void Misspelled_command_suggests_the_right_one()
        {
            var a = Run("SELEC * FROM students;");
            var i = Assert.Single(a.Issues);
            Assert.Equal("command", i.Code);
            Assert.Contains("Did you mean SELECT?", i.Message);
            Assert.Equal("SELECT * FROM students;", TextFix.Apply("SELEC * FROM students;", i.Fixes));
        }

        [Fact]
        public void Word_still_being_typed_is_not_flagged()
        {
            Assert.Empty(Run("SELE", caret: 4).Issues);
        }

        [Fact]
        public void Missing_semicolon_between_lines_is_found_and_fixed()
        {
            var sql = "SELECT * FROM students\nSELECT * FROM courses;";
            var a = Run(sql);
            var i = Assert.Single(a.Issues);
            Assert.Equal("semicolon", i.Code);
            Assert.Contains("line 1", i.Message);
            Assert.Equal("SELECT * FROM students;\nSELECT * FROM courses;", TextFix.Apply(sql, i.Fixes));
            Assert.Equal(2, a.Statements.Count);
        }

        [Theory]
        [InlineData("INSERT INTO students (name)\nSELECT title FROM courses;")]
        [InlineData("CREATE TABLE t2\nSELECT * FROM students;")]
        [InlineData("SELECT * FROM students\nUNION\nSELECT * FROM students;")]
        [InlineData("ALTER TABLE students\nDROP COLUMN age;")]
        [InlineData("CREATE TABLE x (id INT,\n  sid INT REFERENCES students(id)\n  ON DELETE CASCADE\n  ON UPDATE CASCADE);")]
        public void Multi_line_commands_are_not_mistaken_for_missing_semicolons(string sql)
        {
            Assert.DoesNotContain(Run(sql).Issues, i => i.Code == "semicolon");
        }

        [Fact]
        public void Unknown_table_suggests_the_closest()
        {
            var sql = "SELECT * FROM student;";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal("table", i.Code);
            Assert.Contains("Did you mean students?", i.Message);
            Assert.Equal("SELECT * FROM students;", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Tables_created_earlier_in_the_script_count()
        {
            var a = Run("CREATE TABLE marks (id INT PRIMARY KEY, sid INT, score INT);\nINSERT INTO marks (id, sid, score) VALUES (1, 1, 90);\nSELECT score FROM marks;");
            Assert.Empty(a.Issues);
        }

        [Fact]
        public void Use_switches_database_for_following_commands()
        {
            var a = Run("CREATE DATABASE lib;\nUSE lib;\nCREATE TABLE books (id INT);\nSELECT * FROM books;", db: null);
            Assert.Empty(a.Issues);
        }

        [Fact]
        public void No_database_selected_is_explained()
        {
            var i = Assert.Single(Run("SELECT * FROM students;", db: null).Issues);
            Assert.Equal("no-database", i.Code);
        }

        [Fact]
        public void Unknown_column_with_suggestion_in_single_table_query()
        {
            var sql = "SELECT nmae, email FROM students WHERE age > 20;";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal("column", i.Code);
            Assert.Contains("Did you mean name?", i.Message);
            Assert.Equal("SELECT name, email FROM students WHERE age > 20;", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Qualified_column_and_alias_mistakes()
        {
            var a = Run("SELECT s.nam, students.email FROM students s;");
            Assert.Contains(a.Issues, i => i.Code == "column" && i.Message.Contains("Did you mean name?"));
            Assert.Contains(a.Issues, i => i.Code == "alias" && i.Message.Contains("write s.email"));
        }

        [Fact]
        public void Select_aliases_are_allowed_in_order_by()
        {
            Assert.Empty(Run("SELECT age * 2 AS double_age, name n FROM students ORDER BY double_age, n;").Issues);
        }

        [Fact]
        public void Insert_column_count_must_match_values()
        {
            var a = Run("INSERT INTO students (name, email) VALUES ('Ann', 'a@x.lk'), ('Bob');");
            var i = Assert.Single(a.Issues);
            Assert.Equal("count", i.Code);
            Assert.StartsWith("Row 2 has 1 value, but 2 columns are listed (name, email)", i.Message);
        }

        [Fact]
        public void Insert_without_column_list_counts_all_columns()
        {
            var a = Run("INSERT INTO courses VALUES (1, 'DBMS');");
            Assert.Contains("courses has 3 columns (id, title, credits)", Assert.Single(a.Issues).Message);
        }

        [Fact]
        public void Clause_typo_is_caught()
        {
            var sql = "SELECT * FORM students;";
            var i = Assert.Single(Run(sql).Issues, x => x.Code == "typo");
            Assert.Equal("SELECT * FROM students;", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Extra_comma_before_from()
        {
            var sql = "SELECT name, email, FROM students;";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal("comma", i.Code);
            Assert.Equal("SELECT name, email FROM students;", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Unclosed_bracket_and_unclosed_quote()
        {
            Assert.Equal("bracket", Assert.Single(Run("SELECT COUNT(* FROM students;").Issues).Code);
            Assert.Equal("quote", Assert.Single(Run("SELECT * FROM students WHERE name = 'Ann;\nSELECT 1;").Issues).Code);
        }

        [Fact]
        public void Equals_null_becomes_is_null_but_set_equals_null_is_fine()
        {
            var sql = "SELECT * FROM students WHERE email = NULL;";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal("SELECT * FROM students WHERE email IS NULL;", TextFix.Apply(sql, i.Fixes));
            Assert.DoesNotContain(Run("UPDATE students SET email = NULL WHERE id = 3;").Issues, x => x.Code == "null");
        }

        [Fact]
        public void Delete_without_where_warns_with_row_estimate_and_fix()
        {
            var sql = "DELETE FROM students;";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal(IssueSeverity.Warn, i.Severity);
            Assert.Contains("about 1,204", i.Message);
            Assert.Equal("DELETE FROM students WHERE id = 1;", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Reserved_word_column_gets_backticks()
        {
            var sql = "CREATE TABLE sales (id INT, order INT, desc VARCHAR(20));";
            var a = Run(sql);
            Assert.Equal(2, a.Issues.Count(i => i.Code == "reserved"));
            var fixedSql = TextFix.Apply(sql, a.Issues.SelectMany(i => i.Fixes));
            Assert.Equal("CREATE TABLE sales (id INT, `order` INT, `desc` VARCHAR(20));", fixedSql);
        }

        [Fact]
        public void Reserved_table_name_in_from_gets_backticks_when_it_exists()
        {
            var sql = "SELECT * FROM order;";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal("SELECT * FROM `order`;", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Create_existing_table_warns_with_if_not_exists()
        {
            var sql = "CREATE TABLE students (id INT);";
            var i = Assert.Single(Run(sql).Issues);
            Assert.Equal("CREATE TABLE IF NOT EXISTS students (id INT);", TextFix.Apply(sql, i.Fixes));
        }

        [Fact]
        public void Double_quoted_table_name()
        {
            var sql = "SELECT * FROM \"students\";";
            Assert.Equal("SELECT * FROM `students`;", TextFix.Apply(sql, Run(sql).Issues.First(i => i.Code == "reserved").Fixes));
        }

        [Fact]
        public void Without_schema_name_checks_are_skipped()
        {
            Assert.Empty(new SqlGuard(SchemaSnapshot.Empty).Analyze("SELECT nmae FROM nothing_here;", "x").Issues);
        }

        [Fact]
        public void Procedures_are_not_picked_apart()
        {
            var sql = "DELIMITER $$\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1\n  ;\n  UPDATE students SET age = age + 1;\nEND$$\nDELIMITER ;";
            Assert.Empty(Run(sql).Issues);
        }

        [Fact]
        public void Statement_info_targets()
        {
            StatementInfo I(string sql) => StatementInfo.Analyze(SqlSplitter.Split(sql)[0], "college");
            Assert.Equal("students", I("UPDATE students s JOIN courses c ON c.id = s.course_id SET s.age = 1 WHERE c.id = 2").Targets[0].Name);
            Assert.Equal(2, I("UPDATE students s JOIN courses c ON c.id = s.course_id SET s.age = 1").Targets.Count);
            Assert.Equal("students", Assert.Single(I("DELETE s FROM students s JOIN courses c ON c.id = s.course_id").Targets).Name);
            Assert.Equal("marks", Assert.Single(I("INSERT INTO marks SELECT * FROM students").Targets).Name);
            Assert.Equal(new[] { "a", "b" }, I("DROP TABLE IF EXISTS a, shop.b").Targets.Select(t => t.Name));
            Assert.Equal("shop", I("DROP TABLE IF EXISTS a, shop.b").Targets[1].ResolvedDatabase);
            var r = I("RENAME TABLE students TO pupils");
            Assert.Equal("students", Assert.Single(r.Targets).Name);
            Assert.Equal("pupils", Assert.Single(r.NewNames).Name);
            Assert.True(I("ALTER TABLE students DROP COLUMN age").IsDropColumn);
            Assert.False(I("ALTER TABLE students DROP INDEX ix").IsDropColumn);
            Assert.Equal("lib", I("DROP DATABASE lib").DatabaseName);
            Assert.Equal(StatementKind.Read, I("SHOW TABLES").Kind);
            Assert.Empty(I("SELECT YEAR(NOW()) FROM DUAL").Tables);
        }
    }

    public class SafetyAndExplainerTests
    {
        private static SchemaSnapshot Shop()
        {
            var s = new SchemaSnapshot();
            var o = s.AddTable("shop", "orders", "order_id int", "customer_id int", "total decimal(10,2)", "status enum('new','paid','sent')", "note varchar(20)");
            o.Columns[0].Key = "PRI";
            o.Columns[0].Extra = "auto_increment";
            s.AddTable("shop", "customers", "id int", "name varchar(50)");
            return s;
        }

        private static StatementInfo I(string sql) => StatementInfo.Analyze(SqlSplitter.Split(sql)[0], "shop");

        [Fact]
        public void Gate_asks_for_delete_without_where_and_offers_a_where()
        {
            var p = SafetyGate.Check(I("DELETE FROM orders"), Shop(), safeUpdates: true);
            Assert.NotNull(p);
            Assert.Equal("Delete every row in orders?", p.Title);
            Assert.Equal("DELETE FROM orders WHERE order_id = 1", TextFix.Apply("DELETE FROM orders", new[] { p.AddWhere }));
            Assert.Contains("(42 rows)", SafetyGate.WithRows(p.Body, 42));
        }

        [Fact]
        public void Risky_commands_name_the_databases_to_back_up_first()
        {
            var schema = Shop();
            schema.AddTable("school", "marks", "id int");
            Assert.Equal(new[] { "shop" }, SafetyGate.DatabasesToBackUp(new[] { I("DELETE FROM orders"), I("TRUNCATE customers") }, schema));
            Assert.Equal(new[] { "school" }, SafetyGate.DatabasesToBackUp(new[] { I("DROP TABLE school.marks") }, schema));
            Assert.Equal(new[] { "school" }, SafetyGate.DatabasesToBackUp(new[] { I("DROP DATABASE school") }, schema));
            Assert.Equal(new[] { "shop" }, SafetyGate.DatabasesToBackUp(new[] { I("ALTER TABLE orders DROP COLUMN note") }, schema));
            Assert.Empty(SafetyGate.DatabasesToBackUp(new[] { I("DELETE FROM orders WHERE order_id = 3"), I("SELECT * FROM orders"), I("UPDATE orders SET note='x' LIMIT 1") }, schema));
            Assert.Empty(SafetyGate.DatabasesToBackUp(new[] { I("DROP DATABASE nothing_here") }, schema));   // not there: nothing to keep
        }

        [Fact]
        public void Gate_is_quiet_for_normal_commands_and_with_safe_updates_off()
        {
            Assert.Null(SafetyGate.Check(I("DELETE FROM orders WHERE order_id = 3"), Shop(), true));
            Assert.Null(SafetyGate.Check(I("SELECT * FROM orders"), Shop(), true));
            Assert.Null(SafetyGate.Check(I("DELETE FROM orders"), Shop(), false));
            Assert.NotNull(SafetyGate.Check(I("DROP DATABASE shop"), Shop(), false));
            Assert.NotNull(SafetyGate.Check(I("TRUNCATE orders"), Shop(), true));
            Assert.NotNull(SafetyGate.Check(I("DROP TABLE orders"), Shop(), true));
            Assert.Null(SafetyGate.Check(I("DROP TABLE IF EXISTS nothing"), Shop(), true));
            Assert.NotNull(SafetyGate.Check(I("ALTER TABLE orders DROP COLUMN note"), Shop(), true));
        }

        [Fact]
        public void Gate_treats_an_always_true_where_as_every_row()
        {
            foreach (var sql in new[] { "DELETE FROM orders WHERE 1=1", "DELETE FROM orders WHERE TRUE", "UPDATE orders SET note='x' WHERE 1",
                                        "DELETE FROM orders WHERE order_id = order_id", "DELETE FROM orders WHERE 1=1 OR order_id = 3", "DELETE FROM orders WHERE (2 > 1)" })
                Assert.True(I(sql).TouchesEveryRow, sql);
            foreach (var sql in new[] { "DELETE FROM orders WHERE order_id = 3", "DELETE FROM orders WHERE 1=1 AND order_id = 3",
                                        "DELETE FROM orders WHERE 1=0", "DELETE FROM orders WHERE 1=1 LIMIT 1" })
                Assert.False(I(sql).TouchesEveryRow, sql);

            const string q = "DELETE FROM orders WHERE 1=1";
            var p = SafetyGate.Check(I(q), Shop(), true);
            Assert.NotNull(p);
            Assert.Contains("always true", p.Body);
            Assert.Equal("DELETE FROM orders WHERE order_id = 1", TextFix.Apply(q, new[] { p.AddWhere }));
            Assert.Equal(new[] { "shop" }, SafetyGate.DatabasesToBackUp(new[] { I(q) }, Shop()));
        }

        [Fact]
        public void Value_errors_are_one_short_line_with_the_next_step()
        {
            var dt = E(1292, "Incorrect datetime value: '2026-13-01 10:00' for column `shop`.`orders`.`placed` at row 1", "INSERT INTO orders (note) VALUES ('x')");
            Assert.Equal("'2026-13-01 10:00' isn't a valid datetime", dt.Title);
            Assert.Contains("'YYYY-MM-DD HH:MM:SS'", dt.Body);
            var wide = E(1406, "Data too long for column 'note' at row 1", "INSERT INTO orders (note) VALUES ('xxxxxxxxxxxxxxxxxxxxxxxx')");
            Assert.Contains("MODIFY note VARCHAR(40)", wide.Body);
            var fn = E(1582, "Incorrect parameter count in the call to native function 'ROUND'", "SELECT ROUND(1,2,3)");
            Assert.Equal("Wrong number of values for ROUND", fn.Title);
            Assert.DoesNotContain("\n", fn.Body);
        }

        [Fact]
        public void Warnings_read_as_one_line()
        {
            Assert.Null(ErrorExplainer.DescribeWarnings(new ServerNote[0]));
            Assert.Equal("1 warning: dividing by 0 gives NULL.", ErrorExplainer.DescribeWarnings(new[] { new ServerNote { Level = "Warning", Code = 1365, Message = "Division by 0" } }));
            Assert.Equal("2 warnings. First: 'abc' isn't a number, so it was read as 0.", ErrorExplainer.DescribeWarnings(new[]
            {
                new ServerNote { Level = "Warning", Code = 1292, Message = "Truncated incorrect DOUBLE value: 'abc'" },
                new ServerNote { Level = "Warning", Code = 1365, Message = "Division by 0" },
            }));
        }

        private static ErrorExplanation E(int code, string msg, string sql) =>
            ErrorExplainer.Explain(code, msg, SqlSplitter.Split(sql)[0], Shop(), "shop");

        [Fact]
        public void Explains_unknown_table_with_fix()
        {
            var e = E(1146, "Table 'shop.order' doesn't exist", "SELECT * FROM order");
            Assert.Equal("There's no table called order", e.Title);
            Assert.Contains("Did you mean orders?", e.Body);
            Assert.Equal("SELECT * FROM orders", TextFix.Apply("SELECT * FROM order", e.Fixes));
        }

        [Fact]
        public void Explains_unknown_column_with_fix()
        {
            var sql = "SELECT totl FROM orders";
            var e = E(1054, "Unknown column 'totl' in 'field list'", sql);
            Assert.Contains("Did you mean total?", e.Body);
            Assert.Equal("SELECT total FROM orders", TextFix.Apply(sql, e.Fixes));
        }

        [Fact]
        public void Explains_duplicate_primary_key()
        {
            var e = E(1062, "Duplicate entry '7' for key 'PRIMARY'", "INSERT INTO orders (order_id) VALUES (7)");
            Assert.Equal("That order_id is already used", e.Title);
            Assert.Contains("leave order_id out", e.Body);
        }

        [Fact]
        public void Explains_foreign_key_failure_in_words()
        {
            var e = E(1452, "Cannot add or update a child row: a foreign key constraint fails (`shop`.`orders`, CONSTRAINT `fk1` FOREIGN KEY (`customer_id`) REFERENCES `customers` (`id`))",
                "INSERT INTO orders (customer_id) VALUES (99)");
            Assert.Equal("That customer_id doesn't exist in customers", e.Title);
            Assert.Contains("Add that customer first", e.Body);
        }

        [Fact]
        public void Explains_syntax_error_using_the_checker()
        {
            var sql = "SELECT * FROM orders\nSELECT * FROM customers";
            var e = E(1064, "You have an error in your SQL syntax; check the manual that corresponds to your MariaDB server version for the right syntax to use near 'SELECT * FROM customers' at line 2", sql);
            Assert.StartsWith("Most likely: Missing ;", e.Body);
            Assert.Equal(2, e.Line);
            Assert.True(e.HasFix);
        }

        [Fact]
        public void Explains_syntax_error_at_the_end()
        {
            const string nearEnd = "You have an error in your SQL syntax; check the manual that corresponds to your MariaDB server version for the right syntax to use near '' at line 1";
            // the checker knows what's missing
            Assert.Equal("Most likely: WHERE needs a condition after it, like WHERE id = 1.", E(1064, nearEnd, "SELECT * FROM orders WHERE").Body);
            // when it doesn't, the general reading of "near ''"
            Assert.Contains("ends too early", E(1064, nearEnd, "SET @x =").Body);
        }

        [Fact]
        public void Explains_function_space_and_offers_to_remove_it()
        {
            var sql = "SELECT COUNT (*) FROM orders";
            var e = E(1630, "FUNCTION shop.COUNT does not exist. Check the 'Function Name Parsing and Resolution' section in the Reference Manual", sql);
            Assert.Equal("SELECT COUNT(*) FROM orders", TextFix.Apply(sql, e.Fixes));
        }

        [Fact]
        public void Explains_enum_and_length_errors_using_the_schema()
        {
            Assert.Contains("'new', 'paid', 'sent'", E(1265, "Data truncated for column 'status' at row 1", "INSERT INTO orders (status) VALUES ('done')").Body);
            Assert.Contains("holds up to 20 characters", E(1406, "Data too long for column 'note' at row 1", "INSERT INTO orders (note) VALUES ('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx')").Body);
        }

        [Fact]
        public void Unknown_codes_fall_back_to_the_server_message()
        {
            var e = E(9999, "Something odd", "SELECT 1");
            Assert.Equal("MariaDB refused this", e.Title);
            Assert.Equal("Something odd", e.Body);
        }
    }
}
