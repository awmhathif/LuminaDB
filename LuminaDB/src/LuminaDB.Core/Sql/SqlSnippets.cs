using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace LuminaDB.Core.Sql
{
    /// <summary>
    /// A short word that becomes a whole statement: type <c>sel</c>, press Tab, get SELECT … FROM … WHERE
    /// with the cursor in the first blank (SPEC §5.1 "saved snippets").
    /// </summary>
    [DataContract]
    public sealed class SqlSnippet
    {
        public SqlSnippet() { }

        public SqlSnippet(string trigger, string title, string body, bool builtIn = false)
        {
            Trigger = trigger;
            Title = title;
            Body = body;
            IsBuiltIn = builtIn;
        }

        /// <summary>The word typed before Tab, like <c>sel</c>.</summary>
        [DataMember] public string Trigger { get; set; }
        /// <summary>What it makes, shown in the list: "SELECT rows".</summary>
        [DataMember] public string Title { get; set; }
        /// <summary>The SQL. <c>${1:table_name}</c> is a blank Tab jumps to; the same number again repeats it; <c>${0}</c> is where the cursor ends.</summary>
        [DataMember] public string Body { get; set; }

        [IgnoreDataMember] public bool IsBuiltIn { get; internal set; }

        /// <summary>The SQL with every blank filled with its example text (for previews and tooltips).</summary>
        public string Preview => SnippetTemplate.PlainText(Body);
    }

    public enum SnippetPartKind { Text, Field, Caret }

    public sealed class SnippetPart
    {
        public SnippetPart(SnippetPartKind kind, string text, int index)
        {
            Kind = kind;
            Text = text ?? "";
            Index = index;
        }

        public SnippetPartKind Kind { get; }
        /// <summary>Literal text, or a blank's example text.</summary>
        public string Text { get; }
        /// <summary>The blank's number (1–99). A number that appears twice is one blank shown in two places.</summary>
        public int Index { get; }

        public override string ToString() => Kind + ":" + Index + ":" + Text;
    }

    /// <summary>
    /// Reads snippet bodies. Only <c>${n}</c> and <c>${n:text}</c> are special, so ordinary SQL
    /// (DELIMITER $$, names with $ in them) can be saved as a snippet exactly as written.
    /// </summary>
    public static class SnippetTemplate
    {
        public static List<SnippetPart> Parse(string body)
        {
            var parts = new List<SnippetPart>();
            var text = new StringBuilder();
            var defaults = new Dictionary<int, string>();
            bool caret = false;
            body = (body ?? "").Replace("\r\n", "\n");
            // first pass: the first text given for each number, so every copy of a blank shows it
            for (int k = 0; k < body.Length; k++)
                if (TryField(body, k, out int n, out string v, out int e))
                {
                    if (n > 0 && v != null && !defaults.ContainsKey(n)) defaults[n] = v;
                    k = e - 1;
                }
            int i = 0;
            while (i < body.Length)
            {
                if (TryField(body, i, out int index, out string value, out int end))
                {
                    if (text.Length > 0) { parts.Add(new SnippetPart(SnippetPartKind.Text, text.ToString(), 0)); text.Clear(); }
                    if (index == 0)
                    {
                        // one end position; a second ${0} is just dropped
                        if (!caret) parts.Add(new SnippetPart(SnippetPartKind.Caret, "", 0));
                        caret = true;
                    }
                    else
                    {
                        parts.Add(new SnippetPart(SnippetPartKind.Field, defaults.TryGetValue(index, out var d) ? d : "", index));
                    }
                    i = end;
                    continue;
                }
                text.Append(body[i]);
                i++;
            }
            if (text.Length > 0) parts.Add(new SnippetPart(SnippetPartKind.Text, text.ToString(), 0));
            return parts;
        }

        /// <summary>The body with each blank showing its example text.</summary>
        public static string PlainText(string body) =>
            string.Concat(Parse(body).Where(p => p.Kind != SnippetPartKind.Caret).Select(p => p.Text));

        /// <summary>How many different blanks Tab moves through.</summary>
        public static int FieldCount(string body) =>
            Parse(body).Where(p => p.Kind == SnippetPartKind.Field).Select(p => p.Index).Distinct().Count();

        private static bool TryField(string s, int i, out int index, out string value, out int end)
        {
            index = 0; value = null; end = i;
            if (s[i] != '$' || i + 1 >= s.Length || s[i + 1] != '{') return false;
            int j = i + 2, start = j;
            while (j < s.Length && char.IsDigit(s[j]) && j - start < 2) j++;
            if (j == start || j >= s.Length) return false;
            index = int.Parse(s.Substring(start, j - start), System.Globalization.CultureInfo.InvariantCulture);
            if (s[j] == '}') { end = j + 1; return true; }
            if (s[j] != ':') return false;
            int close = s.IndexOf('}', j + 1);
            if (close < 0) return false;
            value = s.Substring(j + 1, close - j - 1);
            if (value.IndexOf('\n') >= 0) return false;   // a blank is one line; anything else is SQL text
            end = close + 1;
            return true;
        }
    }

    /// <summary>The snippets that come with LuminaDB: the statements students write most, MariaDB syntax.</summary>
    public static class SqlSnippets
    {
        public static readonly IReadOnlyList<SqlSnippet> BuiltIn = new List<SqlSnippet>
        {
            new SqlSnippet("sel", "SELECT rows", "SELECT ${2:*}\nFROM ${1:table_name}\nWHERE ${3:id = 1};${0}", true),
            new SqlSnippet("selo", "SELECT, sorted, first rows", "SELECT ${2:*}\nFROM ${1:table_name}\nORDER BY ${3:id} DESC\nLIMIT ${4:10};${0}", true),
            new SqlSnippet("selj", "SELECT with a JOIN", "SELECT ${3:a.*, b.*}\nFROM ${1:table_a} a\nJOIN ${2:table_b} b ON b.${4:id} = a.${5:table_b_id}\nWHERE ${6:a.id = 1};${0}", true),
            new SqlSnippet("selg", "Count per group", "SELECT ${2:column_name}, COUNT(*) AS total\nFROM ${1:table_name}\nGROUP BY ${2}\nHAVING COUNT(*) > ${3:1}\nORDER BY total DESC;${0}", true),
            new SqlSnippet("cnt", "Count the rows", "SELECT COUNT(*) FROM ${1:table_name};${0}", true),
            new SqlSnippet("ins", "INSERT a row", "INSERT INTO ${1:table_name} (${2:name, email})\nVALUES (${3:'Nimal', 'nimal@example.com'});${0}", true),
            new SqlSnippet("insm", "INSERT several rows", "INSERT INTO ${1:table_name} (${2:name, email}) VALUES\n  (${3:'Nimal', 'nimal@example.com'}),\n  (${4:'Kamala', 'kamala@example.com'});${0}", true),
            new SqlSnippet("upd", "UPDATE rows", "UPDATE ${1:table_name}\nSET ${2:column_name} = ${3:'new value'}\nWHERE ${4:id = 1};${0}", true),
            new SqlSnippet("del", "DELETE rows", "DELETE FROM ${1:table_name}\nWHERE ${2:id = 1};${0}", true),
            new SqlSnippet("cdb", "CREATE DATABASE and use it", "CREATE DATABASE IF NOT EXISTS ${1:database_name}\n  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;\nUSE ${1};\n${0}", true),
            new SqlSnippet("ct", "CREATE TABLE", "CREATE TABLE ${1:table_name} (\n  id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,\n  ${2:name VARCHAR(100) NOT NULL},\n  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP\n);${0}", true),
            new SqlSnippet("ctfk", "CREATE TABLE with a foreign key", "CREATE TABLE ${1:child_table} (\n  id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,\n  ${2:parent_table}_id INT UNSIGNED NOT NULL,\n  ${3:name VARCHAR(100) NOT NULL},\n  CONSTRAINT fk_${1}_${2} FOREIGN KEY (${2}_id) REFERENCES ${2} (id) ON DELETE ${4:CASCADE}\n);${0}", true),
            new SqlSnippet("addc", "Add a column", "ALTER TABLE ${1:table_name}\n  ADD COLUMN ${2:column_name} ${3:VARCHAR(100) NULL} AFTER ${4:id};${0}", true),
            new SqlSnippet("fk", "Add a foreign key", "ALTER TABLE ${1:child_table}\n  ADD CONSTRAINT fk_${1}_${2:parent_table} FOREIGN KEY (${3:parent_table_id}) REFERENCES ${2} (id) ON DELETE ${4:CASCADE};${0}", true),
            new SqlSnippet("idx", "Add an index", "CREATE INDEX idx_${1:table_name}_${2:column_name} ON ${1} (${2});${0}", true),
            new SqlSnippet("cview", "CREATE VIEW", "CREATE OR REPLACE VIEW ${1:view_name} AS\nSELECT ${3:*}\nFROM ${2:table_name}\nWHERE ${4:id > 0};${0}", true),
            new SqlSnippet("tx", "A transaction", "START TRANSACTION;\n${1:UPDATE table_name SET column_name = 1 WHERE id = 1;}\nCOMMIT;${0}", true),
            new SqlSnippet("proc", "A stored procedure", "DELIMITER //\nCREATE PROCEDURE ${1:procedure_name}(IN ${2:p_id} INT)\nBEGIN\n  ${3:SELECT * FROM table_name WHERE id = p_id;}\nEND //\nDELIMITER ;\nCALL ${1}(${4:1});${0}", true),
            new SqlSnippet("cuser", "A login for a PHP app", "CREATE USER '${1:app_user}'@'localhost' IDENTIFIED BY '${2:choose-a-password}';\nGRANT ALL PRIVILEGES ON ${3:database_name}.* TO '${1}'@'localhost';${0}", true),
        };

        public const int MaxTriggerLength = 20;

        /// <summary>Every word autocomplete offers (keywords, types, functions): a snippet word must not be one of them.</summary>
        public static readonly HashSet<string> SqlWords = new HashSet<string>(SqlKeywords.CompletionWords(), StringComparer.OrdinalIgnoreCase);

        /// <summary>Why <paramref name="trigger"/> can't be used, or null if it can. A trigger of one of <paramref name="mine"/> is fine: saving replaces it.</summary>
        public static string TriggerProblem(string trigger)
        {
            var t = (trigger ?? "").Trim();
            if (t.Length == 0) return "Type the word that brings the snippet in, like myq.";
            if (t.Length < 2) return "Use at least 2 letters, so it doesn't pop up while you type.";
            if (t.Length > MaxTriggerLength) return "Keep it to " + MaxTriggerLength + " letters or fewer.";
            if (!char.IsLetter(t[0]) || t[0] > 'z') return "Start it with a letter (a to z).";
            if (!t.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '_')) return "Use letters, digits and _ only, with no spaces.";
            var b = BuiltIn.FirstOrDefault(s => string.Equals(s.Trigger, t, StringComparison.OrdinalIgnoreCase));
            if (b != null) return t + " is already the built-in snippet for " + b.Title + ". Pick another word.";
            if (SqlWords.Contains(t)) return t.ToUpperInvariant() + " is an SQL word, so Tab after it would surprise you. Pick another word.";
            return null;
        }
    }

    /// <summary>Your own snippets, kept in config\snippets.json next to the built-in ones.</summary>
    public sealed class SnippetStore
    {
        [DataContract]
        private sealed class SnippetFile
        {
            [DataMember] public List<SqlSnippet> Snippets { get; set; } = new List<SqlSnippet>();
        }

        private readonly string _path;
        private readonly List<SqlSnippet> _mine;

        public SnippetStore(string path)
        {
            _path = path;
            var file = path == null ? null : JsonFile.Read<SnippetFile>(path);
            _mine = (file?.Snippets ?? new List<SqlSnippet>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Trigger) && !string.IsNullOrEmpty(s.Body))
                .GroupBy(s => s.Trigger.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
                .Where(s => !SqlSnippets.BuiltIn.Any(b => string.Equals(b.Trigger, s.Trigger.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var s in _mine) { s.Trigger = s.Trigger.Trim(); s.IsBuiltIn = false; if (string.IsNullOrWhiteSpace(s.Title)) s.Title = s.Trigger; }
        }

        public event Action Changed;

        public IReadOnlyList<SqlSnippet> Mine => _mine.OrderBy(s => s.Trigger, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Built-in first, then yours.</summary>
        public IReadOnlyList<SqlSnippet> All => SqlSnippets.BuiltIn.Concat(Mine).ToList();

        public SqlSnippet Find(string trigger) =>
            string.IsNullOrEmpty(trigger) ? null : All.FirstOrDefault(s => string.Equals(s.Trigger, trigger, StringComparison.OrdinalIgnoreCase));

        public bool HasMine(string trigger) => _mine.Any(s => string.Equals(s.Trigger, (trigger ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds a snippet, or replaces yours with the same word. Throws if the word can't be used.</summary>
        public SqlSnippet Save(string trigger, string title, string body)
        {
            var problem = SqlSnippets.TriggerProblem(trigger);
            if (problem != null) throw new LuminaException(ErrorKind.Unknown, problem);
            body = (body ?? "").Replace("\r\n", "\n").TrimEnd();
            if (body.Trim().Length == 0) throw new LuminaException(ErrorKind.Unknown, "The snippet has no SQL in it.");
            trigger = trigger.Trim();
            title = string.IsNullOrWhiteSpace(title) ? FirstLine(body) : title.Trim();
            _mine.RemoveAll(s => string.Equals(s.Trigger, trigger, StringComparison.OrdinalIgnoreCase));
            var snippet = new SqlSnippet(trigger, title, body);
            _mine.Add(snippet);
            Persist();
            return snippet;
        }

        public bool Delete(string trigger)
        {
            int n = _mine.RemoveAll(s => string.Equals(s.Trigger, trigger, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Persist();
            return n > 0;
        }

        private void Persist()
        {
            if (_path != null) JsonFile.Write(_path, new SnippetFile { Snippets = Mine.ToList() });
            Changed?.Invoke();
        }

        private static string FirstLine(string body)
        {
            var line = SnippetTemplate.PlainText(body).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)) ?? "My snippet";
            return line.Length > 48 ? line.Substring(0, 47) + "…" : line;
        }
    }
}
