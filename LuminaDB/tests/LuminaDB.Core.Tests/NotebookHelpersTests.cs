using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using LuminaDB.Core.Import;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Sql;
using Xunit;

namespace LuminaDB.Core.Tests
{
    /// <summary>A command that needs lines above it which haven't run (the Run vs Run all trap).</summary>
    public class ScriptNeedTests
    {
        private static SchemaSnapshot College()
        {
            var s = new SchemaSnapshot();
            s.AddTable("college", "students", "id int", "name varchar(100)");
            s.AddDatabase("shop");
            return s;
        }

        private static SqlAnalysis At(string sql, string marker, string db = "college") =>
            new SqlGuard(College()).Analyze(sql, db, sql.IndexOf(marker, StringComparison.Ordinal) + 1);

        [Fact]
        public void Insert_into_a_table_created_above_needs_the_create_first()
        {
            const string sql = "CREATE TABLE tbl_student (id INT PRIMARY KEY, name VARCHAR(50));\nALTER TABLE tbl_student ADD age INT;\nINSERT INTO tbl_student (id, name, age) VALUES (1, 'Nimal', 20);";
            var a = At(sql, "INSERT");
            Assert.DoesNotContain(a.Issues, i => i.Severity == IssueSeverity.Bad);   // the script itself is fine
            var need = a.CurrentNeed;
            Assert.NotNull(need);
            Assert.Equal(new[] { "tbl_student" }, need.Tables);
            Assert.Equal(0, need.FromOffset);
            Assert.Null(need.UseDatabase);
            Assert.Equal("tbl_student is created above but not run yet.", need.Message);

            // the ALTER needs it too; the CREATE itself stands alone
            Assert.NotNull(At(sql, "ALTER").CurrentNeed);
            Assert.Null(At(sql, "CREATE").CurrentNeed);
            Assert.Same(need, a.NeedAt(sql.IndexOf("VALUES", StringComparison.Ordinal)));
        }

        [Fact]
        public void A_table_the_server_already_has_needs_nothing()
        {
            Assert.Null(At("INSERT INTO students (id, name) VALUES (1, 'Kamala');", "INSERT").CurrentNeed);
            // made again above (DROP + CREATE), but the server has it: running alone still works
            Assert.Null(At("DROP TABLE IF EXISTS students;\nCREATE TABLE students (id INT);\nSELECT * FROM students;", "SELECT").CurrentNeed);
        }

        [Fact]
        public void Foreign_key_to_a_table_created_above_is_a_need()
        {
            const string sql = "CREATE TABLE dept (id INT PRIMARY KEY);\nCREATE TABLE staff (id INT, dept_id INT, FOREIGN KEY (dept_id) REFERENCES dept(id));";
            var need = At(sql, "CREATE TABLE staff").CurrentNeed;
            Assert.NotNull(need);
            Assert.Equal(new[] { "dept" }, need.Tables);
        }

        [Fact]
        public void New_database_and_use_above_run_from_the_create_database()
        {
            const string sql = "CREATE DATABASE school;\nUSE school;\nCREATE TABLE t (id INT);\nINSERT INTO t VALUES (1);";
            var need = At(sql, "INSERT").CurrentNeed;
            Assert.NotNull(need);
            Assert.Equal(0, need.FromOffset);
            Assert.Equal("school", need.Database);
            Assert.Equal("school", need.UseDatabase);
            Assert.Contains("so alone this runs in college", need.Message);

            var use = At(sql, "USE").CurrentNeed;
            Assert.NotNull(use);
            Assert.Equal("school", use.Database);
        }

        [Fact]
        public void Use_above_only_matters_when_the_notebook_is_somewhere_else()
        {
            const string sql = "USE shop;\nCREATE TABLE orders (id INT);";
            var need = At(sql, "CREATE", db: "college").CurrentNeed;
            Assert.NotNull(need);
            Assert.Equal("shop", need.UseDatabase);
            Assert.Equal(0, need.FromOffset);
            Assert.Null(At(sql, "CREATE", db: "shop").CurrentNeed);   // USE already ran
            Assert.Null(At("USE college;\nSELECT * FROM students;", "SELECT", db: "college").CurrentNeed);
        }

