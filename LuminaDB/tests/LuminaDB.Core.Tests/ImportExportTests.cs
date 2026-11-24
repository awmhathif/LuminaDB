using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LuminaDB.Core;
using LuminaDB.Core.Import;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    public class ImportRewriterTests
    {
        private static string M(string sql) => ImportRewriter.Rewrite(sql, ImportTarget.Modern).Text;
        private static string L(string sql) => ImportRewriter.Rewrite(sql, ImportTarget.Legacy).Text;

        [Fact]
        public void Statements_no_rule_touches_come_back_unchanged()
        {
            const string sql = "INSERT INTO t VALUES (1,'a'),(2,'b')";
            var r = ImportRewriter.Rewrite(sql, ImportTarget.Legacy);
            Assert.Same(sql, r.Text);
            Assert.Empty(r.Applied);
            Assert.Null(r.Skip);
        }

        [Fact]
        public void Workbench_visible_and_invisible_indexes()
        {
            const string sql = "CREATE TABLE t (a INT, UNIQUE INDEX `a_UNIQUE` (`a` ASC) VISIBLE, INDEX i (a) INVISIBLE)";
            Assert.Equal("CREATE TABLE t (a INT, UNIQUE INDEX `a_UNIQUE` (`a` ASC), INDEX i (a))", L(sql));
            Assert.Equal("CREATE TABLE t (a INT, UNIQUE INDEX `a_UNIQUE` (`a` ASC) VISIBLE, INDEX i (a) IGNORED)", M(sql));
            Assert.Equal("ALTER TABLE t ALTER INDEX i IGNORED", M("ALTER TABLE t ALTER INDEX i INVISIBLE"));
            // an invisible column is MariaDB syntax as well, and stays
            const string col = "CREATE TABLE t (a INT, secret VARCHAR(10) INVISIBLE DEFAULT 'x', KEY k (a) INVISIBLE)";
            Assert.Equal("CREATE TABLE t (a INT, secret VARCHAR(10) INVISIBLE DEFAULT 'x', KEY k (a) IGNORED)", M(col));
            Assert.Equal("CREATE TABLE t (a INT, secret VARCHAR(10) INVISIBLE DEFAULT 'x', KEY k (a))", L(col));
            Assert.Equal("ALTER TABLE t ALTER INDEX i NOT IGNORED", M("ALTER TABLE t ALTER INDEX i VISIBLE"));
        }

        [Fact]
        public void Mysql8_accounts()
        {
            Assert.Equal("CREATE USER 'app'@'localhost' IDENTIFIED BY 'App8#pass'", M("CREATE USER 'app'@'localhost' IDENTIFIED WITH caching_sha2_password BY 'App8#pass'"));
            Assert.Equal("CREATE USER u IDENTIFIED BY 'p'", L("CREATE USER u IDENTIFIED WITH mysql_native_password BY 'p'"));
            Assert.Equal("CREATE USER u IDENTIFIED BY 'p'", M("CREATE USER u IDENTIFIED BY 'p' FAILED_LOGIN_ATTEMPTS 3 PASSWORD_LOCK_TIME 1"));
            Assert.Equal("CREATE ROLE 'r'", M("CREATE ROLE 'r'@'%'"));
            Assert.Equal("DROP ROLE IF EXISTS 'r'", M("DROP ROLE IF EXISTS 'r'@'%'"));
            var skip = ImportRewriter.Rewrite("SET DEFAULT ROLE ALL TO 'u'@'%'", ImportTarget.Modern);
            Assert.NotNull(skip.Skip);
        }

        [Fact]
        public void Collations_change_only_for_the_old_engine()
        {
            const string sql = "CREATE TABLE t (n VARCHAR(9)) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci";
            Assert.Equal(sql, M(sql));
            Assert.Equal("CREATE TABLE t (n VARCHAR(9)) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci", L(sql));
            Assert.Equal("COLLATE utf8mb4_bin", L("COLLATE utf8mb4_0900_bin"));
            Assert.Equal("/*!40101 SET character_set_client = utf8 */", L("/*!40101 SET character_set_client = utf8mb3 */"));
            Assert.Equal("CREATE TABLE t (a TEXT) COLLATE=utf8mb4_unicode_520_ci", L("CREATE TABLE t (a TEXT) COLLATE=utf8mb4_uca1400_ai_ci"));
        }

        [Fact]
        public void Text_inside_strings_is_never_changed()
        {
            // the data says "->>", "VISIBLE)", "IDENTIFIED WITH caching_sha2_password BY": none of it may change
            const string sql = "INSERT INTO notes VALUES (1, 'a->>''$.x'' and (x) VISIBLE and IDENTIFIED WITH caching_sha2_password BY', \"TABLE t\")";
            Assert.Equal(sql, L(sql));
            Assert.Equal(sql, M(sql));
            Assert.Equal("SELECT JSON_UNQUOTE(JSON_EXTRACT(j, '$.name')) FROM t WHERE note = 'x->>''$.y'''",
                M("SELECT j->>'$.name' FROM t WHERE note = 'x->>''$.y'''"));
        }

        [Fact]
        public void Json_regexp_and_other_mysql8_syntax()
        {
            Assert.Equal("SELECT JSON_EXTRACT(t.j, '$.a') FROM t", M("SELECT t.j->'$.a' FROM t"));
            Assert.Equal("SELECT JSON_COMPACT('[1,2]')", M("SELECT CAST('[1,2]' AS JSON)"));
            Assert.Equal("SELECT * FROM s WHERE (name REGEXP '^N')", M("SELECT * FROM s WHERE REGEXP_LIKE(name, '^N')"));
            Assert.Equal("SELECT * FROM m3", M("TABLE m3"));
            Assert.Equal("INSERT INTO t VALUES (1,2), (3,4)", M("INSERT INTO t VALUES ROW(1,2), ROW(3,4)"));
            Assert.Equal("SET GLOBAL max_connections = 151", M("SET PERSIST max_connections = 151"));
            Assert.Equal("ANALYZE FORMAT=JSON SELECT 1", M("EXPLAIN ANALYZE SELECT 1"));
            Assert.Equal("CREATE TABLE g (p POINT REF_SYSTEM_ID=4326 NOT NULL)", M("CREATE TABLE g (p POINT NOT NULL SRID 4326)"));
            Assert.Equal("CREATE TABLE c (a INT)", M("CREATE TABLE c (a INT, CHECK (a > 0) NOT ENFORCED)"));
            Assert.Equal("CREATE TABLE c (a INT, CHECK (a > 0))", M("CREATE TABLE c (a INT, CHECK (a > 0) ENFORCED)"));
            Assert.Equal("INSERT INTO t (a, b) VALUES (1, 2) ON DUPLICATE KEY UPDATE b = VALUES(b)",
                M("INSERT INTO t (a, b) VALUES (1, 2) AS new ON DUPLICATE KEY UPDATE b = new.b"));
        }

        [Fact]
        public void Dump_headers_lose_mysql8_and_mariadb11_only_parts()
        {
            var r = M("CREATE DATABASE x /*!40100 DEFAULT CHARACTER SET utf8mb4 */ /*!80016 DEFAULT ENCRYPTION='N' */");
            Assert.DoesNotContain("80016", r);
            Assert.Contains("/*!40100 DEFAULT CHARACTER SET utf8mb4 */", r);
            Assert.Equal("SELECT 1", L("/*M!999999\\- enable the sandbox mode */ SELECT 1"));
        }

        [Fact]
        public void Unsupported_features_are_explained()
        {
            Assert.Contains("LATERAL", ImportRewriter.ExplainUnsupported("SELECT * FROM d, LATERAL (SELECT 1) x"));
            Assert.Contains("ROLLUP", ImportRewriter.ExplainUnsupported("SELECT a, SUM(b) FROM t GROUP BY a WITH ROLLUP ORDER BY a"));
            Assert.Null(ImportRewriter.ExplainUnsupported("SELECT 1"));
        }

        [Fact]
        public void Sources_are_recognised()
        {
            Assert.Equal(SqlSource.PhpMyAdmin, ImportRewriter.Detect("-- phpMyAdmin SQL Dump\n-- version 5.2.1"));
            Assert.Equal(SqlSource.Workbench, ImportRewriter.Detect("-- MySQL Workbench Forward Engineering\n"));
            Assert.Equal(SqlSource.MariaDbDump, ImportRewriter.Detect("/*M!999999\\- enable the sandbox mode */ \n-- MariaDB dump 10.19-11.8.10-MariaDB, for Win64 (AMD64)"));
        }
    }

    public class SqlStreamSplitterTests
    {
        private static List<ScriptStatement> Split(string script) => Split(new StringReader(script));

        private static List<ScriptStatement> Split(TextReader r)
        {
            var s = new SqlStreamSplitter(r);
            var list = new List<ScriptStatement>();
            ScriptStatement st;
            while ((st = s.Next()) != null) list.Add(st);
            return list;
        }

        [Fact]
        public void Splits_on_semicolons_outside_strings_and_comments()
        {
            var list = Split("-- intro; not a command\nSELECT 'a;b';\n/* c; */ SELECT \"x;\" ;# done;\nSELECT `we;rd`");
            Assert.Equal(new[] { "SELECT 'a;b'", "SELECT \"x;\"", "SELECT `we;rd`" }, list.Select(s => s.Text));
            Assert.Equal(new[] { 2, 3, 4 }, list.Select(s => s.Line));
            Assert.False(list[2].Terminated);
        }

        [Fact]
        public void Delimiter_blocks_for_procedures_and_triggers()
        {
            var list = Split("DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END$$\nDELIMITER ;\nCALL p();\n");
            Assert.Equal(new[] { "CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END", "CALL p()" }, list.Select(s => s.Text));
            Assert.Equal("$$", list[0].Delimiter);
        }

        [Fact]
        public void Version_comments_count_and_backslash_quotes_stay_inside_strings()
        {
            var list = Split("/*!40101 SET NAMES utf8mb4 */;\nINSERT INTO t VALUES ('it\\'s; fine');\n");
            Assert.Equal(new[] { "/*!40101 SET NAMES utf8mb4 */", "INSERT INTO t VALUES ('it\\'s; fine')" }, list.Select(s => s.Text));
        }

        /// <summary>A reader that hands out a few characters at a time, so every chunk boundary is tried.</summary>
        private sealed class Trickle : TextReader
        {
            private readonly string _s;
            private readonly int _step;
            private int _i;
            public Trickle(string s, int step) { _s = s; _step = step; }
            public override int Read(char[] buffer, int index, int count)
            {
                int n = Math.Min(Math.Min(count, _step), _s.Length - _i);
                _s.CopyTo(_i, buffer, index, n);
                _i += n;
                return n;
            }
        }

        [Fact]
        public void Same_result_whatever_the_chunk_size()
        {
            var script = "SET x = 'a;''b';\n-- c\nDELIMITER //\nCREATE TRIGGER t BEFORE INSERT ON a FOR EACH ROW BEGIN SET NEW.x = 'q//'; END//\nDELIMITER ;\n/*!80016 x */;\nSELECT \"\\\";\" ; SELECT 3";
            var expected = Split(script).Select(s => s.Text + "|" + s.Line).ToList();
            Assert.Equal(5, expected.Count);
            for (int step = 1; step < 12; step++)
                Assert.Equal(expected, Split(new Trickle(script, step)).Select(s => s.Text + "|" + s.Line).ToList());
        }

        [Fact]
        public void A_big_script_streams_through()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 30000; i++) sb.Append("INSERT INTO t VALUES (").Append(i).Append(", 'row ").Append(i).Append(" with a ; inside');\n");
            var list = Split(sb.ToString());
            Assert.Equal(30000, list.Count);
            Assert.Equal(30000, list[29999].Line);
            Assert.EndsWith("'row 29999 with a ; inside')", list[29999].Text);
        }
    }

    public class CsvAndExcelTests
    {
        private static string Temp(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "LuminaIE_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        [Fact]
        public void Csv_reader_handles_quotes_commas_and_line_breaks()
        {
            var r = new CsvReader(new StringReader("a,b,c\r\n1,\"x, y\",\"say \"\"hi\"\"\"\n2,\"two\nlines\",\n"), ',');
            Assert.Equal(new[] { "a", "b", "c" }, r.Next());
            Assert.Equal(new[] { "1", "x, y", "say \"hi\"" }, r.Next());
            Assert.Equal(3, r.Line);
            Assert.Equal(new[] { "2", "two\nlines", "" }, r.Next());
            Assert.Null(r.Next());
        }

        [Fact]
        public void Csv_writer_quotes_only_what_needs_it_and_round_trips()
        {
            var w = new StringWriter();
            DataExporter.WriteCsv(w, new[] { "id", "name", "note" }, new[]
            {
                new object[] { 1, "Nimal, Perera", null },
                new object[] { 2, "ශාන්ති", "said \"ok\"\nthen left" },
                new object[] { 3, new DateTime(2025, 1, 10), new byte[] { 0xCA, 0xFE } },
            });
            var text = w.ToString();
            Assert.StartsWith("id,name,note\r\n1,\"Nimal, Perera\",\r\n", text);
            Assert.Contains("3,2025-01-10,0xCAFE", text);
            var r = new CsvReader(new StringReader(text), ',');
            r.Next();
            r.Next();
            Assert.Equal(new[] { "2", "ශාන්ති", "said \"ok\"\nthen left" }, r.Next());
        }

        [Fact]
        public void Json_writer_keeps_types()
        {
            var w = new StringWriter();
            DataExporter.WriteJson(w, new[] { "id", "price", "name", "gone" }, new[] { new object[] { 7L, 12.50m, "a \"b\"\n", null } });
            Assert.Equal("[\n  {\"id\": 7, \"price\": 12.50, \"name\": \"a \\\"b\\\"\\n\", \"gone\": null}\n]\n", w.ToString());
            var empty = new StringWriter();
            DataExporter.WriteJson(empty, new[] { "id" }, new object[0][]);
            Assert.Equal("[]\n", empty.ToString());
        }

        [Fact]
        public void Type_guesses()
        {
            var g = new TypeGuesser();
            Assert.Equal("INT", g.Guess(new[] { "1", "-20", "", "300" }, DateOrder.DayFirst));
            Assert.Equal("BIGINT", g.Guess(new[] { "1", "9000000000" }, DateOrder.DayFirst));
            Assert.StartsWith("VARCHAR", g.Guess(new[] { "0771234567", "0112345678" }, DateOrder.DayFirst));   // phone numbers keep their 0
            Assert.Equal("DECIMAL(10,2)", g.Guess(new[] { "12.50", "1,250.75", "3" }, DateOrder.DayFirst));
            Assert.Equal("DOUBLE", g.Guess(new[] { "1.5e10", "2" }, DateOrder.DayFirst));
            Assert.Equal("DATE", g.Guess(new[] { "2025-01-10", "25/12/2024", "1/2/2024" }, DateOrder.DayFirst));
            Assert.Equal("DATETIME", g.Guess(new[] { "2025-01-10 08:30:00", "2025-01-11" }, DateOrder.DayFirst));
            Assert.Equal("TIME", g.Guess(new[] { "08:30", "17:05:10" }, DateOrder.DayFirst));
            Assert.Equal("VARCHAR(50)", g.Guess(new[] { "Nimal", "Kamala Silva" }, DateOrder.DayFirst));
            Assert.Equal("TEXT", g.Guess(new[] { new string('x', 1500) }, DateOrder.DayFirst));
            Assert.Equal("VARCHAR(255)", g.Guess(new[] { "", "NULL" }, DateOrder.DayFirst));
        }

        [Fact]
        public void Dates_are_day_first_unless_the_data_proves_otherwise()
        {
            var g = new TypeGuesser();
            Assert.Equal(DateOrder.DayFirst, g.DateOrderFor(new[] { new[] { "1/2/2024" }, new[] { "3/4/2024" } }));
            Assert.Equal(DateOrder.DayFirst, g.DateOrderFor(new[] { new[] { "25/12/2024" } }));
            Assert.Equal(DateOrder.MonthFirst, g.DateOrderFor(new[] { new[] { "12/25/2024" }, new[] { "1/2/2024" } }));
            Assert.Equal("2024-02-01", TableImporter.Convert("1/2/2024", "date", DateOrder.DayFirst));
            Assert.Equal("2024-01-02", TableImporter.Convert("1/2/2024", "date", DateOrder.MonthFirst));
            Assert.Equal("2024-02-01 08:30:00", TableImporter.Convert("1/2/2024 8:30", "datetime", DateOrder.DayFirst));
            Assert.Equal("2024-12-25", TableImporter.Convert("25 Dec 2024", "date", DateOrder.DayFirst));
        }

        [Fact]
        public void Cell_values_for_the_server()
        {
            Assert.Null(TableImporter.Convert("", "number", DateOrder.DayFirst));
            Assert.Null(TableImporter.Convert("NULL", "date", DateOrder.DayFirst));
            Assert.Equal("", TableImporter.Convert("", "text", DateOrder.DayFirst));
            Assert.Equal("1250.75", TableImporter.Convert("1,250.75", "number", DateOrder.DayFirst));
            Assert.Equal("-5", TableImporter.Convert("(5)", "number", DateOrder.DayFirst));
            Assert.Equal("1", TableImporter.Convert("Yes", "bit", DateOrder.DayFirst));
            Assert.Equal("number", TableImporter.KindOf("decimal(10,2) unsigned"));
            Assert.Equal("datetime", TableImporter.KindOf("timestamp"));
            Assert.Equal("text", TableImporter.KindOf("enum('a','b')"));
        }

        [Fact]
        public void Names_for_columns_and_tables()
        {
            Assert.Equal("first_name", TableImporter.ColumnName(" First Name ", 0));
            Assert.Equal("price_rs", TableImporter.ColumnName("Price (Rs.)", 0));
            Assert.Equal("column_3", TableImporter.ColumnName("  ", 2));
            Assert.Equal("students", TableImporter.TableName("Students_2025-01-10_0930"));
            Assert.Equal("ශිෂ්‍යයන්", TableImporter.ColumnName("ශිෂ්‍යයන්", 0).Replace("_", ""));
        }

        [Fact]
        public void Preview_finds_the_delimiter_header_and_types()
        {
            var file = Temp("marks.csv");
            File.WriteAllText(file, "Index No;Name;Marks;Exam Date;Phone\nHNDIT/01;Nimal;78.5;15/01/2025;0771234567\nHNDIT/02;\"Silva; K\";62;16/01/2025;0712345678\n", new UTF8Encoding(true));
            var p = TableImporter.Preview(file);
            Assert.Equal(';', p.Delimiter);
            Assert.True(p.HasHeader);
            Assert.Equal("marks", p.SuggestedTable);
            Assert.Equal(new[] { "index_no", "name", "marks", "exam_date", "phone" }, p.Columns.Select(c => c.Name));
            Assert.Equal("DECIMAL(10,2)", p.Columns[2].Type);
            Assert.Equal("DATE", p.Columns[3].Type);
            Assert.StartsWith("VARCHAR", p.Columns[4].Type);
            Assert.Equal(2, p.SampledRows);
            Assert.True(p.SawWholeFile);
        }

        [Fact]
        public void Windows_1252_csv_is_read_correctly()
        {
            var file = Temp("old.csv");
            File.WriteAllBytes(file, Encoding.GetEncoding(1252).GetBytes("name,price\nCafé,£5\n"));
            Assert.Equal(1252, TableImporter.DetectEncoding(file).CodePage);
            var p = TableImporter.Preview(file);
            Assert.Equal("Café", p.Columns[0].Examples[0]);
        }

        [Fact]
        public void Excel_written_by_us_reads_back()
        {
            var file = Temp("book.xlsx");
            using (var x = new XlsxWriter(file))
            {
                x.StartSheet("students/2025", new[] { "id", "name", "joined", "big" });
                x.Row(new object[] { 1, "ශාන්ති", new DateTime(2025, 1, 10), 12345678901234567890UL });
                x.Row(new object[] { 2, " padded ", null, 1.5 });
                x.StartSheet("a very long sheet name that is way past thirty-one", new[] { "x" });
                x.Row(new object[] { "\u0001bad control char" });
            }
            using (var zip = ZipFile.OpenRead(file))
                Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.Equal(new[] { "students_2025", "a very long sheet name that is " }, XlsxReader.SheetNames(file));
            var rows = XlsxReader.Rows(file).ToList();
            Assert.Equal(new[] { "id", "name", "joined", "big" }, rows[0]);
            Assert.Equal(new[] { "1", "ශාන්ති", "2025-01-10", "12345678901234567890" }, rows[1]);
            Assert.Equal(new[] { "2", " padded ", "", "1.5" }, rows[2]);
            Assert.Equal("bad control char", XlsxReader.Rows(file, "a very long sheet name that is ").ElementAt(1)[0]);

            var p = TableImporter.Preview(file);
            Assert.True(p.IsExcel);
            Assert.Equal("INT", p.Columns[0].Type);
            Assert.Equal("DATE", p.Columns[2].Type);
        }

        [Fact]
        public void Excel_from_excel_with_shared_strings_and_date_styles()
        {
            // the parts of a workbook as Excel itself writes them
            var file = Temp("excel.xlsx");
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                void Put(string name, string text) { using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text); }
                const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Put("xl/workbook.xml", "<workbook xmlns=\"" + ns + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Marks\" sheetId=\"1\" r:id=\"rId5\"/></sheets></workbook>");
                Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId5\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                Put("xl/sharedStrings.xml", "<sst xmlns=\"" + ns + "\"><si><t>Name</t></si><si><t>Date</t></si><si><r><t>Nim</t></r><r><t>al</t></r></si></sst>");
                Put("xl/styles.xml", "<styleSheet xmlns=\"" + ns + "\"><numFmts><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/><xf numFmtId=\"22\"/></cellXfs></styleSheet>");
                Put("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"" + ns + "\"><dimension ref=\"A1:C3\"/><sheetData>" +
                    "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
                    "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>2</v></c><c r=\"B3\" s=\"1\"><v>45667</v></c><c r=\"C3\" s=\"2\"><v>45667.5</v></c></row>" +
                    "</sheetData></worksheet>");
            }
            var rows = XlsxReader.Rows(file).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Empty(rows[1]);
            Assert.Equal(new[] { "Nimal", "2025-01-10", "2025-01-10 12:00:00" }, rows[2]);
            Assert.Equal(3, XlsxReader.RowCount(file));
        }

        [Fact]
        public void Compatible_sql_loses_the_mariadb11_parts()
        {
            var file = Temp("dump.sql");
            File.WriteAllText(file, "/*M!999999\\- enable the sandbox mode */ \n-- MariaDB dump\nCREATE TABLE t (a TEXT) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_ai_ci;\nINSERT INTO t VALUES ('uca1400 stays in data? no: only names change');\n");
            Assert.Equal(2, DataExporter.MakeCompatible(file));   // a data line with the bare word stays as it is
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("sandbox", text);
            Assert.Contains("COLLATE=utf8mb4_unicode_ci", text);
        }
    }

    public class ImportExportIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public ImportExportIntegrationTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Engines => MariaDbServiceIntegrationTests.Engines;

        private static string Parity(params string[] parts)
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "ParityTests", "database");
                if (Directory.Exists(p)) return Path.Combine(new[] { p }.Concat(parts).ToArray());
            }
            return null;
        }

        private static async Task Exec(MariaDbService s, string sql)
        {
            using (var c = await s.OpenConnectionAsync())
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<object> Scalar(MariaDbService s, string sql)
        {
            using (var c = await s.OpenConnectionAsync())
            using (var cmd = new MySqlCommand(sql, c)) return await cmd.ExecuteScalarAsync();
        }

        private static string Folder()
        {
            var d = Path.Combine(Path.GetTempPath(), "LuminaIE_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Workbench_phpmyadmin_and_mysql8_scripts_import_after_rewriting(string folder, EngineSetKind kind, string version)
        {
            var scripts = Parity("scripts");
            if (scripts == null) { _out.WriteLine("ParityTests folder not found: skipped"); return; }
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3446);
                await s.StartAsync();
                var target = ImportRewriter.TargetFor(kind);

                // the sample school database the C scripts query (as in the parity run)
                var setup = await SqlImporter.ImportAsync(s, new ImportOptions { File = Path.Combine(scripts, "A_everyday.sql"), Database = SqlImporter.Preview(Path.Combine(scripts, "A_everyday.sql")).NeedsDatabase ? "school" : null, Target = target });
                // it holds seven deliberate student mistakes (CHECK, duplicate, foreign key, ENUM, FULL OUTER JOIN, too long, ÷0):
                // the import carries on past each one, as the mysql client does with --force
                Assert.True(setup.Complete);
                Assert.Equal(new[] { 25, 28, 30, 32, 61, 75, 77 }, setup.Problems.Select(p => p.Line));

                // these failed before rewriting (RESULTS.md §1) and must now run with no errors
                var clean = new[] { "C/C01_workbench_forward_engineer.sql", "C/C01b_invisible_index.sql", "C/C02_caching_sha2.sql", "C/C03_native_password.sql",
                    "C/C04_utf8mb4_0900_ai_ci.sql", "C/C04b_db_0900.sql", "C/C05_default_expression.sql", "C/C08_regexp_like.sql",
                    "C/C09_invisible_column.sql", "D_phpmyadmin_export.sql" };
                foreach (var name in clean)
                {
                    var file = Path.Combine(scripts, name);
                    var preview = SqlImporter.Preview(file);
                    var r = await SqlImporter.ImportAsync(s, new ImportOptions { File = file, Database = preview.NeedsDatabase ? "school" : null, Target = target });
                    _out.WriteLine($"{version} {name}: {r.Statements} commands, {r.Rewrites.Sum(n => n.Count)} rewrites ({string.Join(" ", r.Rewrites.Select(n => n.Id).Distinct())}), {r.Problems.Count} problems");
                    foreach (var p in r.Problems) _out.WriteLine($"   line {p.Line}: #{p.Code} {p.Message}");
                    Assert.True(r.Clean, name + ": " + string.Join(" | ", r.Problems.Select(p => p.Line + " " + p.Message)));
                }

                // phpMyAdmin's dump arrived whole (D_verify's main checks)
                Assert.True(Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'library_db'")) > 0);

                // these use features with no automatic rewrite: they run as far as they can, and each failure is explained
                foreach (var name in new[] { "C/C06c_rollup_order_by.sql", "C/C07_lateral.sql" })
                {
                    var file = Path.Combine(scripts, name);
                    var r = await SqlImporter.ImportAsync(s, new ImportOptions { File = file, Database = SqlImporter.Preview(file).NeedsDatabase ? "school" : null, Target = target });
                    _out.WriteLine($"{version} {name}: {r.Problems.Count} problems: {string.Join(" | ", r.Problems.Select(p => p.Text))}");
                    Assert.True(r.Complete);
                    Assert.NotEmpty(r.Problems);
                    Assert.All(r.Problems, p => Assert.False(string.IsNullOrEmpty(p.Explanation), p.Message));
                }

                // C10 mixes everything: the rewritable parts must succeed
                {
                    var file = Path.Combine(scripts, "C", "C10_mysql8_misc.sql");
                    var r = await SqlImporter.ImportAsync(s, new ImportOptions { File = file, Database = SqlImporter.Preview(file).NeedsDatabase ? "misc" : null, Target = target });
                    _out.WriteLine($"{version} C10: {r.Statements} commands, rewrites {string.Join(",", r.Rewrites.Select(n => n.Id + "x" + n.Count))}, skipped {r.Skipped.Count}");
                    foreach (var p in r.Problems) _out.WriteLine($"   line {p.Line}: {p.Headline} → #{p.Code} {p.Text}");
                    Assert.True(r.Complete);
                    Assert.Contains(r.Rewrites, n => n.Id == "R13");
                    Assert.Contains(r.Rewrites, n => n.Id == "R16");
                    Assert.True(r.Problems.Count <= 3, "only the functional index, MEMBER OF and similar unsupported parts may fail");
                }
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task A_mariadb_11_dump_imports_into_either_engine(string folder, EngineSetKind kind, string version)
        {
            var dump = Parity("dumps", "dump_118.sql");
            if (dump == null || !File.Exists(dump)) { _out.WriteLine("dump not found: skipped"); return; }
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3456);
                await s.StartAsync();
                var preview = SqlImporter.Preview(dump);
                Assert.Equal(SqlSource.MariaDbDump, preview.Source);
                if (kind == EngineSetKind.Legacy) Assert.Contains(preview.Rewrites, n => n.Id == "R05c" || n.Id == "R05b");
                var progress = new List<double>();
                var r = await SqlImporter.ImportAsync(s, new ImportOptions { File = dump, Database = preview.NeedsDatabase ? "restored" : null, Target = ImportRewriter.TargetFor(kind) },
                    new Progress<ImportProgress>(p => progress.Add(p.Fraction)));
                _out.WriteLine($"{version}: {r.Statements} commands in {r.Duration.TotalSeconds:0.0}s, {r.Problems.Count} problems, rewrites {string.Join(",", r.Rewrites.Select(n => n.Id + "x" + n.Count))}");
                foreach (var p in r.Problems) _out.WriteLine($"   line {p.Line}: #{p.Code} {p.Message}");
                Assert.True(r.Clean);
                Assert.NotEmpty(r.Databases);
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Export_and_import_round_trips(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3466);
                await s.StartAsync();
                await Exec(s, @"CREATE DATABASE shop CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE TABLE shop.items (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(60) NOT NULL, price DECIMAL(10,2), added DATE, note TEXT, photo BLOB);
INSERT INTO shop.items (name, price, added, note, photo) VALUES
 ('සබන්', 120.50, '2025-01-10', 'line one\nline \""two\""', X'CAFE'),
 ('Rice, red', 250.00, NULL, NULL, NULL),
 ('தமிழ்', 99.99, '2024-12-31', '', NULL);
CREATE TABLE shop.orders (id INT PRIMARY KEY, item_id INT, qty INT, FOREIGN KEY (item_id) REFERENCES shop.items(id));
INSERT INTO shop.orders VALUES (1, 1, 2), (2, 3, 5);");
                string sum = Convert.ToString(await Scalar(s, "CHECKSUM TABLE shop.items"), CultureInfo.InvariantCulture);
                var dir = Folder();

                // SQL, made XAMPP-friendly, back in after a DROP
                var sql = await DataExporter.ExportAsync(s, new ExportRequest { Databases = { "shop" }, Format = ExportFormat.Sql, Flavour = SqlFlavour.Compatible, Folder = dir });
                var text = File.ReadAllText(sql.Files[0]);
                Assert.DoesNotContain("sandbox mode", text);
                Assert.DoesNotContain("uca1400", text);
                await Exec(s, "DROP DATABASE shop");
                var back = await SqlImporter.ImportAsync(s, new ImportOptions { File = sql.Files[0], Target = ImportRewriter.TargetFor(kind) });
                Assert.True(back.Clean, string.Join(" | ", back.Problems.Select(p => p.Message)));
                Assert.Equal(3L, Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM shop.items")));
                Assert.Equal("සබන්", await Scalar(s, "SELECT name FROM shop.items WHERE id = 1"));
                using (var c = await s.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("CHECKSUM TABLE shop.items", c))
                using (var rd = await cmd.ExecuteReaderAsync()) { rd.Read(); Assert.NotNull(rd.GetValue(1)); }

                // one table only
                var one = await DataExporter.ExportAsync(s, new ExportRequest { Databases = { "shop" }, Tables = { "orders" }, Format = ExportFormat.Sql, Flavour = SqlFlavour.MariaDb, Folder = dir });
                var oneText = File.ReadAllText(one.Files[0]);
                Assert.Contains("CREATE TABLE `orders`", oneText);
                Assert.DoesNotContain("CREATE TABLE `items`", oneText);

                // CSV out, then into a new table
                var csv = await DataExporter.ExportAsync(s, new ExportRequest { Databases = { "shop" }, Tables = { "items" }, Format = ExportFormat.Csv, Folder = dir });
                Assert.Equal(3, csv.Rows);
                var bytes = File.ReadAllBytes(csv.Files[0]);
                Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV has a BOM so Excel reads Sinhala");
                var p = TableImporter.Preview(csv.Files[0]);
                Assert.Equal(',', p.Delimiter);
                Assert.Equal("INT", p.Columns.Single(c => c.Name == "id").Type);
                Assert.Equal("DATE", p.Columns.Single(c => c.Name == "added").Type);
                var opts = new TableImportOptions { File = csv.Files[0], Delimiter = p.Delimiter, HasHeader = true, DateOrder = p.DateOrder, Database = "shop", Table = "items_copy", Columns = p.Columns };
                var imp = await TableImporter.ImportAsync(s, opts);
                Assert.True(imp.Clean, string.Join(" | ", imp.Problems.Select(x => x.Message)));
                Assert.Equal(3, imp.Inserted);
                Assert.False(imp.AddedId);   // the file had an id column, which became the key
                Assert.Equal("PRI", await Scalar(s, "SELECT COLUMN_KEY FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'shop' AND TABLE_NAME = 'items_copy' AND COLUMN_NAME = 'id'"));
                Assert.Equal("Rice, red", await Scalar(s, "SELECT name FROM shop.items_copy WHERE id = 2"));
                Assert.Equal("line one\nline \"two\"", await Scalar(s, "SELECT note FROM shop.items_copy WHERE id = 1"));
                Assert.Equal(new DateTime(2024, 12, 31), await Scalar(s, "SELECT added FROM shop.items_copy WHERE id = 3"));
                Assert.Equal(120.50m, await Scalar(s, "SELECT price FROM shop.items_copy WHERE id = 1"));

                // the same rows again into the existing table: every one clashes on id, and each is reported
                var again = await TableImporter.ImportAsync(s, new TableImportOptions { File = csv.Files[0], Delimiter = ',', Database = "shop", Table = "items_copy", CreateTable = false, Columns = p.Columns });
                Assert.Equal(0, again.Inserted);
                Assert.Equal(3, again.Failed);
                Assert.Equal(1062, again.Problems[0].Code);

                // Excel and JSON for the whole database
                var xlsx = await DataExporter.ExportAsync(s, new ExportRequest { Databases = { "shop" }, Format = ExportFormat.Excel, Folder = dir });
                Assert.Equal(new[] { "items", "items_copy", "orders" }, XlsxReader.SheetNames(xlsx.Files[0]));
                var sheet = XlsxReader.Rows(xlsx.Files[0], "items").ToList();
                Assert.Equal("සබන්", sheet[1][1]);
                Assert.Equal("120.50", sheet[1][2]);
                var json = await DataExporter.ExportAsync(s, new ExportRequest { Databases = { "shop" }, Format = ExportFormat.Json, Folder = dir });
                Assert.Equal(3, json.Files.Count);
                Assert.Contains("\"name\": \"தமிழ்\"", File.ReadAllText(json.Files.Single(f => Path.GetFileName(f) == "items.json")));

                // an Excel sheet into a new table with an id added
                var x2 = Path.Combine(dir, "students.xlsx");
                using (var w = new XlsxWriter(x2))
                {
                    w.StartSheet("Students", new[] { "Index No", "Name", "Joined", "GPA" });
                    w.Row(new object[] { "HNDIT/01", "Nimal", "15/01/2025", 3.45 });
                    w.Row(new object[] { "HNDIT/02", "ශාන්ති", "16/01/2025", 3.9 });
                }
                var xp = TableImporter.Preview(x2);
                var ximp = await TableImporter.ImportAsync(s, new TableImportOptions { File = x2, Sheet = xp.Sheet, Database = "college", Table = xp.SuggestedTable, DateOrder = xp.DateOrder, Columns = xp.Columns });
                Assert.True(ximp.Clean, string.Join(" | ", ximp.Problems.Select(q => q.Message)));
                Assert.True(ximp.AddedId);
                Assert.Equal(new DateTime(2025, 1, 16), await Scalar(s, "SELECT joined FROM college.students WHERE id = 2"));
                Assert.Equal("ශාන්ති", await Scalar(s, "SELECT name FROM college.students WHERE index_no = 'HNDIT/02'"));
                _out.WriteLine($"{version}: export/import round trips ok ({sum})");
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task A_bad_row_costs_that_row_only(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3476);
                await s.StartAsync();
                await Exec(s, "CREATE DATABASE lab; CREATE TABLE lab.marks (id INT AUTO_INCREMENT PRIMARY KEY, student VARCHAR(10) NOT NULL, mark TINYINT UNSIGNED)");
                var dir = Folder();
                var file = Path.Combine(dir, "marks.csv");
                var sb = new StringBuilder("Student,Mark\n");
                for (int i = 1; i <= 1200; i++) sb.Append("s").Append(i).Append(',').Append(i == 700 ? "999" : (i % 100).ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("this name is far too long,50\n");
                File.WriteAllText(file, sb.ToString());
                var p = TableImporter.Preview(file);
                var r = await TableImporter.ImportAsync(s, new TableImportOptions { File = file, Database = "lab", Table = "marks", CreateTable = false, Columns = p.Columns, BatchSize = 500 });
                _out.WriteLine($"{version}: {r.Inserted} in, {r.Failed} failed: {string.Join(" | ", r.Problems.Select(x => x.Line + " " + x.Message))}");
                Assert.Equal(1201, r.Rows);
                Assert.Equal(1199, r.Inserted);
                Assert.Equal(2, r.Failed);
                Assert.Equal(new[] { 701, 1202 }, r.Problems.Select(x => x.Line));
                Assert.Equal(1199L, Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM lab.marks")));
            }
        }
    }
}
