using System;
using System.Collections.Generic;
using System.Text;

namespace LuminaDB.Core.Sql
{
    public enum TokenKind { Word, QuotedIdent, String, Number, Comment, Symbol, Whitespace, Variable }

    public struct SqlToken
    {
        public SqlToken(TokenKind kind, int start, int length, string text, bool unterminated = false)
        {
            Kind = kind; Start = start; Length = length; Text = text; Unterminated = unterminated;
        }

        public TokenKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public string Text { get; }
        /// <summary>A string, `identifier` or /* comment */ that never closes.</summary>
        public bool Unterminated { get; }
        public bool IsSignificant => Kind != TokenKind.Whitespace && Kind != TokenKind.Comment;
        public bool IsWord(string w) => Kind == TokenKind.Word && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string s) => Kind == TokenKind.Symbol && Text == s;
        public string Upper => Text.ToUpperInvariant();

        /// <summary>The identifier this token names: `my table` → my table, plain words as written.</summary>
        public string IdentName => Kind == TokenKind.QuotedIdent
            ? (Text.Length >= 2 && !Unterminated ? Text.Substring(1, Text.Length - 2).Replace("``", "`") : Text.TrimStart('`'))
            : Text;

        public override string ToString() => $"{Kind}:{Text}";
    }

    /// <summary>
    /// MariaDB/MySQL tokenizer. Knows the details that trip students: '' and \' inside strings,
    /// "-- " needing a space, # comments, /*! version comments */, `backtick names`, @variables.
    /// </summary>
    public static class SqlLexer
    {
        public static List<SqlToken> Tokenize(string sql)
        {
            var list = new List<SqlToken>();
            if (string.IsNullOrEmpty(sql)) return list;
            int i = 0, n = sql.Length;
            while (i < n)
            {
                char c = sql[i];
                int start = i;
                if (char.IsWhiteSpace(c))
                {
                    while (i < n && char.IsWhiteSpace(sql[i])) i++;
                    list.Add(new SqlToken(TokenKind.Whitespace, start, i - start, sql.Substring(start, i - start)));
                }
                else if (c == '#' || (c == '-' && i + 1 < n && sql[i + 1] == '-' && (i + 2 >= n || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))))
                {
                    while (i < n && sql[i] != '\n') i++;
                    list.Add(new SqlToken(TokenKind.Comment, start, i - start, sql.Substring(start, i - start)));
                }
                else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    bool open = close < 0;
                    i = open ? n : close + 2;
                    list.Add(new SqlToken(TokenKind.Comment, start, i - start, sql.Substring(start, i - start), open));
                }
                else if (c == '\'' || c == '"')
                {
                    bool closed = ScanQuoted(sql, ref i, c, true);
                    list.Add(new SqlToken(TokenKind.String, start, i - start, sql.Substring(start, i - start), !closed));
                }
                else if (c == '`')
                {
                    bool closed = ScanQuoted(sql, ref i, '`', false);
                    list.Add(new SqlToken(TokenKind.QuotedIdent, start, i - start, sql.Substring(start, i - start), !closed));
                }
                else if (c == '@')
                {
                    i++;
                    if (i < n && sql[i] == '@') i++;
                    if (i < n && (sql[i] == '\'' || sql[i] == '"' || sql[i] == '`')) ScanQuoted(sql, ref i, sql[i], sql[i] != '`');
                    else while (i < n && (IsWordChar(sql[i]) || sql[i] == '.')) i++;
                    list.Add(new SqlToken(TokenKind.Variable, start, i - start, sql.Substring(start, i - start)));
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1]) && (list.Count == 0 || !EndsName(list))))
                {
                    if (c == '0' && i + 1 < n && (sql[i + 1] == 'x' || sql[i + 1] == 'X'))
                    {
                        i += 2;
                        while (i < n && Uri.IsHexDigit(sql[i])) i++;
                    }
                    else
                    {
                        while (i < n && char.IsDigit(sql[i])) i++;
                        if (i < n && sql[i] == '.' && i + 1 < n && char.IsDigit(sql[i + 1])) { i++; while (i < n && char.IsDigit(sql[i])) i++; }
                        else if (i < n && sql[i] == '.' && (i + 1 >= n || !IsWordChar(sql[i + 1]))) i++;
                        if (i < n && (sql[i] == 'e' || sql[i] == 'E') && i + 1 < n && (char.IsDigit(sql[i + 1]) || ((sql[i + 1] == '-' || sql[i + 1] == '+') && i + 2 < n && char.IsDigit(sql[i + 2]))))
                        {
                            i += 2;
                            while (i < n && char.IsDigit(sql[i])) i++;
                        }
                    }
                    // MySQL allows names that start with digits (2026_marks): treat as a word.
                    if (i < n && IsWordChar(sql[i]) && !char.IsDigit(sql[i]))
                    {
                        while (i < n && IsWordChar(sql[i])) i++;
                        list.Add(new SqlToken(TokenKind.Word, start, i - start, sql.Substring(start, i - start)));
                    }
                    else list.Add(new SqlToken(TokenKind.Number, start, i - start, sql.Substring(start, i - start)));
                }
                else if (IsWordChar(c))
                {
                    // x'..' / b'..' / N'..' / _utf8mb4'..' literals
                    while (i < n && IsWordChar(sql[i])) i++;
                    if (i < n && sql[i] == '\'' && IsLiteralPrefix(sql.Substring(start, i - start)))
                    {
                        bool closed = ScanQuoted(sql, ref i, '\'', true);
                        list.Add(new SqlToken(TokenKind.String, start, i - start, sql.Substring(start, i - start), !closed));
                    }
                    else list.Add(new SqlToken(TokenKind.Word, start, i - start, sql.Substring(start, i - start)));
                }
                else
                {
                    string two = i + 1 < n ? sql.Substring(i, 2) : null;
                    string three = i + 2 < n ? sql.Substring(i, 3) : null;
                    if (three == "<=>") i += 3;
                    else if (two == "<=" || two == ">=" || two == "<>" || two == "!=" || two == ":=" || two == "||" || two == "&&" || two == "<<" || two == ">>" || two == "->") i += 2;
                    else i++;
                    list.Add(new SqlToken(TokenKind.Symbol, start, i - start, sql.Substring(start, i - start)));
                }
            }
            return list;
        }

        private static bool EndsName(List<SqlToken> list)
        {
            var t = list[list.Count - 1];
            return t.Kind == TokenKind.Word || t.Kind == TokenKind.QuotedIdent;
        }

        private static bool IsLiteralPrefix(string p) =>
            p.Equals("x", StringComparison.OrdinalIgnoreCase) || p.Equals("b", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("n", StringComparison.OrdinalIgnoreCase) || p.StartsWith("_", StringComparison.Ordinal);

        public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c > 127 && !char.IsWhiteSpace(c) && !char.IsPunctuation(c);

        /// <summary>Moves i past a quoted run. Returns false if it never closes.</summary>
        private static bool ScanQuoted(string s, ref int i, char q, bool backslashEscapes)
        {
            int n = s.Length;
            i++;
            while (i < n)
            {
                char c = s[i];
                if (backslashEscapes && c == '\\') { i += 2; continue; }
                if (c == q)
                {
                    if (i + 1 < n && s[i + 1] == q) { i += 2; continue; }   // '' or `` escape
                    i++;
                    return true;
                }
                i++;
            }
            i = n;
            return false;
        }

        /// <summary>Only the tokens that matter for meaning (no spaces or comments).</summary>
        public static List<SqlToken> Significant(IEnumerable<SqlToken> tokens)
        {
            var l = new List<SqlToken>();
            foreach (var t in tokens) if (t.IsSignificant) l.Add(t);
            return l;
        }

        /// <summary>A name that needs no backticks: letters/digits/_ , not starting with a digit, not reserved.</summary>
        public static bool IsPlainName(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsDigit(name[0])) return false;
            foreach (var c in name) if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '$'))) return false;
            return !SqlKeywords.Reserved.Contains(name);
        }

        public static string Quote(string identifier) => "`" + (identifier ?? "").Replace("`", "``") + "`";

        public static string StringLiteral(string s)
        {
            var sb = new StringBuilder("'");
            foreach (var c in s ?? "")
            {
                if (c == '\'') sb.Append("''");
                else if (c == '\\') sb.Append("\\\\");
                else sb.Append(c);
            }
            return sb.Append('\'').ToString();
        }
    }
}
