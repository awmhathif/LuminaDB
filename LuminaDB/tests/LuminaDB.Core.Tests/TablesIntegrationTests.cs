using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>Tables against real MariaDB 11.8 and 10.4: read designs, create, alter, rows, operations, objects.</summary>
    [Collection("RealMariaDb")]
    public class TablesIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public TablesIntegrationTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Engines => MariaDbServiceIntegrationTests.Engines;

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
  note VARCHAR(20) NOT NULL DEFAULT '',
  meta JSON,
  updated TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  name_len INT AS (CHAR_LENGTH(name)) VIRTUAL,
  CONSTRAINT fk_course FOREIGN KEY (course_id) REFERENCES courses(id) ON DELETE SET NULL,
  CONSTRAINT chk_name CHECK (name <> '')
);
CREATE TRIGGER students_lower BEFORE INSERT ON students FOR EACH ROW SET NEW.email = LOWER(NEW.email);
CREATE DATABASE archive;
INSERT INTO courses (title) VALUES ('DBMS'), ('Networks');
INSERT INTO students (name, email, course_id, joined) VALUES
  ('Nimal Perera', 'NIMAL@x.lk', 1, '2025-01-10'),
  ('Kamala Silva', 'kamala@x.lk', 2, '2025-02-01'),
  ('ශාන්ති', 'shanthi@x.lk', 1, NULL);
