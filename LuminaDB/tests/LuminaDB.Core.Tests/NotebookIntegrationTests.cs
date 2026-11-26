using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>Notebook session, schema reader, explainer and Undo against real MariaDB 11.8 and 10.4.</summary>
    [Collection("RealMariaDb")]
    public class NotebookIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public NotebookIntegrationTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Engines => MariaDbServiceIntegrationTests.Engines;

        private static async Task<List<StatementResult>> RunScript(NotebookSession session, MariaDbService server, string script)
        {
            var results = new List<StatementResult>();
            foreach (var s in SqlSplitter.Split(script))
            {
                var schema = await SchemaReader.ReadAsync(server);
                results.Add(await session.ExecuteAsync(s, schema));
            }
            return results;
        }

        private static async Task<object> Scalar(MariaDbService s, string sql)
        {
            using (var c = await s.OpenConnectionAsync())
            using (var cmd = new MySqlCommand(sql, c))
                return await cmd.ExecuteScalarAsync();
        }

        private const string College = @"
CREATE DATABASE college;
USE college;
CREATE TABLE courses (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(80) NOT NULL, credits TINYINT NOT NULL DEFAULT 3);
CREATE TABLE students (
  id INT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(100) NOT NULL,
  email VARCHAR(120) UNIQUE,
  course_id INT,
  joined DATE,
  status ENUM('active','left') NOT NULL DEFAULT 'active',
  name_len INT AS (CHAR_LENGTH(name)) VIRTUAL,
  CONSTRAINT fk_course FOREIGN KEY (course_id) REFERENCES courses(id)
);
CREATE TRIGGER students_lower BEFORE INSERT ON students FOR EACH ROW SET NEW.email = LOWER(NEW.email);
INSERT INTO courses (title) VALUES ('DBMS'), ('Networks');
INSERT INTO students (name, email, course_id, joined) VALUES
  ('Nimal Perera', 'NIMAL@x.lk', 1, '2025-01-10'),
  ('Kamala Silva', 'kamala@x.lk', 2, '2025-02-01'),
  ('ශාන්ති', 'shanthi@x.lk', 1, NULL);
