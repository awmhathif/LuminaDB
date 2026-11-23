using System;
using System.Collections.Generic;
using System.Text;

namespace LuminaDB.Core.Sql
{
    /// <summary>
    /// "Tidy up": capital keywords, one clause per line, indented sub-queries. Never touches
    /// strings, names or comments, never adds a space before "(" (COUNT (*) breaks in MariaDB),
    /// and leaves DELIMITER lines and the text between commands exactly as written.
    /// </summary>
    public static class SqlFormatter
    {
        private static readonly HashSet<string> BreakBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "WHERE", "HAVING", "LIMIT", "VALUES", "UNION", "EXCEPT", "INTERSECT", "RETURNING", "WINDOW",
        };

        private static readonly HashSet<string> JoinLead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "NATURAL", "STRAIGHT_JOIN", "FULL",
        };

        public static string Format(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return script ?? "";
            var statements = SqlSplitter.Split(script);
            var sb = new StringBuilder(script.Length + 64);
            int pos = 0;
            foreach (var s in statements)
            {
                sb.Append(script, pos, s.Start - pos);
                sb.Append(FormatStatement(s));
                pos = s.End;
            }
            sb.Append(script, pos, script.Length - pos);
            return sb.ToString();
        }

        public static string FormatStatement(SqlStatement s)
        {
            var tokens = SqlLexer.Tokenize(s.Text);
            string verb = s.Verb;
            bool routine = IsRoutine(tokens);
            var sb = new StringBuilder(s.Text.Length + 32);
            int depth = 0;                  // sub-query nesting (for indent)
            var parens = new Stack<bool>(); // true = this ( opens a sub-query
            string clause = verb;
            bool betweenOpen = false;
            bool pendingNewline = false;
            SqlToken? prev = null;          // previous significant token
            bool hadSpace = false;          // whitespace (or comment) since prev
            bool lastWasLineComment = false;

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.Whitespace)
                {
                    hadSpace = true;
                    if (routine) sb.Append(t.Text);   // procedures: keep the author's layout
                    continue;
                }
                if (routine)
                {
                    sb.Append(t.Kind == TokenKind.Word && SqlKeywords.IsUppercaseKeyword(t.Text) && !IsQualified(tokens, i) ? t.Upper : t.Text);
                    continue;
                }
                if (t.Kind == TokenKind.Comment)
                {
                    if (sb.Length > 0 && !EndsWithNewline(sb)) sb.Append(lastWasLineComment ? "\n" : " ");
                    sb.Append(t.Text.TrimEnd('\r', '\n'));
                    lastWasLineComment = !t.Text.StartsWith("/*", StringComparison.Ordinal);
                    hadSpace = true;
                    if (lastWasLineComment) pendingNewline = true;
                    continue;
                }

                string text = t.Text;
                bool newline = pendingNewline;
                pendingNewline = false;
                lastWasLineComment = false;
                int indentDepth = depth;
                bool clauseLevel = parens.Count == 0 || parens.Peek();

                if (t.Kind == TokenKind.Word && !IsQualified(tokens, i) && !clauseLevel)
                {
                    if (SqlKeywords.IsUppercaseKeyword(t.Upper) || SqlKeywords.Functions.Contains(t.Upper) && NextIsParen(tokens, i)) text = t.Upper;
                }
                else if (t.Kind == TokenKind.Word && !IsQualified(tokens, i))
                {
                    string up = t.Upper;
                    var prevUp = prev.HasValue && prev.Value.Kind == TokenKind.Word ? prev.Value.Upper : "";
                    if (SqlKeywords.IsUppercaseKeyword(up) || SqlKeywords.Functions.Contains(up) && NextIsParen(tokens, i)) text = up;

                    if (BreakBefore.Contains(up) && !(up == "VALUES" && prev.HasValue && (prev.Value.IsSymbol("=") || prev.Value.IsSymbol(",") || prev.Value.IsSymbol("(")))) newline = prev.HasValue;
                    else if ((up == "GROUP" || up == "ORDER" || up == "PARTITION") && NextWordIs(tokens, i, "BY") && !InsideWindow(tokens, i)) newline = prev.HasValue;
                    else if (JoinLead.Contains(up) && !JoinLead.Contains(prevUp) && !string.Equals(prevUp, "OUTER", StringComparison.OrdinalIgnoreCase) && LeadsToJoin(tokens, i)) newline = prev.HasValue;
                    else if (up == "SET" && (verb == "UPDATE" || verb == "INSERT" || verb == "REPLACE") && parens.Count == 0) { newline = prev.HasValue; clause = "UPDATE_SET"; }
                    else if (up == "ON" && NextWordIs(tokens, i, "DUPLICATE")) newline = true;
                    else if (up == "SELECT" && prev.HasValue && prev.Value.IsSymbol("(") == false && parens.Count == 0 && verb != "SELECT" && verb != "WITH" && prevUp != "UNION" && prevUp != "ALL" && prevUp != "DISTINCT") newline = true;
                    else if ((up == "AND" || up == "OR") && (clause == "WHERE" || clause == "HAVING") && !betweenOpen) { newline = true; }

                    if (up == "BETWEEN") betweenOpen = true;
                    else if (up == "AND" && betweenOpen) betweenOpen = false;
                    if (up == "WHERE" || up == "HAVING" || up == "FROM" || up == "LIMIT" || up == "SELECT") clause = up;
                    else if ((up == "GROUP" || up == "ORDER") && NextWordIs(tokens, i, "BY")) clause = up;
                }

                if (t.IsSymbol(")") && parens.Count > 0)
                {
                    if (parens.Pop()) depth = Math.Max(0, depth - 1);
                    indentDepth = depth;
                }

                if (sb.Length > 0)
                {
                    if (newline)
                    {
                        TrimTrailingSpaces(sb);
                        if (!EndsWithNewline(sb)) sb.Append('\n');
                        sb.Append(' ', (indentDepth * 4) + (IsAndOr(t) ? 2 : 0));
                    }
                    else if (NeedsSpace(prev, t, hadSpace)) sb.Append(' ');
                }
                sb.Append(text);
                if (t.IsSymbol("("))
                {
                    bool sub = NextWordIs(tokens, i, "SELECT") || NextWordIs(tokens, i, "WITH");
                    parens.Push(sub);
                    if (sub) depth++;
                }
                prev = t;
                hadSpace = false;
            }
            TrimTrailingSpaces(sb);
            return sb.ToString();
        }

        private static bool IsAndOr(SqlToken t) => t.IsWord("AND") || t.IsWord("OR");

        private static bool NeedsSpace(SqlToken? prevTok, SqlToken t, bool hadSpace)
        {
            if (!prevTok.HasValue) return false;
            var p = prevTok.Value;
            if (t.IsSymbol(",") || t.IsSymbol(";") || t.IsSymbol(")") || t.IsSymbol(".")) return false;
            if (p.IsSymbol("(") || p.IsSymbol(".") || p.IsSymbol("@")) return false;
            if (t.IsSymbol("(")) return hadSpace;     // keep "name(" vs "name (" exactly as written
            if (p.IsSymbol(",")) return true;
            if (!hadSpace)
            {
                // tight operators the author wrote without spaces (a=1) get spaces; other tight pairs stay tight
                if (p.Kind == TokenKind.Symbol && IsSpacedOperator(p.Text)) return true;
                if (t.Kind == TokenKind.Symbol && IsSpacedOperator(t.Text)) return true;
                return false;
            }
            return true;
        }

        private static bool IsSpacedOperator(string s) =>
            s == "=" || s == "<>" || s == "!=" || s == "<=" || s == ">=" || s == "<" || s == ">" || s == "<=>" || s == ":=" || s == "||" || s == "&&";

        private static bool IsQualified(List<SqlToken> tokens, int i)
        {
            for (int k = i - 1; k >= 0; k--)
            {
                if (tokens[k].Kind == TokenKind.Whitespace) continue;
                return tokens[k].IsSymbol(".");
            }
            return false;
        }

        private static bool NextIsParen(List<SqlToken> tokens, int i) => i + 1 < tokens.Count && tokens[i + 1].IsSymbol("(");

        private static bool NextWordIs(List<SqlToken> tokens, int i, string word)
        {
            for (int k = i + 1; k < tokens.Count; k++)
            {
                if (!tokens[k].IsSignificant) continue;
                return tokens[k].IsWord(word);
            }
            return false;
        }

        private static bool LeadsToJoin(List<SqlToken> tokens, int i)
        {
            for (int k = i; k < tokens.Count && k < i + 8; k++)
            {
                if (!tokens[k].IsSignificant) continue;
                if (tokens[k].IsWord("JOIN") || tokens[k].IsWord("STRAIGHT_JOIN")) return true;
                if (tokens[k].Kind != TokenKind.Word || !(JoinLead.Contains(tokens[k].Text) || tokens[k].IsWord("OUTER"))) return false;
            }
            return false;
        }

        /// <summary>ORDER BY / PARTITION BY inside OVER ( ... ) stays on its line.</summary>
        private static bool InsideWindow(List<SqlToken> tokens, int i)
        {
            int depth = 0;
            for (int k = i - 1; k >= 0; k--)
            {
                var t = tokens[k];
                if (t.IsSymbol(")")) depth++;
                else if (t.IsSymbol("("))
                {
                    if (depth == 0)
                    {
                        for (int j = k - 1; j >= 0; j--)
                        {
                            if (!tokens[j].IsSignificant) continue;
                            return tokens[j].IsWord("OVER") || tokens[j].Kind == TokenKind.Word && SqlKeywords.Functions.Contains(tokens[j].Text);
                        }
                        return false;
                    }
                    depth--;
                }
            }
            return false;
        }

        private static bool IsRoutine(List<SqlToken> tokens)
        {
            int seen = 0;
            foreach (var t in tokens)
            {
                if (!t.IsSignificant) continue;
                if (t.IsWord("TRIGGER") || t.IsWord("PROCEDURE") || t.IsWord("FUNCTION") || t.IsWord("EVENT") || t.IsWord("PACKAGE")) return true;
                if (seen == 0 && (t.IsWord("BEGIN") || t.IsWord("IF") || t.IsWord("WHILE") || t.IsWord("LOOP") || t.IsWord("REPEAT") || t.IsWord("CASE"))) return true;
                if (++seen > 8) return false;
            }
            return false;
        }

        private static bool EndsWithNewline(StringBuilder sb) => sb.Length > 0 && sb[sb.Length - 1] == '\n';

        private static void TrimTrailingSpaces(StringBuilder sb)
        {
            int k = sb.Length;
            while (k > 0 && sb[k - 1] == ' ') k--;
            sb.Length = k;
        }
    }
}
