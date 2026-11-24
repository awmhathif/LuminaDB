using LuminaDB.Core.Sql;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class SqlBracketsTests
    {
        [Fact]
        public void Open_brackets_are_found_outermost_first()
        {
            const string sql = "CREATE TABLE t (\n  id INT,\n  CHECK (id > 0";
            var open = SqlBrackets.OpenBefore(sql, sql.Length);
            Assert.Equal(new[] { sql.IndexOf('('), sql.LastIndexOf('(') }, open);
            Assert.Equal(sql.LastIndexOf('('), SqlBrackets.InnermostOpen(sql, sql.Length));
            Assert.Equal(sql.IndexOf('('), SqlBrackets.InnermostOpen(sql, sql.LastIndexOf('(')));
        }

        [Theory]
        [InlineData("SELECT '(' FROM t (")]           // in a string
        [InlineData("SELECT \"(\" FROM t (")]
        [InlineData("SELECT `a(b` FROM t (")]         // in a quoted name
        [InlineData("SELECT 1 -- (\nFROM t (")]       // in comments
        [InlineData("SELECT 1 # (\nFROM t (")]
        [InlineData("SELECT /* ( */ 1 FROM t (")]
        [InlineData("SELECT 'it''s (' FROM t (")]     // doubled quote
        [InlineData("SELECT 'a\\'(' FROM t (")]       // backslash escape
        public void Brackets_in_strings_and_comments_do_not_count(string sql)
        {
            Assert.Equal(new[] { sql.Length - 1 }, SqlBrackets.OpenBefore(sql, sql.Length));
        }

        [Fact]
        public void A_semicolon_ends_the_statement()
        {
            const string sql = "SELECT COUNT(;\nCREATE TABLE t (";
            Assert.Equal(new[] { sql.Length - 1 }, SqlBrackets.OpenBefore(sql, sql.Length));
            Assert.Equal(-1, SqlBrackets.Partner(sql, sql.IndexOf('(')));
        }

        [Fact]
        public void Partners_are_found_both_ways_and_nested()
        {
            const string sql = "SELECT a FROM t WHERE (a IN (1, 2)) AND b;";
            int outer = sql.IndexOf('('), inner = sql.IndexOf('(', outer + 1);
            int innerClose = sql.IndexOf(')'), outerClose = innerClose + 1;
            Assert.Equal(outerClose, SqlBrackets.Partner(sql, outer));
            Assert.Equal(outer, SqlBrackets.Partner(sql, outerClose));
            Assert.Equal(innerClose, SqlBrackets.Partner(sql, inner));
            Assert.Equal(inner, SqlBrackets.Partner(sql, innerClose));
        }

        [Fact]
        public void Unmatched_and_non_brackets()
        {
            Assert.Equal(-1, SqlBrackets.Partner("SELECT (1", 7));
            Assert.Equal(-1, SqlBrackets.Partner("SELECT 1)", 8));
            Assert.Null(SqlBrackets.Partner("SELECT 1", 3));                 // not a bracket
            Assert.Null(SqlBrackets.Partner("SELECT '(' , 1", 8));           // a bracket in a string
            Assert.Null(SqlBrackets.Partner("", 0));
            Assert.Null(SqlBrackets.Partner("()", 5));
        }
    }
}
