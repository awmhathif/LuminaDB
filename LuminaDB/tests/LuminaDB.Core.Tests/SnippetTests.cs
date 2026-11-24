using System;
using System.IO;
using System.Linq;
using LuminaDB.Core.Sql;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class SnippetTests
    {
        [Fact]
        public void Blanks_repeats_and_the_end_mark_are_read()
        {
            var p = SnippetTemplate.Parse("SELECT ${2:*} FROM ${1:t} WHERE ${1}.id = 1;${0}");
            Assert.Equal(new[] { "Text:0:SELECT ", "Field:2:*", "Text:0: FROM ", "Field:1:t", "Text:0: WHERE ", "Field:1:t", "Text:0:.id = 1;", "Caret:0:" },
                         p.Select(x => x.ToString()));
            Assert.Equal(2, SnippetTemplate.FieldCount("SELECT ${2:*} FROM ${1:t} WHERE ${1}.id = 1;${0}"));
        }

        [Fact]
        public void A_repeat_written_first_still_gets_the_text()
        {
            var p = SnippetTemplate.Parse("${1}, ${1:name}");
            Assert.Equal(new[] { "name", "name" }, p.Where(x => x.Kind == SnippetPartKind.Field).Select(x => x.Text));
            Assert.Equal("name, name", SnippetTemplate.PlainText("${1}, ${1:name}"));
            Assert.Equal("a, a", SnippetTemplate.PlainText("${1:a}, ${1:b}"));
        }

        [Fact]
        public void Ordinary_sql_with_dollars_stays_as_written()
        {
            const string sql = "DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT '$1', 'a${x}', '${', '${1:two\nlines}'; END $$\nDELIMITER ;";
            var parts = SnippetTemplate.Parse(sql);
            Assert.Single(parts);
            Assert.Equal(SnippetPartKind.Text, parts[0].Kind);
            Assert.Equal(sql, SnippetTemplate.PlainText(sql));
        }

        [Fact]
        public void Windows_line_ends_become_plain_newlines_and_a_second_end_mark_is_dropped()
        {
            var p = SnippetTemplate.Parse("A;${0}\r\nB;${0}");
            Assert.Single(p, x => x.Kind == SnippetPartKind.Caret);
            Assert.Equal("A;\nB;", SnippetTemplate.PlainText("A;${0}\r\nB;${0}"));
        }

        [Fact]
        public void Built_in_snippets_have_unique_words_an_end_mark_and_sql_the_checker_accepts()
        {
            var all = SqlSnippets.BuiltIn;
            Assert.Equal(all.Count, all.Select(s => s.Trigger.ToLowerInvariant()).Distinct().Count());
            foreach (var s in all)
            {
                Assert.True(s.IsBuiltIn);
                Assert.Matches("^[a-z][a-z0-9_]{1,19}$", s.Trigger);
                Assert.False(SqlSnippets.SqlWords.Contains(s.Trigger), s.Trigger + " is an SQL word, so the list would pick the word, not the snippet");
                Assert.Single(SnippetTemplate.Parse(s.Body), x => x.Kind == SnippetPartKind.Caret);
                Assert.True(SnippetTemplate.FieldCount(s.Body) >= 1, s.Trigger + " has no blanks");
                var sql = s.Preview;
                Assert.DoesNotContain("${", sql);
                var statements = SqlSplitter.Split(sql).Where(x => x.Text.Trim().Length > 0).ToList();
                Assert.NotEmpty(statements);
                Assert.All(statements, x => Assert.True(x.Terminated, s.Trigger + ": a command has no ending ; in " + x.Text));
                var bad = new SqlGuard(SchemaSnapshot.Empty).Analyze(sql, "x").Issues.Where(i => i.Severity == IssueSeverity.Bad).ToList();
                Assert.True(bad.Count == 0, s.Trigger + ": " + string.Join("; ", bad.Select(i => i.Message)));
            }
        }

        [Fact]
        public void The_procedure_snippet_splits_into_its_three_commands()
        {
            var proc = SqlSnippets.BuiltIn.Single(s => s.Trigger == "proc");
            var statements = SqlSplitter.Split(proc.Preview).Where(x => x.Text.Trim().Length > 0).ToList();
            Assert.Contains(statements, x => x.Text.Contains("CREATE PROCEDURE") && x.Text.Contains("END"));
            Assert.Contains(statements, x => x.Text.TrimStart().StartsWith("CALL", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("", "Type the word")]
        [InlineData("q", "at least 2")]
        [InlineData("2go", "Start it with a letter")]
        [InlineData("my q", "no spaces")]
        [InlineData("ශා", "Start it with a letter")]
        [InlineData("sel", "built-in snippet for SELECT rows")]
        [InlineData("SEL", "built-in")]
        [InlineData("from", "SQL word")]
        [InlineData("now", "SQL word")]
        [InlineData("abcdefghijklmnopqrstu", "20 letters")]
        public void Bad_words_are_refused_with_a_reason(string trigger, string expected)
        {
            Assert.Contains(expected, SqlSnippets.TriggerProblem(trigger));
        }

        [Theory]
        [InlineData("myq")]
        [InlineData("top_students")]
        [InlineData("q2")]
        public void Good_words_are_accepted(string trigger) => Assert.Null(SqlSnippets.TriggerProblem(trigger));

        [Fact]
        public void Your_snippets_are_saved_replaced_deleted_and_read_back()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lumina-snippets-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(dir, "snippets.json");
            try
            {
                var store = new SnippetStore(file);
                int changed = 0;
                store.Changed += () => changed++;
                Assert.Empty(store.Mine);
                Assert.Equal(SqlSnippets.BuiltIn.Count, store.All.Count);

                store.Save("top", "Top marks", "SELECT * FROM marks\r\nORDER BY score DESC LIMIT ${1:5};\n\n");
                store.Save("zz", "", "-- a note\nSELECT 1;");
                Assert.Equal(2, changed);
                Assert.Equal("SELECT 1;", store.Find("ZZ").Title);
                Assert.Equal("SELECT * FROM marks\nORDER BY score DESC LIMIT ${1:5};", store.Find("top").Body);
                Assert.True(store.HasMine("TOP"));

                store.Save("TOP", "Best five", "SELECT 5;");
                Assert.Equal(2, store.Mine.Count);
                Assert.Equal("Best five", store.Find("top").Title);

                var again = new SnippetStore(file);
                Assert.Equal(new[] { "TOP", "zz" }, again.Mine.Select(s => s.Trigger));
                Assert.All(again.Mine, s => Assert.False(s.IsBuiltIn));
                Assert.Equal(SqlSnippets.BuiltIn.Count + 2, again.All.Count);
                Assert.Same(SqlSnippets.BuiltIn[0], again.Find("sel"));

                Assert.True(again.Delete("zz"));
                Assert.False(again.Delete("zz"));
                Assert.Single(new SnippetStore(file).Mine);

                var ex = Assert.Throws<LuminaException>(() => again.Save("del", "x", "SELECT 1;"));
                Assert.Contains("built-in", ex.Message);
                Assert.Throws<LuminaException>(() => again.Save("blank", "x", "  \n "));
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        [Fact]
        public void A_damaged_or_hand_edited_file_is_cleaned_up_on_load()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lumina-snippets-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(dir, "snippets.json");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(file, "{\"Snippets\":[{\"Trigger\":\" sel \",\"Title\":\"x\",\"Body\":\"SELECT 2;\"},{\"Trigger\":\"a1\",\"Title\":\"\",\"Body\":\"SELECT 1;\"},{\"Trigger\":\"a1\",\"Title\":\"later\",\"Body\":\"SELECT 3;\"},{\"Trigger\":\"\",\"Body\":\"x\"},null]}");
                var store = new SnippetStore(file);
                Assert.Single(store.Mine);
                Assert.Equal("later", store.Find("a1").Title);
                Assert.True(store.Find("sel").IsBuiltIn);

                File.WriteAllText(file, "not json");
                Assert.Empty(new SnippetStore(file).Mine);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