        [Fact]
        public void Without_a_loaded_schema_nothing_is_claimed()
        {
            var a = new SqlGuard(SchemaSnapshot.Empty).Analyze("CREATE TABLE t (id INT);\nINSERT INTO t VALUES (1);", "x", 30);
            Assert.Null(a.CurrentNeed);
        }
    }

    public class ResultExportTests
    {
        private static string Temp(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "LuminaRX_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>A JOIN result as the Notebook holds it: display strings, NULL as DBNull, two "id" columns.</summary>
        private static ResultTable Joined()
        {
            var t = new ResultTable { Rows = new DataTable() };
            void Col(string name, string type, bool numeric)
            {
                var c = new ResultColumn { Name = name, TypeName = type, IsNumeric = numeric, Key = "c" + t.Columns.Count };
                t.Columns.Add(c);
                t.Rows.Columns.Add(c.Key, typeof(string));
            }
            Col("id", "INT", true);
            Col("name", "VARCHAR", false);
            Col("id", "INT", true);
            Col("fee", "DECIMAL", true);
            Col("phone", "VARCHAR", false);
            t.Rows.Rows.Add("1", "නිමල්, \"Nimal\"", "10", "1500.50", "0771234567");
            t.Rows.Rows.Add("2", "தமிழ்", "11", DBNull.Value, DBNull.Value);
            t.Rows.Rows.Add("3", " spaced ", "12", "12345678901234567890", "x");
            return t;
        }

        [Fact]
        public void Csv_has_a_bom_quotes_where_needed_and_empty_nulls()
        {
            var file = Temp("r.csv");
            Assert.Equal(3, ResultExport.Write(Joined(), ExportFormat.Csv, file));
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            var lines = File.ReadAllText(file, Encoding.UTF8).Split(new[] { "\r\n" }, StringSplitOptions.None);
            Assert.Equal("id,name,id,fee,phone", lines[0]);
            Assert.Equal("1,\"නිමල්, \"\"Nimal\"\"\",10,1500.50,0771234567", lines[1]);
            Assert.Equal("2,தமிழ்,11,,", lines[2]);
            Assert.Equal("3,\" spaced \",12,12345678901234567890,x", lines[3]);
            Assert.False(File.Exists(file + ".part"));
        }

        [Fact]
        public void Json_renames_repeated_columns_and_keeps_numbers_as_numbers()
        {
            var file = Temp("r.json");
            ResultExport.Write(Joined(), ExportFormat.Json, file);
            var text = File.ReadAllText(file, Encoding.UTF8);
            Assert.Contains("{\"id\": 1, \"name\": \"නිමල්, \\\"Nimal\\\"\", \"id_2\": 10, \"fee\": 1500.50, \"phone\": \"0771234567\"}", text);
            Assert.Contains("\"fee\": null, \"phone\": null", text);
            Assert.Contains("\"fee\": \"12345678901234567890\"", text);   // too many digits for a number: kept exact as text
            Assert.Equal(new[] { "id", "id_2", "x", "id_3" }, ResultExport.UniqueNames(new[] { "id", "id", "x", "id" }));
        }

        [Fact]
        public void Excel_reads_back_with_the_same_cells()
        {
            var file = Temp("r.xlsx");
            ResultExport.Write(Joined(), ExportFormat.Excel, file);
            var rows = XlsxReader.Rows(file).ToList();
            Assert.Equal(new[] { "id", "name", "id", "fee", "phone" }, rows[0]);
            Assert.Equal("නිමල්, \"Nimal\"", rows[1][1]);
            Assert.Equal("1500.50", rows[1][3]);   // a number cell
            Assert.Equal("0771234567", rows[1][4]);   // a phone number keeps its leading 0 (a text column)
            Assert.Equal("தமிழ்", rows[2][1]);
            Assert.Equal(4, rows.Count);
        }

        [Fact]
        public void File_names_come_from_the_table_and_the_time()
        {
            var when = new DateTime(2026, 11, 25, 14, 30, 0);
            Assert.Equal("students_2026-11-25_1430.csv", ResultExport.FileName("students", ExportFormat.Csv, when));
            Assert.Equal("result_2026-11-25_1430.xlsx", ResultExport.FileName("  ", ExportFormat.Excel, when));
            Assert.Equal("a_b_2026-11-25_1430.json", ResultExport.FileName("a/b", ExportFormat.Json, when));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResultExport.Extension(ExportFormat.Sql));
        }
    }

