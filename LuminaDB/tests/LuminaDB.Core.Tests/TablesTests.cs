using System.Collections.Generic;
using System.Linq;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;
using Xunit;

namespace LuminaDB.Core.Tests
{
    /// <summary>Tables: types, defaults, CREATE/ALTER, design checks, row SQL and value checks. No server needed.</summary>
    public class TablesTests
    {
        private static ColumnDef Col(string name, string type, string length = "", bool nullable = true, string def = "")
        {
            var c = new ColumnDef { Name = name, OriginalName = name, BaseType = type, Length = length, Nullable = nullable, Default = def };
            return c;
        }

        private static TableDesign Students()
        {
            var d = new TableDesign { Database = "college", Name = "students", OriginalName = "students" };
            var id = Col("id", "int", nullable: false); id.AutoIncrement = true;
            d.Columns.Add(id);
            d.Columns.Add(Col("name", "varchar", "100", nullable: false));
            d.Columns.Add(Col("email", "varchar", "120"));
            d.Columns.Add(Col("course_id", "int"));
            d.Columns.Add(Col("status", "enum", "'active','left'", nullable: false, def: "active"));
            d.Indexes.Add(new IndexDef { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = { "id" } });
            d.Indexes.Add(new IndexDef { Name = "email", Kind = IndexKind.Unique, Columns = { "email" } });
            d.ForeignKeys.Add(new ForeignKeyDef { Name = "fk_course", Columns = { "course_id" }, RefTable = "courses", RefColumns = { "id" } });
            return d;
        }

        [Theory]
        [InlineData("int(11) unsigned", "int", "", true, "INT UNSIGNED")]
        [InlineData("int(11)", "int", "", false, "INT")]
        [InlineData("tinyint(1)", "tinyint", "1", false, "TINYINT(1)")]
        [InlineData("decimal(10,2)", "decimal", "10,2", false, "DECIMAL(10,2)")]
        [InlineData("enum('a','b''s')", "enum", "'a','b''s'", false, "ENUM('a','b''s')")]
        [InlineData("varchar(100)", "varchar", "100", false, "VARCHAR(100)")]
        [InlineData("double precision", "double precision", "", false, "DOUBLE PRECISION")]
        public void Column_types_are_parsed(string columnType, string baseType, string length, bool unsigned, string full)
        {
            var c = new ColumnDef();
            ColumnTypes.Parse(columnType, c);
            Assert.Equal(baseType, c.BaseType);
            Assert.Equal(length, c.Length);
            Assert.Equal(unsigned, c.Unsigned);
            Assert.Equal(full, c.FullType);
        }

        [Fact]
        public void Enum_values_unquote()
        {
            Assert.Equal(new[] { "a", "b's", "c,d" }, ColumnTypes.EnumValues("'a','b''s','c,d'"));
        }

        [Theory]
        [InlineData("varchar", "abc", "'abc'")]
        [InlineData("varchar", "it's", "'it''s'")]
        [InlineData("varchar", "''", "''")]
        [InlineData("int", "3", "3")]
        [InlineData("int", "NULL", "NULL")]
        [InlineData("datetime", "CURRENT_TIMESTAMP", "current_timestamp()")]
        [InlineData("datetime", "now()", "current_timestamp()")]
        [InlineData("varchar", "uuid()", "(uuid())")]
        [InlineData("varchar", "123", "'123'")]
        [InlineData("date", "2025-01-01", "'2025-01-01'")]
        [InlineData("varchar", "", null)]
        public void Defaults_become_sql(string type, string def, string sql)
        {
            Assert.Equal(sql, DdlBuilder.DefaultSql(Col("c", type, def: def)));
        }

        [Theory]
        [InlineData("varchar", "'abc'", "abc")]
        [InlineData("varchar", "''", "''")]
        [InlineData("varchar", "'NULL'", "'NULL'")]
        [InlineData("int", "3", "3")]
        [InlineData("datetime", "current_timestamp()", "CURRENT_TIMESTAMP")]
        [InlineData("varchar", "NULL", "NULL")]
        [InlineData("varchar", "'it''s'", "it's")]
        public void Server_defaults_display_and_round_trip(string type, string serverDefault, string shown)
        {
            var c = Col("c", type);
            Assert.Equal(shown, DdlBuilder.DefaultForDisplay(serverDefault, c));
            c.Default = shown;
            var back = DdlBuilder.DefaultSql(c);
            Assert.Equal(serverDefault == "current_timestamp()" ? "current_timestamp()" : serverDefault, back);
        }

