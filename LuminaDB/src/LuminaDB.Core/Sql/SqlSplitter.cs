using System;
using System.Collections.Generic;

namespace LuminaDB.Core.Sql
{
    /// <summary>One command from a script, with its place in the original text.</summary>
    public sealed class SqlStatement
    {
        public SqlStatement(string script, int start, int length, string delimiter, bool terminated)
        {
            Start = start;
            Length = length;
            Text = script.Substring(start, length);
            Delimiter = delimiter;
            Terminated = terminated;
            StartLine = LineOf(script, start);
        }

        public string Text { get; }
        /// <summary>Offset of the first non-space character in the script.</summary>
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        /// <summary>1-based line in the script.</summary>
        public int StartLine { get; }
        /// <summary>The delimiter in force (";" or what DELIMITER set).</summary>
        public string Delimiter { get; }
        /// <summary>False when the script ends without a delimiter after this command.</summary>
        public bool Terminated { get; }

        private List<SqlToken> _tokens;
        /// <summary>Tokens of this statement with offsets relative to the SCRIPT.</summary>
        public List<SqlToken> Tokens
        {
            get
            {
                if (_tokens != null) return _tokens;
                var raw = SqlLexer.Tokenize(Text);
                _tokens = new List<SqlToken>(raw.Count);
                foreach (var t in raw) _tokens.Add(new SqlToken(t.Kind, t.Start + Start, t.Length, t.Text, t.Unterminated));
                return _tokens;
            }
        }

        public List<SqlToken> SignificantTokens => SqlLexer.Significant(Tokens);

        /// <summary>The first word, upper-case ("" if none).</summary>
        public string Verb
        {
            get
            {
                foreach (var t in Tokens)
                    if (t.IsSignificant) return t.Kind == TokenKind.Word ? t.Upper : "";
                return "";
            }
        }

        /// <summary>First line of the command, shortened, for receipts.</summary>
        public string Headline(int max = 80)
        {
            var s = Text.Replace("\r", "");
            int nl = s.IndexOf('\n');
            var first = (nl >= 0 ? s.Substring(0, nl) : s).Trim();
            if (nl >= 0 && first.Length < max) first += " …";
            return first.Length > max ? first.Substring(0, max - 1).TrimEnd() + "…" : first;
        }

        public static int LineOf(string text, int offset)
        {
            int line = 1;
            for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        public override string ToString() => Text;
    }

    /// <summary>
    /// Splits a script into commands the way the mysql client does: ';' (or a DELIMITER-set
    /// marker like $$ or //) outside strings, names and comments ends a command. DELIMITER
    /// lines are consumed here because the server itself doesn't understand them.
    /// </summary>
    public static class SqlSplitter
    {
        public static List<SqlStatement> Split(string script)
        {
            var list = new List<SqlStatement>();
            if (string.IsNullOrEmpty(script)) return list;
            string delim = ";";
            int n = script.Length;
            int i = 0;
            int stmtStart = 0;
            bool atStatementStart = true;   // only spaces/comments seen since the last command ended

            while (i < n)
            {
                char c = script[i];

                if (atStatementStart && IsLineStart(script, i) && StartsWithWord(script, i, "DELIMITER"))
                {
                    int lineEnd = script.IndexOf('\n', i);
                    if (lineEnd < 0) lineEnd = n;
                    var rest = script.Substring(i + 9, lineEnd - i - 9).Trim();
                    int sp = IndexOfWhite(rest);
                    if (sp >= 0) rest = rest.Substring(0, sp);
                    if (rest.Length > 0) delim = rest;
                    i = lineEnd;
                    stmtStart = i;
                    continue;
                }

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '#' || (c == '-' && i + 1 < n && script[i + 1] == '-' && (i + 2 >= n || char.IsWhiteSpace(script[i + 2]) || char.IsControl(script[i + 2]))))
                {
                    while (i < n && script[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && script[i + 1] == '*' && !(delim.StartsWith("/*", StringComparison.Ordinal)))
                {
                    int close = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? n : close + 2;
                    continue;
                }

                if (Matches(script, i, delim))
                {
                    Add(list, script, stmtStart, i, delim, true);
                    i += delim.Length;
                    stmtStart = i;
                    atStatementStart = true;
                    continue;
                }

                atStatementStart = false;
                if (c == '\'' || c == '"' || c == '`')
                {
                    i = SkipQuoted(script, i, c);
                    continue;
                }
                i++;
            }
            Add(list, script, stmtStart, n, delim, false);
            return list;
        }

        /// <summary>The command the caret is in (or just after). Null for an empty script.</summary>
        public static SqlStatement StatementAt(IList<SqlStatement> statements, int caret)
        {
            if (statements == null || statements.Count == 0) return null;
            SqlStatement best = statements[0];
            foreach (var s in statements)
            {
                if (s.Start <= caret) best = s;
                else break;
            }
            return best;
        }

        private static void Add(List<SqlStatement> list, string script, int from, int to, string delim, bool terminated)
        {
            int a = from, b = to;
            while (a < b && char.IsWhiteSpace(script[a])) a++;
            while (b > a && char.IsWhiteSpace(script[b - 1])) b--;
            if (b <= a) return;
            // skip leading plain comments so the command starts at its first real word;
            // /*!40101 ... */ version comments (mysqldump) ARE commands and are kept
            foreach (var t in SqlLexer.Tokenize(script.Substring(a, b - a)))
            {
                if (t.Kind == TokenKind.Whitespace || t.Kind == TokenKind.Comment && !IsVersionComment(t.Text)) continue;
                int start = a + t.Start;
                list.Add(new SqlStatement(script, start, b - start, delim, terminated));
                return;
            }
            // comment-only chunk: nothing to run
        }

        public static bool IsVersionComment(string text) =>
            text.StartsWith("/*!", StringComparison.Ordinal) || text.StartsWith("/*M!", StringComparison.Ordinal);

        private static bool Matches(string s, int i, string delim)
        {
            if (i + delim.Length > s.Length) return false;
            bool wordy = SqlLexer.IsWordChar(delim[0]);
            if (string.Compare(s, i, delim, 0, delim.Length, wordy ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) != 0) return false;
            return true;
        }

        private static bool IsLineStart(string s, int i)
        {
            for (int k = i - 1; k >= 0; k--)
            {
                if (s[k] == '\n') return true;
                if (!char.IsWhiteSpace(s[k])) return false;
            }
            return true;
        }

        private static bool StartsWithWord(string s, int i, string word)
        {
            if (i + word.Length > s.Length) return false;
            if (string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            return i + word.Length == s.Length || char.IsWhiteSpace(s[i + word.Length]);
        }

        private static int IndexOfWhite(string s)
        {
            for (int k = 0; k < s.Length; k++) if (char.IsWhiteSpace(s[k])) return k;
            return -1;
        }

        private static int SkipQuoted(string s, int i, char q)
        {
            int n = s.Length;
            i++;
            while (i < n)
            {
                char c = s[i];
                if (q != '`' && c == '\\') { i += 2; continue; }
                if (c == q)
                {
                    if (i + 1 < n && s[i + 1] == q) { i += 2; continue; }
                    return i + 1;
                }
                i++;
            }
            return n;
        }
    }
}