    public class ProjectDatabaseScanTests
    {
        private static string Folder(Dictionary<string, string> files)
        {
            var dir = Path.Combine(Path.GetTempPath(), "LuminaPS_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            foreach (var f in files)
            {
                var path = Path.Combine(dir, f.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, f.Value);
            }
            return dir;
        }

        [Fact]
        public void Mysqli_connect_names_the_database_and_its_dump_comes_first()
        {
            var dir = Folder(new Dictionary<string, string>
            {
                ["index.php"] = "<?php include 'config.php'; echo 'hi';",
                ["config.php"] = "<?php\n$conn = new mysqli(\"localhost\", \"root\", \"\", \"feedback_db\");\nif ($conn->connect_error) die('x');",
                ["db/feedback_db.sql"] = "CREATE TABLE feedback (id INT);",
                ["old/backup-big.sql"] = new string('-', 5000),
                ["vendor/lib/test.sql"] = "SELECT 1;",
            });
            var scan = ProjectDatabaseScan.Scan(dir);
            Assert.Equal("feedback_db", scan.Database);
            Assert.Equal("config.php", scan.FoundIn);
            Assert.Equal(new[] { Path.Combine("db", "feedback_db.sql"), Path.Combine("old", "backup-big.sql") }, scan.SqlFiles);
        }

        [Fact]
        public void Variables_pdo_define_and_env_are_followed()
        {
            Assert.Equal("shop_db", ProjectDatabaseScan.Scan(Folder(new Dictionary<string, string>
            {
                ["inc/db.php"] = "<?php\n$host = 'localhost';\n$dbname = 'shop_db';\n$link = mysqli_connect($host, 'root', '', $dbname);",
            })).Database);
            Assert.Equal("school_db", ProjectDatabaseScan.Scan(Folder(new Dictionary<string, string>
            {
                ["conn.php"] = "<?php $pdo = new PDO(\"mysql:host=localhost;dbname=school_db;charset=utf8mb4\", 'root', '');",
            })).Database);
            Assert.Equal("blog", ProjectDatabaseScan.Scan(Folder(new Dictionary<string, string>
            {
                ["settings.php"] = "<?php define('DB_NAME', 'blog'); define('DB_USER', 'root');",
            })).Database);
            Assert.Equal("laravel_app", ProjectDatabaseScan.Scan(Folder(new Dictionary<string, string>
            {
                [".env"] = "APP_NAME=x\nDB_CONNECTION=mysql\nDB_DATABASE=laravel_app\nDB_USERNAME=root",
                ["public/index.php"] = "<?php echo 1;",
            })).Database);
            Assert.Equal("lib_db", ProjectDatabaseScan.Scan(Folder(new Dictionary<string, string>
            {
                ["a.php"] = "<?php $c = mysqli_connect('localhost', 'root', '');\nmysqli_select_db($c, 'lib_db');",
            })).Database);
        }

        [Fact]
        public void Placeholders_and_folders_without_php_give_nothing()
        {
            var scan = ProjectDatabaseScan.Scan(Folder(new Dictionary<string, string>
            {
                ["wp-config.php"] = "<?php define( 'DB_NAME', 'database_name_here' );",
                ["readme.txt"] = "hello",
            }));
            Assert.Null(scan.Database);
            Assert.Empty(scan.SqlFiles);
            Assert.Null(ProjectDatabaseScan.Scan(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"))).Database);
        }

        [Fact]
        public void Call_arguments_split_outside_quotes_and_brackets()
        {
            const string php = "x(\"a,b\", foo(1, 2), 'it\\'s', $d);";
            Assert.Equal(new[] { "\"a,b\"", "foo(1, 2)", "'it\\'s'", "$d" }, ProjectDatabaseScan.Arguments(php, 2));
        }
    }
}