        [Fact]
        public void Create_writes_columns_keys_and_options()
        {
            var sql = DdlBuilder.Create(Students());
            Assert.StartsWith("CREATE TABLE `college`.`students` (", sql);
            Assert.Contains("`id` INT NOT NULL AUTO_INCREMENT", sql);
            Assert.Contains("`status` ENUM('active','left') NOT NULL DEFAULT 'active'", sql);
            Assert.Contains("`email` VARCHAR(120) NULL", sql);
            Assert.Contains("PRIMARY KEY (`id`)", sql);
            Assert.Contains("UNIQUE INDEX `email` (`email`)", sql);
            Assert.Contains("CONSTRAINT `fk_course` FOREIGN KEY (`course_id`) REFERENCES `courses` (`id`) ON DELETE RESTRICT ON UPDATE RESTRICT", sql);
            Assert.EndsWith("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;", sql);
        }

        [Fact]
        public void Unchanged_design_needs_no_sql()
        {
            var d = Students();
            Assert.Empty(DdlBuilder.Alter(d, d.Clone()));
        }

        [Fact]
        public void Rename_move_add_drop_become_one_alter()
        {
            var before = Students();
            var after = before.Clone();
            after.Column("name").Name = "full_name";                            // rename
            var email = after.Column("email");
            after.Columns.Remove(email);
            after.Columns.Insert(1, email);                                    // move email after id
            after.Columns.Add(Col("phone", "varchar", "20") );                 // add at the end
            after.Columns.Last().OriginalName = null;
            after.Columns.Remove(after.Column("status"));                      // drop
            var sql = DdlBuilder.Alter(before, after);
            Assert.Single(sql);
            var s = sql[0];
            Assert.Contains("DROP COLUMN `status`", s);
            Assert.Contains("MODIFY COLUMN `email` VARCHAR(120) NULL AFTER `id`", s);
            Assert.Contains("CHANGE COLUMN `name` `full_name` VARCHAR(100) NOT NULL", s);
            Assert.DoesNotContain("`full_name` VARCHAR(100) NOT NULL AFTER", s);   // already after email once email moved
            Assert.Contains("ADD COLUMN `phone` VARCHAR(20) NULL AFTER `course_id`", s);
            Assert.True(s.IndexOf("DROP COLUMN", System.StringComparison.Ordinal) < s.IndexOf("ADD COLUMN", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Changed_foreign_key_is_dropped_first_and_added_last()
        {
            var before = Students();
            var after = before.Clone();
            after.ForeignKeys[0].OnDelete = "CASCADE";
            after.Indexes.Add(new IndexDef { Name = "idx_name", Kind = IndexKind.Index, Columns = { "name(20)" } });
            var sql = DdlBuilder.Alter(before, after);
            Assert.Equal(3, sql.Count);
            Assert.Contains("DROP FOREIGN KEY `fk_course`", sql[0]);
            Assert.Contains("ADD INDEX `idx_name` (`name`(20))", sql[1]);
            Assert.Contains("ADD CONSTRAINT `fk_course` FOREIGN KEY (`course_id`) REFERENCES `courses` (`id`) ON DELETE CASCADE", sql[2]);
        }

        [Fact]
        public void No_action_and_restrict_are_the_same()
        {
            var before = Students();
            var after = before.Clone();
            after.ForeignKeys[0].OnDelete = "NO ACTION";
            Assert.Empty(DdlBuilder.Alter(before, after));
        }

        [Fact]
        public void Primary_key_change_drops_and_adds()
        {
            var before = Students();
            var after = before.Clone();
            after.Column("id").AutoIncrement = false;
            after.PrimaryKey.Columns = new List<string> { "id", "name" };
            var s = string.Join("\n", DdlBuilder.Alter(before, after));
            Assert.Contains("DROP PRIMARY KEY", s);
            Assert.Contains("ADD PRIMARY KEY (`id`, `name`)", s);
            Assert.Contains("MODIFY COLUMN `id` INT NOT NULL", s);
        }

        [Fact]
        public void Checker_catches_the_usual_design_mistakes()
        {
            var d = TableDesign.NewTable("college", "things");
            d.Columns.Add(new ColumnDef { Name = "title", BaseType = "varchar" });                          // no length
            d.Columns.Add(new ColumnDef { Name = "price", BaseType = "decimal", Length = "5,7" });         // scale > precision
            d.Columns.Add(new ColumnDef { Name = "code", BaseType = "varchr", Length = "10" });             // typo
            d.Columns.Add(new ColumnDef { Name = "kind", BaseType = "enum", Length = "small,large" });     // no quotes
            d.Columns.Add(new ColumnDef { Name = "seq", BaseType = "varchar", Length = "5", AutoIncrement = true });
            d.Columns.Add(new ColumnDef { Name = "Title", BaseType = "int" });                              // duplicate name
            d.Columns.Add(new ColumnDef { Name = "made", BaseType = "int", Nullable = false, Default = "NULL" });
            d.Columns.Add(new ColumnDef { Name = "course_id", BaseType = "bigint", Nullable = false });
            d.ForeignKeys.Add(new ForeignKeyDef { Name = "fk", Columns = { "course_id" }, RefTable = "courses", RefColumns = { "id" }, OnDelete = "SET NULL" });
            var schema = new SchemaSnapshot();
            schema.AddTable("college", "courses", "id int(11)", "title varchar(80)");
            var issues = DesignChecker.Check(d, schema).Where(i => i.Severity == DesignSeverity.Bad).Select(i => i.Message).ToList();
            Assert.Contains(issues, m => m.Contains("title needs a length"));
            Assert.Contains(issues, m => m.Contains("decimals (7)"));
            Assert.Contains(issues, m => m.Contains("varchr isn't a MariaDB type") && m.Contains("varchar"));
            Assert.Contains(issues, m => m.Contains("values in quotes"));
            Assert.Contains(issues, m => m.Contains("only whole-number columns"));
            Assert.Contains(issues, m => m.Contains("Two columns are called"));
            Assert.Contains(issues, m => m.Contains("default can't be NULL"));
            Assert.Contains(issues, m => m.Contains("bigint") && m.Contains("same type"));
            Assert.Contains(issues, m => m.Contains("can't SET NULL"));
        }

        [Fact]
        public void Checker_accepts_a_good_design_and_warns_about_float_and_missing_key()
        {
            var good = Students();
            var schema = new SchemaSnapshot();
            schema.AddTable("college", "courses", "id int(11)", "title varchar(80)");
            Assert.DoesNotContain(DesignChecker.Check(good, schema), i => i.Severity == DesignSeverity.Bad);

            var d = new TableDesign { Database = "college", Name = "t" };
            d.Columns.Add(new ColumnDef { Name = "price", BaseType = "float" });
            var w = DesignChecker.Check(d, schema);
            Assert.Contains(w, i => i.Severity == DesignSeverity.Warn && i.Message.Contains("decimal(10,2)"));
            Assert.Contains(w, i => i.Severity == DesignSeverity.Warn && i.Message.Contains("no primary key"));
        }

        [Fact]
        public void Row_query_searches_filters_sorts_and_pages()
        {
            var q = new RowQuery
            {
                Table = Students(),
                Search = "50%_off",
                Filters = { new RowFilter { Column = "course_id", Op = "≥", Value = "2" }, new RowFilter { Column = "email", Op = "is empty (NULL)" }, new RowFilter { Column = "name", Op = "starts with", Value = "O'B" } },
                SortColumn = "name",
                SortDescending = true,
                Offset = 200,
                Limit = 100,
            };
            var sql = q.SelectSql();
            Assert.Contains("`course_id` >= 2", sql);
            Assert.Contains("`email` IS NULL", sql);
            Assert.Contains("`name` LIKE 'O''B%'", sql);
            Assert.Contains("`name` LIKE '%50\\\\%\\\\_off%'", sql);
            Assert.EndsWith("ORDER BY `name` DESC LIMIT 100 OFFSET 200;", sql);
            Assert.StartsWith("SELECT COUNT(*) FROM `college`.`students` WHERE", q.CountSql());
            Assert.EndsWith("ORDER BY `id` LIMIT 100;", new RowQuery { Table = Students() }.SelectSql());
        }

        [Fact]
        public void Row_sql_inserts_updates_and_deletes_by_key()
        {
            var t = Students();
            var ins = RowSql.Insert(t, new Dictionary<string, FieldValue>
            {
                ["id"] = new FieldValue { UseDefault = true },
                ["name"] = FieldValue.Of("O'Brien"),
                ["email"] = FieldValue.Of(null),
                ["course_id"] = FieldValue.Of("2"),
            });
            Assert.Equal("INSERT INTO `college`.`students` (`name`, `email`, `course_id`)\nVALUES ('O''Brien', NULL, 2);", ins);

            var key = new Dictionary<string, string> { ["id"] = "7" };
            Assert.Null(RowSql.Update(t, key, new Dictionary<string, FieldValue>()));
            var upd = RowSql.Update(t, key, new Dictionary<string, FieldValue> { ["email"] = FieldValue.Of("a\\b@x.lk"), ["status"] = new FieldValue { UseDefault = true } });
            Assert.Equal("UPDATE `college`.`students`\nSET `email` = 'a\\\\b@x.lk', `status` = DEFAULT\nWHERE `id` = 7\nLIMIT 1;", upd);

            var del = RowSql.Delete(t, new[] { (IDictionary<string, string>)new Dictionary<string, string> { ["id"] = "1" }, new Dictionary<string, string> { ["id"] = "2" } });
            Assert.Equal("DELETE FROM `college`.`students`\nWHERE (`id` = 1)\n   OR (`id` = 2)\nLIMIT 2;", del);

            var noKey = new TableDesign { Database = "d", Name = "log", Columns = { Col("msg", "text") } };
            Assert.Throws<LuminaException>(() => RowSql.KeyWhere(noKey, new Dictionary<string, string>()));
        }

        [Fact]
        public void Unique_not_null_key_works_when_there_is_no_primary_key()
        {
            var t = new TableDesign { Database = "d", Name = "t", Columns = { Col("code", "varchar", "10", nullable: false), Col("x", "int") } };
            t.Indexes.Add(new IndexDef { Name = "code", Kind = IndexKind.Unique, Columns = { "code" } });
            Assert.Equal(new[] { "code" }, t.RowKey);
            Assert.Equal("`code` = 'A1'", RowSql.KeyWhere(t, new Dictionary<string, string> { ["code"] = "A1" }));
        }

        [Theory]
        [InlineData("tinyint", "", false, "300", "holds -128 to 127")]
        [InlineData("int", "", true, "-1", "holds 0 to")]
        [InlineData("int", "", false, "12abc", "whole number")]
        [InlineData("int", "", false, "42", null)]
        [InlineData("decimal", "5,2", false, "1234.5", "at most 3 digit")]
        [InlineData("decimal", "5,2", false, "12,50", "a dot, no commas")]
        [InlineData("decimal", "5,2", false, "123.45", null)]
        [InlineData("varchar", "5", false, "abcdef", "at most 5 characters")]
        [InlineData("varchar", "5", false, "ශාන්ති", "at most 5 characters; this has 6")]
        [InlineData("varchar", "2", false, "😀😀", null)]
        [InlineData("date", "", false, "2025-02-30", "year-month-day")]
        [InlineData("date", "", false, "31/03/2025", "year-month-day")]
        [InlineData("date", "", false, "2024-02-29", null)]
        [InlineData("datetime", "", false, "2025-03-31 25:00", "date and time")]
        [InlineData("datetime", "", false, "2025-03-31 14:30", null)]
        [InlineData("timestamp", "", false, "1969-12-31 00:00:00", "1970 to 2106")]
        [InlineData("enum", "'small','large'", false, "medium", "one of: small, large")]
        [InlineData("set", "'a','b'", false, "a,c", "c isn't allowed")]
        [InlineData("json", "", false, "{\"a\": [1, 2}", "takes JSON")]
        [InlineData("json", "", false, "{\"a\": [1, 2]}", null)]
        [InlineData("time", "", false, "14:30", null)]
        [InlineData("year", "", false, "1800", "1901 to 2155")]
        public void Values_are_checked_before_sending(string type, string length, bool unsigned, string value, string problem)
        {
            var c = Col("c", type, length); c.Unsigned = unsigned;
            var p = RowSql.Problem(c, FieldValue.Of(value), isInsert: true);
            if (problem == null) Assert.Null(p);
            else { Assert.NotNull(p); Assert.Contains(problem, p); }
        }

        [Fact]
        public void Null_and_default_rules()
        {
            var name = Col("name", "varchar", "10", nullable: false);
            Assert.Contains("can't be empty", RowSql.Problem(name, FieldValue.Of(null), true));
            Assert.Contains("no default", RowSql.Problem(name, new FieldValue { UseDefault = true }, true));
            var id = Col("id", "int", nullable: false); id.AutoIncrement = true;
            Assert.Null(RowSql.Problem(id, new FieldValue { UseDefault = true }, true));
            var credits = Col("credits", "tinyint", nullable: false, def: "3");
            Assert.Null(RowSql.Problem(credits, new FieldValue { UseDefault = true }, true));
        }

        [Fact]
        public void Literals_keep_numbers_and_quote_the_rest()
        {
            Assert.Equal("42", RowSql.Literal(Col("c", "int"), "42"));
            Assert.Equal("'42abc'", RowSql.Literal(Col("c", "int"), "42abc"));
            Assert.Equal("'42'", RowSql.Literal(Col("c", "varchar", "5"), "42"));
            Assert.Equal("0x00FF", RowSql.Literal(Col("c", "blob"), "0x00FF"));
            Assert.Equal("NULL", RowSql.Literal(Col("c", "int"), null));
        }

        [Fact]
        public void Object_definitions_become_editable_scripts()
        {
            var view = DbObjects.ToScript(DbObjectKind.View, "CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`localhost` SQL SECURITY DEFINER VIEW `v` AS select `s`.`name` AS `name` from `college`.`students` `s` where `s`.`id` > 1");
            Assert.StartsWith("CREATE OR REPLACE VIEW `v` AS\nSELECT", view);
            Assert.DoesNotContain("DEFINER", view);
            Assert.Contains("WHERE", view);

            var proc = DbObjects.ToScript(DbObjectKind.Procedure, "CREATE DEFINER=`root`@`localhost` PROCEDURE `p`(IN x INT)\nBEGIN\n  SELECT x;\nEND");
            Assert.Equal("DELIMITER $$\nCREATE OR REPLACE PROCEDURE `p`(IN x INT)\nBEGIN\n  SELECT x;\nEND$$\nDELIMITER ;\n", proc);
            var parts = SqlSplitter.Split(proc);
            Assert.Single(parts, s => !string.IsNullOrWhiteSpace(s.Text));
        }

        [Theory]
        [InlineData(DbObjectKind.View)]
        [InlineData(DbObjectKind.Procedure)]
        [InlineData(DbObjectKind.Function)]
        [InlineData(DbObjectKind.Trigger)]
        [InlineData(DbObjectKind.Event)]
        public void Templates_pass_the_checker(DbObjectKind kind)
        {
            var schema = new SchemaSnapshot();
            var t = schema.AddTable("college", "students", "id int(11) PRI", "name varchar(100)");
            var script = DbObjects.Template(kind, t);
            var a = new SqlGuard(schema).Analyze(script, "college");
            Assert.DoesNotContain(a.Issues, i => i.Severity == IssueSeverity.Bad);
            Assert.Contains("CREATE OR REPLACE", script);
        }

        [Fact]
        public void Operations_write_the_expected_sql()
        {
            var t = Students();
            Assert.Equal("RENAME TABLE `college`.`students` TO `college`.`learners`;", TableOps.Rename(t, "learners"));
            Assert.Equal("RENAME TABLE `college`.`students` TO `archive`.`students`;", TableOps.Move(t, "archive"));
            var copy = TableOps.Copy(t, "college", "students_copy", true);
            Assert.Equal("CREATE TABLE `college`.`students_copy` LIKE `college`.`students`;", copy[0]);
            Assert.StartsWith("INSERT INTO `college`.`students_copy` (`id`, `name`", copy[1]);
            Assert.Equal("ALTER TABLE `college`.`students` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;", TableOps.ConvertCollation(t, "utf8mb4_bin"));
            Assert.Equal("CHECK TABLE `college`.`students`;", TableOps.Maintenance(t, "CHECK"));
            Assert.Throws<System.ArgumentException>(() => TableOps.Maintenance(t, "DROP"));
            Assert.Equal("DELETE FROM `college`.`students`;", TableOps.Empty(t));
        }
    }
}