";

        private static async Task<object> Scalar(MariaDbService s, string sql)
        {
            using (var c = await s.OpenConnectionAsync())
            using (var cmd = new MySqlCommand(sql, c))
                return await cmd.ExecuteScalarAsync();
        }

        private static async Task<long> Count(MariaDbService s, string sql) => Convert.ToInt64(await Scalar(s, sql));

        private static async Task<TableDesign> Read(MariaDbService s, string db, string table)
        {
            using (var c = await s.OpenConnectionAsync()) return await TableDesignReader.ReadAsync(c, db, table);
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Designs_read_create_alter_and_round_trip(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var server = rig.NewService(3456);
                await server.StartAsync();
                using (var session = new NotebookSession(server))
                {
                    var setup = await session.RunScriptAsync(College, await SchemaReader.ReadAsync(server), "setup");
                    Assert.True(setup.Ok, setup.Failed?.Statement.Text + " → " + setup.Failed?.ErrorMessage);

                    // ---- read ----
                    var d = await Read(server, "college", "students");
                    Assert.Equal(new[] { "id", "name", "email", "course_id", "joined", "status", "note", "meta", "updated", "name_len" }, d.Columns.Select(c => c.Name));
                    Assert.True(d.Column("id").AutoIncrement);
                    Assert.False(d.Column("name").Nullable);
                    Assert.Equal("VARCHAR(100)", d.Column("name").FullType);
                    Assert.Equal("active", d.Column("status").Default);
                    Assert.Equal("''", d.Column("note").Default);
                    Assert.Equal("json", d.Column("meta").BaseType);
                    Assert.True(d.Column("updated").OnUpdateNow);
                    Assert.Equal("CURRENT_TIMESTAMP", d.Column("updated").Default);
                    Assert.True(d.Column("name_len").IsGenerated);
                    Assert.Equal(new[] { "id" }, d.PrimaryKey.Columns);
                    Assert.Contains(d.Indexes, i => i.Kind == IndexKind.Unique && i.Columns.SequenceEqual(new[] { "email" }));
                    var fk = Assert.Single(d.ForeignKeys);
                    Assert.Equal("fk_course", fk.Name);
                    Assert.Equal("SET NULL", fk.OnDelete);
                    Assert.Equal("courses", fk.RefTable);
                    Assert.Contains(d.Checks, c => c.Name == "chk_name");
                    Assert.DoesNotContain(d.Checks, c => c.Expression.Contains("json_valid"));
                    Assert.Contains(d.Triggers, t => t.Name == "students_lower");
                    Assert.True(d.RowEstimate >= 0);

                    // reading back and diffing against itself is a no-op
                    Assert.Empty(DdlBuilder.Alter(d, d.Clone()));
                    var schema = await SchemaReader.ReadAsync(server);
                    Assert.DoesNotContain(DesignChecker.Check(d, schema), i => i.Severity == DesignSeverity.Bad);

                    // ---- create from a design, read it back ----
                    var n = TableDesign.NewTable("college", "results");
                    n.Columns.Add(new ColumnDef { Name = "student_id", BaseType = "int", Nullable = false });
                    n.Columns.Add(new ColumnDef { Name = "mark", BaseType = "decimal", Length = "5,2", Nullable = false, Default = "0" });
                    n.Columns.Add(new ColumnDef { Name = "grade", BaseType = "enum", Length = "'A','B','C','F'" });
                    n.Columns.Add(new ColumnDef { Name = "comment", BaseType = "varchar", Length = "200", Default = "it's ok" });
                    n.Columns.Add(new ColumnDef { Name = "taken", BaseType = "datetime", Default = "CURRENT_TIMESTAMP" });
                    n.Columns.Add(new ColumnDef { Name = "extra", BaseType = "json" });
                    n.Indexes.Add(new IndexDef { Name = "idx_student", Kind = IndexKind.Index, Columns = { "student_id" } });
                    n.ForeignKeys.Add(new ForeignKeyDef { Name = "fk_student", Columns = { "student_id" }, RefTable = "students", RefColumns = { "id" }, OnDelete = "CASCADE" });
                    n.Checks.Add(new CheckDef { Name = "chk_mark", Expression = "mark BETWEEN 0 AND 100" });
                    Assert.DoesNotContain(DesignChecker.Check(n, schema), i => i.Severity == DesignSeverity.Bad);
                    var create = DdlBuilder.Create(n);
                    _out.WriteLine(create);
                    var cr = await session.RunScriptAsync(create, schema, "create results");
                    Assert.True(cr.Ok, cr.Failed?.ErrorMessage);
                    var back = await Read(server, "college", "results");
                    back.OriginalName = n.Name; n.OriginalName = n.Name;
                    foreach (var c in n.Columns) c.OriginalName = c.Name;
                    var diff = DdlBuilder.Alter(back, n);
                    Assert.True(diff.Count == 0, "round trip differs: " + string.Join("\n", diff));
                    Assert.Equal("it's ok", back.Column("comment").Default);
                    Assert.Equal("json", back.Column("extra").BaseType);

                    // the check works
                    var bad = await session.RunScriptAsync("INSERT INTO college.results (student_id, mark) VALUES (1, 140);", await SchemaReader.ReadAsync(server), "x");
                    Assert.False(bad.Ok);
                    Assert.Equal(4025, bad.Failed.ErrorCode);

                    // ---- alter: rename, reorder, add, drop, index, FK rule change, check ----
                    var before = await Read(server, "college", "students");
                    var after = before.Clone();
                    after.Column("name").Name = "full_name";
                    var email = after.Column("email"); after.Columns.Remove(email); after.Columns.Insert(1, email);
                    after.Columns.Add(new ColumnDef { Name = "phone", BaseType = "varchar", Length = "20" });
                    after.Columns.Remove(after.Column("joined"));
                    after.Column("note").Length = "40";
                    after.Indexes.Add(new IndexDef { Name = "idx_status", Kind = IndexKind.Index, Columns = { "status", "full_name(10)" } });
                    after.ForeignKeys[0].OnDelete = "CASCADE";
                    after.Checks.RemoveAll(c => c.Name == "chk_name");
                    after.Checks.Add(new CheckDef { Name = "chk_name", Expression = "CHAR_LENGTH(full_name) > 1" });
                    // generated column uses `name`: point it at the new name
                    after.Column("name_len").GenerationExpression = "char_length(`full_name`)";
                    var alter = DdlBuilder.Alter(before, after);
                    foreach (var s in alter) _out.WriteLine(s);
                    var ar = await session.RunScriptAsync(string.Join("\n", alter), await SchemaReader.ReadAsync(server), "alter students");
                    Assert.True(ar.Ok, ar.Failed?.Statement.Text + " → " + ar.Failed?.ErrorMessage);
                    Assert.True(ar.Undo != null && ar.Undo.Covered, ar.Undo?.SkippedReason);
                    var now = await Read(server, "college", "students");
                    Assert.Equal(new[] { "id", "email", "full_name", "course_id", "status", "note", "meta", "updated", "name_len", "phone" }, now.Columns.Select(c => c.Name));
                    Assert.Equal("CASCADE", now.ForeignKeys[0].OnDelete);
                    Assert.Equal("40", now.Column("note").Length);
                    Assert.Contains(now.Indexes, i => i.Name == "idx_status" && i.Columns.SequenceEqual(new[] { "status", "full_name(10)" }));
                    Assert.Equal(3L, await Count(server, "SELECT COUNT(*) FROM college.students"));
                    Assert.Equal("ශාන්ති", await Scalar(server, "SELECT full_name FROM college.students WHERE id = 3"));
                    var wanted = after.Clone();
                    foreach (var c in wanted.Columns) c.OriginalName = c.Name;
                    var settled = DdlBuilder.Alter(now, wanted);
                    foreach (var s in settled) _out.WriteLine("settled: " + s);
                    // the server may rewrite expressions; columns, keys and FKs must match exactly
                    Assert.DoesNotContain(settled, s => s.Contains("COLUMN") || s.Contains("FOREIGN KEY") || s.Contains("INDEX"));

                    // one Undo puts the whole alter back
                    await session.Undo.RestoreAsync(ar.Undo.Batch.Id);
                    var restored = await Read(server, "college", "students");
                    Assert.Equal(d.Columns.Select(c => c.Name), restored.Columns.Select(c => c.Name));
                    Assert.Equal("2025-02-01", Convert.ToDateTime(await Scalar(server, "SELECT joined FROM college.students WHERE id = 2")).ToString("yyyy-MM-dd"));

                    // a failing middle statement stops the script and leaves one batch to put things back
                    var halfway = await session.RunScriptAsync("ALTER TABLE college.students ADD COLUMN x INT;\nALTER TABLE college.students ADD COLUMN x INT;", await SchemaReader.ReadAsync(server), "broken");
                    Assert.False(halfway.Ok);
                    Assert.Equal(1060, halfway.Failed.ErrorCode);
                    Assert.Equal(2, halfway.Results.Count);
                    _out.WriteLine(version + " ok");
                }
                await server.StopAsync();
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Rows_operations_and_objects(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var server = rig.NewService(3466);
                await server.StartAsync();
                using (var session = new NotebookSession(server))
                {
                    var setup = await session.RunScriptAsync(College, await SchemaReader.ReadAsync(server), "setup");
                    Assert.True(setup.Ok, setup.Failed?.ErrorMessage);
                    var t = await Read(server, "college", "students");

                    async Task<ScriptResult> Run(string sql)
                    {
                        var r = await session.RunScriptAsync(sql, await SchemaReader.ReadAsync(server), sql);
                        Assert.True(r.Ok, sql + " → " + r.Failed?.ErrorMessage);
                        return r;
                    }

                    // ---- browse ----
                    var q = new RowQuery { Table = t, Search = "x.lk", Filters = { new RowFilter { Column = "course_id", Op = "=", Value = "1" } }, SortColumn = "name" };
                    Assert.Equal(2L, await Count(server, q.CountSql()));
                    using (var c = await server.OpenConnectionAsync())
                    using (var cmd = new MySqlCommand(q.SelectSql(), c))
                    using (var rd = await cmd.ExecuteReaderAsync())
                    {
                        var names = new List<string>();
                        while (await rd.ReadAsync()) names.Add(rd.GetString(rd.GetOrdinal("name")));
                        Assert.Equal(new[] { "Nimal Perera", "ශාන්ති" }, names);
                    }
                    Assert.Equal(1L, await Count(server, new RowQuery { Table = t, Filters = { new RowFilter { Column = "joined", Op = "is empty (NULL)" } } }.CountSql()));
                    Assert.Equal(0L, await Count(server, new RowQuery { Table = t, Search = "%" }.CountSql()));   // % is literal
                    Assert.Equal(1L, await Count(server, new RowQuery { Table = t, Search = "ශාන්" }.CountSql()));

                    // ---- insert / update / delete through the session (each undoable) ----
                    var ins = await Run(RowSql.Insert(t, new Dictionary<string, FieldValue>
                    {
                        ["id"] = new FieldValue { UseDefault = true },
                        ["name"] = FieldValue.Of("O'Brien \\ Jr"),
                        ["email"] = FieldValue.Of("OB@X.LK"),
                        ["course_id"] = FieldValue.Of("2"),
                        ["joined"] = FieldValue.Of("2025-03-31"),
                        ["meta"] = FieldValue.Of("{\"a\": 1}"),
                    }));
                    var newId = ins.Results[0].LastInsertId;
                    Assert.True(newId >= 4);
                    var key = new Dictionary<string, string> { ["id"] = newId.ToString() };
                    Dictionary<string, string> row;
                    using (var c = await server.OpenConnectionAsync()) row = await RowStore.LoadRowAsync(c, t, key);
                    Assert.Equal("O'Brien \\ Jr", row["name"]);
                    Assert.Equal("ob@x.lk", row["email"]);            // trigger ran
                    Assert.Equal("2025-03-31", row["joined"]);
                    Assert.Equal("active", row["status"]);
                    Assert.Equal("", row["note"]);
                    Assert.Equal("2", row["course_id"]);

                    var upd = await Run(RowSql.Update(t, key, new Dictionary<string, FieldValue> { ["status"] = FieldValue.Of("left"), ["joined"] = FieldValue.Of(null) }));
                    Assert.Equal(1L, upd.Results[0].Affected);
                    Assert.Equal("left", await Scalar(server, "SELECT status FROM college.students WHERE id = " + newId));
                    await session.Undo.RestoreAsync(upd.Undo.Batch.Id);
                    Assert.Equal("active", await Scalar(server, "SELECT status FROM college.students WHERE id = " + newId));

                    var del = await Run(RowSql.Delete(t, new[] { (IDictionary<string, string>)key, new Dictionary<string, string> { ["id"] = "1" } }));
                    Assert.Equal(2L, del.Results[0].Affected);
                    await session.Undo.RestoreAsync(del.Undo.Batch.Id);
                    Assert.Equal(4L, await Count(server, "SELECT COUNT(*) FROM college.students"));

                    // ---- foreign-key lookups ----
                    var schema = await SchemaReader.ReadAsync(server);
                    var courses = schema.FindTable("college", "courses");
                    Assert.Equal("title", RowStore.LabelColumn(courses, "id"));
                    using (var c = await server.OpenConnectionAsync())
                    {
                        var all = await RowStore.LookupAsync(c, "college", "courses", "id", "title", null);
                        Assert.Equal(new[] { "1", "2" }, all.Select(i => i.Value));
                        var net = Assert.Single(await RowStore.LookupAsync(c, "college", "courses", "id", "title", "net"));
                        Assert.Equal("2", net.Value);
                        Assert.Contains("Networks", net.Label);
                        Assert.Equal(new[] { "students (fk_course)" }, await TableOps.ReferencedByAsync(c, "college", "courses"));
                        var collations = await TableOps.CollationsAsync(c);
                        Assert.Equal("utf8mb4_unicode_ci", collations[0]);
                        Assert.Contains("InnoDB", await TableOps.EnginesAsync(c));
                    }

                    // ---- operations, each undoable ----
                    var courseDesign = await Read(server, "college", "courses");
                    var copy = await Run(string.Join("\n", TableOps.Copy(courseDesign, "college", "courses_copy", true)));
                    Assert.Equal(2L, await Count(server, "SELECT COUNT(*) FROM college.courses_copy"));
                    Assert.Equal(new[] { "id" }, (await Read(server, "college", "courses_copy")).PrimaryKey.Columns);
                    await session.Undo.RestoreAsync(copy.Undo.Batch.Id);
                    Assert.Equal(0L, await Count(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='college' AND TABLE_NAME='courses_copy'"));

                    Assert.Contains("already a table called", TableOps.NewNameProblem(schema, "college", "COURSES"));
                    var ren = await Run(TableOps.Rename(courseDesign, "modules"));
                    Assert.Equal(1L, await Count(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='college' AND TABLE_NAME='modules'"));
                    await session.Undo.RestoreAsync(ren.Undo.Batch.Id);
                    Assert.Equal(1L, await Count(server, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='college' AND TABLE_NAME='courses'"));

                    var mv = await Run(TableOps.Move(courseDesign, "archive"));
                    Assert.Equal(2L, await Count(server, "SELECT COUNT(*) FROM archive.courses"));
                    await session.Undo.RestoreAsync(mv.Undo.Batch.Id);
                    Assert.Equal(2L, await Count(server, "SELECT COUNT(*) FROM college.courses"));

                    var coll = await Run(TableOps.ConvertCollation(courseDesign, "utf8mb4_bin"));
                    Assert.Equal("utf8mb4_bin", (await Read(server, "college", "courses")).Collation);

                    foreach (var verb in TableOps.MaintenanceVerbs)
                    {
                        var m = await Run(TableOps.Maintenance(courseDesign, verb));
                        Assert.NotEmpty(m.Results[0].Tables);
                        _out.WriteLine(version + " " + verb + ": " + string.Join(" | ", m.Results[0].Tables[0].Rows.Rows.Cast<System.Data.DataRow>().Select(r => string.Join(",", r.ItemArray))));
                    }

                    var studentDesign = await Read(server, "college", "students");
                    var empty = await Run(TableOps.Empty(studentDesign));
                    Assert.Equal(0L, await Count(server, "SELECT COUNT(*) FROM college.students"));
                    await session.Undo.RestoreAsync(empty.Undo.Batch.Id);
                    Assert.Equal(4L, await Count(server, "SELECT COUNT(*) FROM college.students"));

                    var drop = await Run(TableOps.Drop(studentDesign, false));
                    await session.Undo.RestoreAsync(drop.Undo.Batch.Id);
                    Assert.Equal(4L, await Count(server, "SELECT COUNT(*) FROM college.students"));

                    // ---- objects: templates run, definitions round trip ----
                    schema = await SchemaReader.ReadAsync(server);
                    var stable = schema.FindTable("college", "students");
                    await session.UseAsync("college");
                    foreach (var k in new[] { DbObjectKind.View, DbObjectKind.Procedure, DbObjectKind.Function, DbObjectKind.Trigger, DbObjectKind.Event })
                    {
                        var script = DbObjects.Template(k, stable);
                        var r = await session.RunScriptAsync(script, schema, "template " + k);
                        Assert.True(r.Ok, k + ": " + r.Failed?.Statement.Text + " → " + r.Failed?.ErrorMessage);
                    }

                    List<DbObjectInfo> objects;
                    using (var c = await server.OpenConnectionAsync()) objects = await DbObjects.ListAsync(c, "college");
                    Assert.Contains(objects, o => o.Kind == DbObjectKind.Procedure);
                    Assert.Contains(objects, o => o.Kind == DbObjectKind.Function);
                    Assert.Contains(objects, o => o.Kind == DbObjectKind.Trigger && o.Name == "students_lower" && o.Table == "students");
                    Assert.Contains(objects, o => o.Kind == DbObjectKind.Event);
                    schema = await SchemaReader.ReadAsync(server);
                    var view = schema.Find("college").Tables.Values.First(x => x.IsView);
                    objects.Add(new DbObjectInfo { Kind = DbObjectKind.View, Database = "college", Name = view.Name });

                    foreach (var o in objects)
                    {
                        string def;
                        using (var c = await server.OpenConnectionAsync()) def = await DbObjects.DefinitionAsync(c, o);
                        Assert.DoesNotContain("DEFINER", def);
                        Assert.Contains("CREATE OR REPLACE", def);
                        // saving the definition unchanged works and keeps the object
                        var r = await session.RunScriptAsync(def, schema, "save " + o.Name);
                        Assert.True(r.Ok, o.KindWord + " " + o.Name + ": " + r.Failed?.Statement.Text + " → " + r.Failed?.ErrorMessage);
                        using (var c = await server.OpenConnectionAsync())
                            Assert.Equal(def, await DbObjects.DefinitionAsync(c, o));
                    }

                    // dropping an object is undoable too
                    var proc = objects.First(o => o.Kind == DbObjectKind.Procedure);
                    var dp = await Run(DbObjects.DropSql(proc));
                    _out.WriteLine(version + " drop procedure undo: " + (dp.Undo?.Covered == true ? "covered" : dp.Undo?.SkippedReason));
                    Assert.Equal(4L, await Count(server, "SELECT COUNT(*) FROM college.students"));
                }
                await server.StopAsync();
            }
        }
    }
}
