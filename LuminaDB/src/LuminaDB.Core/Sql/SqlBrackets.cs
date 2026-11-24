using System;
using System.Collections.Generic;

namespace LuminaDB.Core.Sql
{
    /// <summary>
    /// Round brackets in SQL text, for the editor: which "(" are still open at a point (auto-indent) and which bracket
    /// pairs with which (bracket matching). Brackets inside 'strings', "strings", `names` and comments don't count, and a
    /// ";" outside them ends the statement, so a forgotten ")" never shifts the statements after it.
    /// </summary>
    public static class SqlBrackets
    {
        /// <summary>Offsets of the "(" still open just before <paramref name="end"/>, outermost first.</summary>
        public static List<int> OpenBefore(string text, int end)
        {
            var open = new List<int>();
            if (string.IsNullOrEmpty(text)) return open;
            end = Math.Max(0, Math.Min(end, text.Length));
            Walk(text, end, (i, c) =>
            {
                if (c == '(') open.Add(i);
                else if (c == ')') { if (open.Count > 0) open.RemoveAt(open.Count - 1); }
                else if (c == ';') open.Clear();
                return true;
            });
            return open;
        }

        /// <summary>The innermost "(" still open just before <paramref name="end"/>, or -1.</summary>
        public static int InnermostOpen(string text, int end)
        {
            var open = OpenBefore(text, end);
            return open.Count == 0 ? -1 : open[open.Count - 1];
        }

        /// <summary>
        /// For the bracket at <paramref name="offset"/>: the offset of its partner, or -1 when it has none in its
        /// statement. Null when there is no code bracket there (another character, or a bracket inside a string or comment).
        /// </summary>
        public static int? Partner(string text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset < 0 || offset >= text.Length) return null;
            char at = text[offset];
            if (at != '(' && at != ')') return null;

            bool isCode = false;
            int result = -1;
            var open = new List<int>();
            if (at == ')')
            {
                Walk(text, offset + 1, (i, c) =>
                {
                    if (i == offset) { isCode = true; result = open.Count > 0 ? open[open.Count - 1] : -1; return false; }
                    if (c == '(') open.Add(i);
                    else if (c == ')') { if (open.Count > 0) open.RemoveAt(open.Count - 1); }
                    else if (c == ';') open.Clear();
                    return true;
                });
                return isCode ? result : (int?)null;
            }

            // "(": find where it sits, then the ")" that empties the stack back to it
            int depthAtTarget = -1;
            Walk(text, text.Length, (i, c) =>
            {
                if (c == '(')
                {
                    if (i == offset) { isCode = true; depthAtTarget = open.Count; }
                    open.Add(i);
                }
                else if (c == ')')
                {
                    if (open.Count > 0)
                    {
                        open.RemoveAt(open.Count - 1);
                        if (isCode && open.Count == depthAtTarget) { result = i; return false; }
                    }
                }
                else if (c == ';')
                {
                    if (isCode) return false;   // the statement ended with our "(" still open
                    open.Clear();
                }
                return true;
            });
            return isCode ? result : (int?)null;
        }

        /// <summary>
        /// Calls <paramref name="code"/> for every character before <paramref name="end"/> that is plain SQL (not inside a
        /// string, quoted name or comment). Stops early when it returns false.
        /// </summary>
        private static void Walk(string t, int end, Func<int, char, bool> code)
        {
            int n = t.Length, i = 0;
            while (i < end)
            {
                char c = t[i];
                if (c == '\'' || c == '"' || c == '`')
                {
                    char q = c;
                    i++;
                    while (i < n)
                    {
                        if (t[i] == '\\' && q != '`') { i += 2; continue; }
                        if (t[i] == q)
                        {
                            if (i + 1 < n && t[i + 1] == q) { i += 2; continue; }   // '' inside a string
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }
                if (c == '#' || (c == '-' && i + 1 < n && t[i + 1] == '-' && (i + 2 >= n || char.IsWhiteSpace(t[i + 2]))))
                {
                    while (i < n && t[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && t[i + 1] == '*')
                {
                    int close = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? n : close + 2;
                    continue;
                }
                if (!code(i, c)) return;
                i++;
            }
        }
    }
}