";

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Session_runs_scripts_keeps_state_reads_schema_and_explains_errors(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var server = rig.NewService(3436);
                await server.StartAsync();
                using (var session = new NotebookSession(server))
                {
                    var setup = await RunScript(session, server, College);
                    foreach (var r in setup) Assert.True(r.Ok, r.Statement.Text + " → " + r.ErrorMessage);
                    Assert.Equal("college", session.CurrentDatabase);

                    // schema reader sees it (and hides lumina_undo)
                    var schema = await SchemaReader.ReadAsync(server);
                    var students = schema.FindTable("college", "students");
                    Assert.NotNull(students);
                    Assert.Equal(new[] { "id", "name", "email", "course_id", "joined", "status", "name_len" }, students.Columns.Select(c => c.Name));
                    Assert.True(students.Find("name_len").IsGenerated);
                    Assert.Equal(new[] { "active", "left" }, students.Find("status").EnumValues);
                    Assert.Null(schema.Find(DatabaseCatalog.UndoSchema));
                    Assert.DoesNotContain((await DatabaseCatalog.ListAsync(server)).Select(d => d.Name), n => n == DatabaseCatalog.UndoSchema);

                    // results: values shown like the server prints them, Sinhala intact, NULL kept
                    var sel = await session.ExecuteAsync(SqlSplitter.Split("SELECT id, name, email, joined, status, name_len, NULL AS nothing, 1.50 AS price FROM students ORDER BY id")[0], schema);
                    Assert.True(sel.Ok, sel.ErrorMessage);
                    var t = Assert.Single(sel.Tables);
                    Assert.Equal(3, t.RowCount);
                    Assert.Equal("nimal@x.lk", t.Rows.Rows[0]["c2"]);            // trigger ran
                    Assert.Equal("2025-01-10", t.Rows.Rows[0]["c3"]);
                    Assert.Equal("ශාන්ති", t.Rows.Rows[2]["c1"]);
                    Assert.Equal(DBNull.Value, t.Rows.Rows[2]["c3"]);
                    Assert.Equal("1.50", t.Rows.Rows[0]["c7"]);
                    Assert.Equal("3 rows", sel.Outcome.Split('·')[0].Trim());

                    // variables survive between commands (one connection, like a console)
                    await session.ExecuteAsync(SqlSplitter.Split("SET @n = 41")[0], schema);
                    var v = await session.ExecuteAsync(SqlSplitter.Split("SELECT @n + 1")[0], schema);
                    Assert.Equal("42", v.Tables[0].Rows.Rows[0]["c0"]);

                    // errors come back explained, never thrown
                    var e1 = await session.ExecuteAsync(SqlSplitter.Split("SELECT nmae FROM students")[0], schema);
                    Assert.False(e1.Ok);
                    Assert.Equal(1054, e1.ErrorCode);
                    Assert.Contains("Did you mean name?", e1.Explanation.Body);

                    var e2 = await session.ExecuteAsync(SqlSplitter.Split("INSERT INTO students (name, course_id) VALUES ('X', 99)")[0], schema);
                    Assert.Equal(1452, e2.ErrorCode);
                    Assert.Equal("That course_id doesn't exist in courses", e2.Explanation.Title);

                    var e3 = await session.ExecuteAsync(SqlSplitter.Split("INSERT INTO students (name, email) VALUES ('Y', 'kamala@x.lk')")[0], schema);
                    Assert.Equal(1062, e3.ErrorCode);
                    Assert.Contains("Another row already has email = kamala@x.lk", e3.Explanation.Body);

                    var e4 = await session.ExecuteAsync(SqlSplitter.Split("SELECT * FROM students\nWHERE id = 1\nSELECT 2")[0], schema);
                    Assert.Equal(1064, e4.ErrorCode);
                    Assert.Equal(3, e4.Explanation.Line);

                    var e5 = await session.ExecuteAsync(SqlSplitter.Split("INSERT INTO students (name, status) VALUES ('Z', 'gone')")[0], schema);
                    Assert.True(e5.ErrorCode == 1265 || e5.ErrorCode == 1366, e5.ErrorCode + " " + e5.ErrorMessage);
                    _out.WriteLine(version + " enum error: " + e5.ErrorCode + " → " + e5.Explanation.Body);

                    // warnings belong to their own command: SELECT 1 after SELECT 1/0 (or after an error) shows none
                    var div = await session.ExecuteAsync(SqlSplitter.Split("SELECT 1/0")[0], schema);
                    Assert.Contains(div.Warnings, w => w.Code == 1365);
                    var plain = await session.ExecuteAsync(SqlSplitter.Split("SELECT 1")[0], schema);
                    Assert.Empty(plain.Warnings);
                    await session.ExecuteAsync(SqlSplitter.Split("SELECT * FROM no_such_table")[0], schema);
                    Assert.Empty((await session.ExecuteAsync(SqlSplitter.Split("SELECT 2")[0], schema)).Warnings);

                    // the session still works after errors, and survives a server restart
                    await server.RestartAsync();
                    var after = await session.ExecuteAsync(SqlSplitter.Split("SELECT COUNT(*) FROM students")[0], schema);
                    Assert.True(after.Ok, after.ErrorMessage);
                    Assert.Equal("3", after.Tables[0].Rows.Rows[0]["c0"]);
                    Assert.Equal("college", session.CurrentDatabase);

                    // row cap stops a huge result without hanging
                    session.RowCap = 1000;
                    var big = await session.ExecuteAsync(SqlSplitter.Split("SELECT seq FROM seq_1_to_500000")[0], schema);
                    if (big.Ok)
                    {
                        Assert.True(big.Tables[0].Truncated);
                        Assert.Equal(1000, big.Tables[0].RowCount);
                    }
                    else _out.WriteLine(version + ": no SEQUENCE engine (" + big.ErrorMessage + ")");
                    var still = await session.ExecuteAsync(SqlSplitter.Split("SELECT 1")[0], schema);
                    Assert.True(still.Ok, still.ErrorMessage);
                }
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Undo_puts_tables_rows_triggers_and_databases_back(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var server = rig.NewService(3446);
                await server.StartAsync();
                Assert.StartsWith(version, server.ServerVersion);
                using (var session = new NotebookSession(server))
                {
                    foreach (var r in await RunScript(session, server, College)) Assert.True(r.Ok, r.ErrorMessage);
                    var undo = session.Undo;

                    async Task<StatementResult> Do(string sql)
                    {
                        var res = await session.ExecuteAsync(SqlSplitter.Split(sql)[0], await SchemaReader.ReadAsync(server));
                        Assert.True(res.Ok, sql + " → " + res.ErrorMessage);
                        return res;
                    }
                    async Task UndoLatest()
                    {
                        var b = await undo.LatestAsync();
                        Assert.NotNull(b);
                        await undo.RestoreAsync(b.Id);
                    }

                    // DELETE without WHERE → back, with the same ids, generated column and trigger
                    var del = await Do("DELETE FROM students");
                    Assert.True(del.Undo.Covered, del.Undo.SkippedReason);
                    Assert.Equal(3L, del.Affected);
                    await UndoLatest();
                    Assert.Equal(3L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM college.students")));
                    Assert.Equal("ශාන්ති", await Scalar(server, "SELECT name FROM college.students WHERE id = 3"));
                    Assert.Equal(6L, Convert.ToInt64(await Scalar(server, "SELECT name_len FROM college.students WHERE id = 3")));
                    Assert.Equal(1L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_NAME = 'students_lower'")));
                    await Do("INSERT INTO students (name, email) VALUES ('After', 'UPPER@X.LK')");
                    Assert.Equal("upper@x.lk", await Scalar(server, "SELECT email FROM college.students WHERE name = 'After'"));
                    // the auto-increment counter wasn't reset to 1
                    Assert.True(Convert.ToInt64(await Scalar(server, "SELECT id FROM college.students WHERE name = 'After'")) >= 4);

                    // UPDATE → back
                    await Do("UPDATE students SET status = 'left'");
                    await UndoLatest();
                    Assert.Equal(0L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM college.students WHERE status = 'left'")));

                    // DROP TABLE (a parent with a child pointing at it) → back, foreign key intact
                    await Do("SET FOREIGN_KEY_CHECKS = 0");
                    await Do("DROP TABLE courses");
                    await Do("SET FOREIGN_KEY_CHECKS = 1");
                    await UndoLatest();
                    Assert.Equal(2L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM college.courses")));
                    var fk = await session.ExecuteAsync(SqlSplitter.Split("INSERT INTO students (name, course_id) VALUES ('Bad', 77)")[0], await SchemaReader.ReadAsync(server));
                    Assert.Equal(1452, fk.ErrorCode);

                    // ALTER ... DROP COLUMN → back with data
                    await Do("ALTER TABLE students DROP COLUMN joined");
                    await UndoLatest();
                    Assert.Equal("2025-02-01", Convert.ToDateTime(await Scalar(server, "SELECT joined FROM college.students WHERE id = 2")).ToString("yyyy-MM-dd"));

                    // CREATE TABLE → undo removes it
                    await Do("CREATE TABLE scratch (id INT)");
                    await UndoLatest();
                    Assert.Equal(0L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='college' AND TABLE_NAME='scratch'")));

                    // RENAME → back to the old name
                    await Do("RENAME TABLE courses TO modules");
                    await UndoLatest();
                    Assert.Equal(1L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='college' AND TABLE_NAME='courses'")));
                    Assert.Equal(0L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='college' AND TABLE_NAME='modules'")));

                    // DROP DATABASE → everything back
                    await Do("DROP DATABASE college");
                    Assert.Null(session.CurrentDatabase);
                    await UndoLatest();
                    Assert.Equal(4L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM college.students")));
                    Assert.Equal(2L, Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM college.courses")));

                    // a failed command leaves no undo batch behind
                    var before = (await undo.LatestAsync())?.Id;
                    await session.UseAsync("college");
                    var failed = await session.ExecuteAsync(SqlSplitter.Split("UPDATE students SET course_id = 999")[0], await SchemaReader.ReadAsync(server));
                    Assert.False(failed.Ok);
                    Assert.Equal(before, (await undo.LatestAsync())?.Id);

                    // only the last 20 are kept
                    for (int i = 0; i < 23; i++) await Do("UPDATE students SET status = 'active' WHERE id = 1");
                    var ready = Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM lumina_undo._log WHERE state = 'ready'"));
                    Assert.True(ready <= UndoService.KeepBatches, "ready batches: " + ready);
                    var copies = Convert.ToInt64(await Scalar(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='lumina_undo' AND TABLE_NAME LIKE 'b%'"));
                    Assert.True(copies <= UndoService.KeepBatches, "copy tables: " + copies);
                }
            }
        }
    }
}
