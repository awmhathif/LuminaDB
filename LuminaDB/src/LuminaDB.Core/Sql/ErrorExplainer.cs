using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LuminaDB.Core.Sql
{
    /// <summary>What went wrong, in words a first-year student understands.</summary>
    public sealed class ErrorExplanation
    {
        public int Code { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        /// <summary>The server's own words, kept for searching and for teachers.</summary>
        public string ServerMessage { get; set; }
        /// <summary>Script offset MariaDB pointed at ("near ... at line N"), or -1.</summary>
        public int Offset { get; set; } = -1;
        /// <summary>1-based script line, or 0.</summary>
        public int Line { get; set; }
        public string FixLabel { get; set; }
        public List<TextFix> Fixes { get; } = new List<TextFix>();
        public bool HasFix => Fixes.Count > 0;
        /// <summary>The server stopped or the connection dropped (not the user's SQL).</summary>
        public bool IsConnectionProblem { get; set; }
        public override string ToString() => $"#{Code} {Title}: {Body}";
    }

    public static class ErrorExplainer
    {
        private static Match M(string s, string pattern) => Regex.Match(s ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// Turns a MariaDB error into a title, an explanation and (when we can tell) a one-click fix.
        /// <paramref name="statement"/> is the command that failed, with script offsets.
        /// </summary>
        public static ErrorExplanation Explain(int code, string message, SqlStatement statement, SchemaSnapshot schema, string currentDb)
        {
            schema = schema ?? SchemaSnapshot.Empty;
            message = message ?? "";
            var e = new ErrorExplanation { Code = code, ServerMessage = message };
            var info = statement != null ? StatementInfo.Analyze(statement, currentDb) : null;
            Match m;

            switch (code)
            {
                // ------------------------------------------------ syntax & queries
                case 1064:
                    {
                        e.Title = "MariaDB couldn't read this command";
                        m = M(message, @"near '(.*)' at line (\d+)\s*$");
                        string near = m.Success ? m.Groups[1].Value : null;
                        int line = m.Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                        if (statement != null && m.Success) Locate(e, statement, near, line);

                        // our own checker usually knows the real reason
                        if (statement != null)
                        {
                            var guess = new SqlGuard(schema).Analyze(statement.Text, currentDb).Issues
                                .FirstOrDefault(i => i.Severity == IssueSeverity.Bad && i.Code != "table" && i.Code != "column");
                            if (guess != null)
                            {
                                e.Body = "Most likely: " + guess.Message;
                                if (guess.HasFix)
                                {
                                    e.FixLabel = guess.FixLabel;
                                    foreach (var f in guess.Fixes) e.Fixes.Add(new TextFix(f.Start + statement.Start, f.Length, f.Text));
                                }
                                if (e.Offset < 0) { e.Offset = statement.Start + guess.Start; e.Line = SqlStatement.LineOf(statement.Text, guess.Start) + statement.StartLine - 1; }
                                break;
                            }
                        }
                        if (near == null) e.Body = "Something in the command isn't valid SQL. Check spelling, commas and brackets.";
                        else if (near.Length == 0) e.Body = "The command ends too early: something is missing at the very end (a value, a bracket or a name).";
                        else
                        {
                            var firstWord = Regex.Match(near, @"^\s*([A-Za-z_]+)").Groups[1].Value;
                            string hint = "";
                            if (firstWord.Length > 0 && SqlKeywords.Reserved.Contains(firstWord) && PrecededByName(statement, e.Offset))
                                hint = " If " + firstWord + " is meant as a name, wrap it in backticks: `" + firstWord.ToLowerInvariant() + "`.";
                            else if (firstWord.Length > 0 && SqlKeywords.LineStarters.Contains(firstWord))
                                hint = " Check that the command before it ends with ;";
                            e.Body = "It got confused just before: " + Shorten(near) + hint +
                                     " The mistake is usually right there or at the end of the line above.";
                        }
                        break;
                    }
                case 1065:
                    e.Title = "Nothing to run";
                    e.Body = "The command is empty, or only a comment.";
                    break;
                case 1052:
                    m = M(message, @"Column '([^']+)' in (.+?) is ambiguous");
                    {
                        var col = m.Success ? m.Groups[1].Value : "this column";
                        var owners = info == null ? new List<string>() : info.Tables.Where(r => !r.IsDerived && r.Name != null)
                            .Where(r => schema.FindTable(r.ResolvedDatabase, r.Name)?.Find(col) != null)
                            .Select(r => r.Alias ?? r.Name).Distinct().ToList();
                        e.Title = col + " is in more than one table";
                        e.Body = owners.Count >= 2
                            ? "Both " + string.Join(" and ", owners) + " have a column called " + col + ". Say which one: " + string.Join(" or ", owners.Select(o => o + "." + col)) + "."
                            : "Several tables in this query have a column called " + col + ". Put the table name (or alias) in front: table." + col + ".";
                        if (owners.Count >= 1 && statement != null) AddReplaceFirstBare(e, statement, col, owners[0] + "." + col, "Use " + owners[0] + "." + col);
                    }
                    break;
                case 1111:
                    e.Title = "COUNT/SUM/AVG can't go in WHERE";
                    e.Body = "WHERE looks at one row at a time, before groups exist. Put conditions on totals in HAVING, after GROUP BY: ... GROUP BY x HAVING COUNT(*) > 2.";
                    break;
                case 1055:
                    e.Title = "Column isn't grouped";
                    e.Body = "With GROUP BY, every column you show must either be in the GROUP BY list or be inside COUNT/SUM/MAX/MIN/AVG.";
                    break;
                case 1242:
                    e.Title = "The sub-query returned several rows";
                    e.Body = "= expects exactly one value, but the inner SELECT returned more than one. Use IN (...) instead of =, or make the inner SELECT return one row (LIMIT 1, or a tighter WHERE).";
                    break;
                case 1241:
                    e.Title = "Too many columns in a comparison";
                    e.Body = "A sub-query used as a value must return exactly one column. Select just the column you compare.";
                    break;
                case 1630:
                case 1305:
                    m = M(message, @"(FUNCTION|PROCEDURE) ([^ ]+) does not exist");
                    {
                        var kind = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "function";
                        var name = m.Success ? m.Groups[2].Value : "";
                        var bare = name.Contains(".") ? name.Substring(name.LastIndexOf('.') + 1) : name;
                        if (SqlKeywords.Functions.Contains(bare))
                        {
                            e.Title = "Remove the space after " + bare.ToUpperInvariant();
                            e.Body = "Built-in functions must touch their bracket: " + bare.ToUpperInvariant() + "(...) works, " + bare.ToUpperInvariant() + " (...) doesn't.";
                            if (statement != null)
                            {
                                var tok = statement.Tokens;
                                for (int i = 0; i + 2 < tok.Count; i++)
                                    if (tok[i].IsWord(bare) && tok[i + 1].Kind == TokenKind.Whitespace && tok[i + 2].IsSymbol("("))
                                    {
                                        e.FixLabel = "Remove the space";
                                        e.Fixes.Add(new TextFix(tok[i + 1].Start, tok[i + 1].Length, ""));
                                        e.Offset = tok[i].Start;
                                        break;
                                    }
                            }
                        }
                        else
                        {
                            e.Title = "There's no " + kind + " called " + bare;
                            e.Body = "Check the spelling, and that you created it in this database. Stored " + kind + "s belong to one database.";
                        }
                    }
                    break;
                case 1093:
                    m = M(message, @"target table '([^']+)'");
                    e.Title = "Can't read and change the same table here";
                    e.Body = "MariaDB won't let a sub-query read " + (m.Success ? m.Groups[1].Value : "the table") + " while this command changes it. Wrap the sub-query in another one: WHERE id IN (SELECT id FROM (SELECT ...) AS t).";
                    break;
                case 1248:
                    e.Title = "The sub-query needs a name";
                    e.Body = "A (SELECT ...) used in FROM must be given an alias: FROM (SELECT ...) AS t.";
                    break;

                // ------------------------------------------------ missing/duplicate objects
                case 1146:
                    m = M(message, @"Table '([^'.]+)\.([^']+)' doesn't exist");
                    {
                        var db = m.Success ? m.Groups[1].Value : currentDb;
                        var table = m.Success ? m.Groups[2].Value : "";
                        var candidates = schema.Find(db)?.Tables.Keys.ToList() ?? new List<string>();
                        var guess = SqlKeywords.Closest(table, candidates);
                        e.Title = "There's no table called " + table;
                        e.Body = "The database " + db + " has no table " + table + "." + (guess != null ? " Did you mean " + guess + "?" :
                                 candidates.Count == 0 ? " It has no tables yet." : candidates.Count <= 8 ? " Its tables: " + string.Join(", ", candidates) + "." : "");
                        if (guess != null && statement != null) AddReplaceName(e, statement, table, guess, "Use " + guess);
                        else if (statement != null) PointAtName(e, statement, table);
                    }
                    break;
                case 1054:
                    m = M(message, @"Unknown column '([^']+)' in '([^']+)'");
                    {
                        var full = m.Success ? m.Groups[1].Value : "";
                        var where = m.Success ? m.Groups[2].Value : "";
                        string qual = null, col = full;
                        if (full.Contains(".")) { qual = full.Substring(0, full.LastIndexOf('.')); col = full.Substring(full.LastIndexOf('.') + 1); }
                        var columns = new List<string>();
                        string owner = null;
                        if (info != null)
                            foreach (var r in info.Tables.Where(r => !r.IsDerived && r.Name != null))
                            {
                                if (qual != null && !string.Equals(r.Alias ?? r.Name, qual, StringComparison.OrdinalIgnoreCase) && !string.Equals(r.Name, qual, StringComparison.OrdinalIgnoreCase)) continue;
                                var t = schema.FindTable(r.ResolvedDatabase, r.Name);
                                if (t == null) continue;
                                owner = owner ?? t.Name;
                                columns.AddRange(t.Columns.Select(c => c.Name));
                            }
                        var guess = SqlKeywords.Closest(col, columns.Distinct());
                        e.Title = "There's no column called " + col;
                        string place = PlaceName(where);
                        if (qual != null && owner == null)
                            e.Body = qual + " isn't a table or alias in this command, so " + full + " can't be found.";
                        else
                            e.Body = (owner != null ? owner : "The table") + " has no column " + col + (place != null ? " (used " + place + ")" : "") + "." +
                                     (guess != null ? " Did you mean " + guess + "?" : columns.Count > 0 && columns.Count <= 10 ? " Columns: " + string.Join(", ", columns.Distinct()) + "." : "");
                        if (where == "field list" && LooksLikeText(statement, col))
                            e.Body += " If " + col + " is meant as text, put it in single quotes: '" + col + "'. Double quotes \"...\" and backticks `...` mean names.";
                        if (guess != null && statement != null) AddReplaceName(e, statement, col, guess, "Use " + guess);
                        else if (statement != null) PointAtName(e, statement, col);
                    }
                    break;
                case 1049:
                    m = M(message, @"Unknown database '([^']+)'");
                    {
                        var db = m.Success ? m.Groups[1].Value : "";
                        var guess = SqlKeywords.Closest(db, schema.Databases.Keys);
                        e.Title = "There's no database called " + db;
                        e.Body = guess != null ? "Did you mean " + guess + "?" : "Create it first (CREATE DATABASE " + db + ";) or pick one from the list.";
                        if (guess != null && statement != null) AddReplaceName(e, statement, db, guess, "Use " + guess);
                    }
                    break;
                case 1046:
                    e.Title = "No database selected";
                    e.Body = "MariaDB doesn't know which database this table is in. Pick one in the toolbar, or put USE your_database; above this command.";
                    break;
                case 1007:
                    m = M(message, @"database '([^']+)'");
                    e.Title = "That database already exists";
                    e.Body = (m.Success ? m.Groups[1].Value : "It") + " is already there. Add IF NOT EXISTS to skip creating it, or use a different name.";
                    AddAfterWord(e, statement, "DATABASE", " IF NOT EXISTS", "Add IF NOT EXISTS");
                    break;
                case 1050:
                    m = M(message, @"Table '([^']+)' already exists");
                    e.Title = "That table already exists";
                    e.Body = (m.Success ? m.Groups[1].Value : "It") + " is already there. Add IF NOT EXISTS to skip it, or DROP TABLE it first if you want to start over.";
                    AddAfterWord(e, statement, "TABLE", " IF NOT EXISTS", "Add IF NOT EXISTS");
                    break;
                case 1060:
                    m = M(message, @"Duplicate column name '([^']+)'");
                    e.Title = "Two columns with the same name";
                    e.Body = "The column " + (m.Success ? m.Groups[1].Value : "") + " appears twice. Every column in a table needs its own name.";
                    break;
                case 1061:
                    m = M(message, @"Duplicate key name '([^']+)'");
                    e.Title = "That index name is taken";
                    e.Body = "The table already has an index called " + (m.Success ? m.Groups[1].Value : "that") + ". Pick another name.";
                    break;
                case 1091:
                    m = M(message, @"DROP (?:COLUMN|INDEX)? ?'([^']+)'");
                    e.Title = "Nothing to drop";
                    e.Body = "There's no column or index called " + (m.Success ? m.Groups[1].Value : "that") + " in this table. Check the name with DESCRIBE table;";
                    break;

                // ------------------------------------------------ data & constraints
                case 1062:
                    m = M(message, @"Duplicate entry '(.*)' for key '([^']+)'");
                    {
                        var value = m.Success ? m.Groups[1].Value : "";
                        var key = m.Success ? m.Groups[2].Value : "";
                        if (key.Contains(".")) key = key.Substring(key.LastIndexOf('.') + 1);
                        var target = info?.Targets.FirstOrDefault();
                        var t = target != null ? schema.FindTable(target.ResolvedDatabase, target.Name) : null;
                        string column = key == "PRIMARY" ? t?.PrimaryKey.FirstOrDefault()?.Name ?? "the primary key" : key;
                        e.Title = "That " + column + " is already used";
                        e.Body = "There's already a row with " + column + " = " + Shorten(value) + ", and " + column +
                                 (key == "PRIMARY" ? " is the primary key" : " must be unique") + ". Use another value" +
                                 (t?.PrimaryKey.Any(c => c.IsAutoIncrement) == true && key == "PRIMARY" ? ", or leave " + column + " out so MariaDB picks the next number." : ", or UPDATE the existing row instead.");
                    }
                    break;
                case 1452:
                case 1216:
                    m = M(message, @"FOREIGN KEY \(`([^`]+)`\) REFERENCES `([^`]+)` \(`([^`]+)`\)");
                    if (m.Success)
                    {
                        e.Title = "That " + m.Groups[1].Value + " doesn't exist in " + m.Groups[2].Value;
                        e.Body = m.Groups[1].Value + " must match an existing " + m.Groups[2].Value + "." + m.Groups[3].Value +
                                 ". Add that " + Singular(m.Groups[2].Value) + " first, or use an id that's already there (SELECT " + m.Groups[3].Value + " FROM " + m.Groups[2].Value + ";).";
                    }
                    else
                    {
                        e.Title = "The linked row doesn't exist";
                        e.Body = "A foreign key points to a row that isn't there. Add the parent row first.";
                    }
                    break;
                case 1451:
                case 1217:
                    m = M(message, @"`([^`]+)`\.`([^`]+)`, CONSTRAINT `[^`]+` FOREIGN KEY \(`([^`]+)`\) REFERENCES `([^`]+)`");
                    if (m.Success)
                    {
                        e.Title = "Other rows still point to this one";
                        e.Body = "Rows in " + m.Groups[2].Value + " use this " + Singular(m.Groups[4].Value) + " (column " + m.Groups[3].Value +
                                 "). Delete or change those rows first, or create the foreign key with ON DELETE CASCADE so they go together.";
                    }
                    else
                    {
                        e.Title = "Other rows still point to this one";
                        e.Body = "Another table refers to this row through a foreign key. Delete those rows first.";
                    }
                    break;
                case 1048:
                    m = M(message, @"Column '([^']+)' cannot be null");
                    e.Title = (m.Success ? m.Groups[1].Value : "This column") + " can't be empty";
                    e.Body = "The column " + (m.Success ? m.Groups[1].Value : "") + " is NOT NULL, so it needs a real value here.";
                    break;
                case 1364:
                    m = M(message, @"Field '([^']+)' doesn't have a default value");
                    {
                        var c = m.Success ? m.Groups[1].Value : "";
                        e.Title = "A value for " + c + " is missing";
                        e.Body = "You didn't give " + c + " a value, and it has no default. Add " + c + " to the column list and give it a value, or give the column a DEFAULT.";
                    }
                    break;
                case 1366:
                    m = M(message, @"Incorrect (\w+) value: '(.*)' for column (?:`[^`]+`\.){0,2}`?([^`' ]+)`? at row (\d+)");
                    if (m.Success)
                    {
                        var type = m.Groups[1].Value.ToLowerInvariant();
                        e.Title = "'" + Shorten(m.Groups[2].Value) + "' isn't a valid " + (type == "integer" || type == "decimal" || type == "double" ? "number" : type);
                        e.Body = "The column " + m.Groups[3].Value + " holds " + (type == "integer" ? "whole numbers" : type == "decimal" || type == "double" ? "numbers" : type + " values") +
                                 " (row " + m.Groups[4].Value + "). Check the values are in the same order as the columns.";
                    }
                    else { e.Title = "Wrong kind of value"; e.Body = "A value doesn't fit the column's type (text in a number column, for example)."; }
                    break;
                case 1406:
                    m = M(message, @"Data too long for column '([^']+)' at row (\d+)");
                    {
                        var c = m.Success ? m.Groups[1].Value : "";
                        var target = info?.Targets.FirstOrDefault();
                        var col = target != null ? schema.FindTable(target.ResolvedDatabase, target.Name)?.Find(c) : null;
                        e.Title = "Too long for " + c;
                        e.Body = "The text is longer than " + c + " allows" + (col?.MaxLength != null ? " (" + col.MaxLength + " characters)" : "") +
                                 ". Shorten it, or make the column bigger: ALTER TABLE " + (target?.Name ?? "t") + " MODIFY " + c + " VARCHAR(" + ((col?.MaxLength ?? 50) * 2) + ");";
                    }
                    break;
                case 1265:
                    m = M(message, @"Data truncated for column '([^']+)' at row (\d+)");
                    {
                        var c = m.Success ? m.Groups[1].Value : "";
                        var target = info?.Targets.FirstOrDefault();
                        var col = target != null ? schema.FindTable(target.ResolvedDatabase, target.Name)?.Find(c) : null;
                        e.Title = "That value doesn't fit " + c;
                        e.Body = col != null && col.EnumValues.Count > 0
                            ? c + " only accepts: " + string.Join(", ", col.EnumValues.Select(v => "'" + v + "'")) + " (spelling and spaces must match)."
                            : "The value is too long or the wrong kind for " + c + (col != null ? " (" + col.Type + ")" : "") + ".";
                    }
                    break;
                case 1292:
                    m = M(message, @"Incorrect (date|datetime|time|timestamp)\w* value: '(.*)' for column (?:`[^`]+`\.){0,2}`?([^`' ]+)`?");
                    if (m.Success)
                    {
                        var kind = m.Groups[1].Value.ToLowerInvariant();
                        e.Title = "'" + Shorten(m.Groups[2].Value) + "' isn't a valid " + kind;
                        e.Body = "Write dates as 'YYYY-MM-DD' (e.g. '2026-03-14')" + (kind != "date" ? " and times as 'YYYY-MM-DD HH:MM:SS'" : "") +
                                 ", in quotes. The month must be 01-12 and the day must exist in that month.";
                    }
                    else { e.Title = "Invalid date or time"; e.Body = "Write dates as 'YYYY-MM-DD', in quotes."; }
                    break;
                case 1136:
                    e.Title = "Number of values doesn't match the columns";
                    e.Body = "Each row in VALUES (...) must have exactly one value per column. List the columns you're filling: INSERT INTO t (a, b) VALUES (1, 2);";
                    break;
                case 1264:
                    m = M(message, @"Out of range value for column '([^']+)'");
                    e.Title = "Number too big for " + (m.Success ? m.Groups[1].Value : "the column");
                    e.Body = "The value doesn't fit the column's type. TINYINT goes to 127, INT to about 2 billion; use BIGINT or DECIMAL for bigger numbers. Phone numbers belong in VARCHAR.";
                    break;
                case 1690:
                    e.Title = "The result went below zero";
                    e.Body = "An UNSIGNED number can't be negative, so the subtraction failed. Use CAST(x AS SIGNED) - y, or check the larger value comes first.";
                    break;
                case 4025:
                    m = M(message, @"CONSTRAINT `([^`]+)` failed for `([^`]+)`\.`([^`]+)`");
                    e.Title = "A CHECK rule rejected the value";
                    e.Body = "The rule " + (m.Success ? m.Groups[1].Value + " on " + m.Groups[3].Value : "") + " says this value isn't allowed. Use SHOW CREATE TABLE to see the rule.";
                    break;

                // ------------------------------------------------ table definitions
                case 1068:
                    e.Title = "Only one primary key per table";
                    e.Body = "PRIMARY KEY appears more than once. For a key made of two columns, write it once at the end: PRIMARY KEY (a, b).";
                    break;
                case 1075:
                    e.Title = "AUTO_INCREMENT must be the key";
                    e.Body = "A table can have one AUTO_INCREMENT column, and it must be the PRIMARY KEY: id INT AUTO_INCREMENT PRIMARY KEY.";
                    break;
                case 1005:
                case 1215:
                case 150:
                    if (message.IndexOf("150", StringComparison.Ordinal) >= 0 || code == 1215 || message.IndexOf("foreign key", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        e.Title = "The foreign key can't be made";
                        e.Body = "Three usual causes: the two columns have different types (INT vs INT UNSIGNED, or different lengths); the parent column isn't a PRIMARY KEY or UNIQUE; or the parent table doesn't exist yet. Create the parent table first, with the same column type.";
                    }
                    else { e.Title = "The table can't be created"; e.Body = message; }
                    break;
                case 1067:
                    m = M(message, @"Invalid default value for '([^']+)'");
                    e.Title = "Bad DEFAULT for " + (m.Success ? m.Groups[1].Value : "a column");
                    e.Body = "The default must fit the column type. For dates use a real date or CURRENT_TIMESTAMP, never '0000-00-00'.";
                    break;
                case 1170:
                    e.Title = "TEXT columns need a length in an index";
                    e.Body = "Use VARCHAR(255) instead of TEXT for columns you index or make UNIQUE, or give a prefix: INDEX (col(100)).";
                    break;
                case 1071:
                    e.Title = "Index is too long";
                    e.Body = "utf8mb4 uses up to 4 bytes per character, so very long VARCHARs can't be fully indexed. Use VARCHAR(255) or smaller for indexed columns.";
                    break;
                case 1118:
                    e.Title = "Row too large";
                    e.Body = "The columns together are too big for one row. Change some VARCHAR(…) columns to TEXT.";
                    break;
                case 1101:
                    e.Title = "TEXT/BLOB can't have a DEFAULT here";
                    e.Body = "Remove the DEFAULT from the TEXT/BLOB column, or use VARCHAR if you need a default.";
                    break;

                // ------------------------------------------------ locks & connection
                case 1213:
                    e.Title = "Two transactions blocked each other";
                    e.Body = "MariaDB cancelled this one to break the deadlock. Nothing was changed by it: just run it again.";
                    break;
                case 1205:
                    e.Title = "Waited too long for a lock";
                    e.Body = "Another connection (another Notebook tab, your PHP page, or an open transaction) is holding these rows. Finish it with COMMIT or ROLLBACK, then run again.";
                    break;
                case 1317:
                    e.Title = "Stopped";
                    e.Body = "You stopped this command before it finished. If it was changing rows, nothing was half-done: MariaDB rolled it back.";
                    break;
                case 2013:
                case 2006:
                case 2002:
                case 2003:
                    e.Title = "Lost the connection to the server";
                    e.Body = "The database server stopped or restarted while this ran. Check the masthead; press Start if it's stopped, then run again.";
                    e.IsConnectionProblem = true;
                    break;

                // ------------------------------------------------ permissions
                case 1142:
                case 1143:
                    e.Title = "Not allowed";
                    e.Body = "The user this Notebook uses may not do that. " + Shorten(message, 160);
                    break;
                case 1044:
                    e.Title = "Not allowed to use that database";
                    e.Body = Shorten(message, 160);
                    break;
                case 1045:
                    e.Title = "Wrong password";
                    e.Body = "The saved root password no longer matches. Enter it once in Users.";
                    break;
                case 1524:
                    e.Title = "MySQL 8's login method";
                    e.Body = message != null && message.IndexOf("caching_sha2", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "caching_sha2_password is MySQL 8's way of storing passwords; MariaDB doesn't have it. Create the user with IDENTIFIED BY 'password' instead (Users does this for you)."
                        : "MariaDB doesn't have that login plugin. Create the user with IDENTIFIED BY 'password' instead.";
                    break;
                case 1396:
                    e.Title = "That user already exists, or doesn't";
                    e.Body = "CREATE USER fails when the user is already there, DROP USER when it isn't. The Users page lists every account.";
                    break;
                case 1227:
                    e.Title = "Needs a stronger account";
                    e.Body = Shorten(message, 160);
                    break;

                default:
                    e.Title = "MariaDB refused this";
                    e.Body = message;
                    break;
            }

            if (e.Offset < 0 && statement != null && code != 1064) PointAtFirstOfFixes(e);
            if (e.Offset >= 0 && e.Line == 0 && statement != null) e.Line = statement.StartLine + CountLines(statement.Text, e.Offset - statement.Start);
            if (e.Line == 0 && statement != null) e.Line = statement.StartLine;
            return e;
        }

        private static int CountLines(string text, int upTo)
        {
            int n = 0;
            for (int i = 0; i < upTo && i < text.Length; i++) if (text[i] == '\n') n++;
            return n;
        }

        private static void PointAtFirstOfFixes(ErrorExplanation e)
        {
            if (e.Fixes.Count > 0) e.Offset = e.Fixes[0].Start;
        }

        /// <summary>Finds the "near '...'" text inside the statement, starting at the given line.</summary>
        private static void Locate(ErrorExplanation e, SqlStatement s, string near, int line)
        {
            var text = s.Text;
            int lineStart = 0;
            for (int l = 1; l < line && lineStart < text.Length; l++)
            {
                int nl = text.IndexOf('\n', lineStart);
                if (nl < 0) break;
                lineStart = nl + 1;
            }
            int idx = -1;
            if (!string.IsNullOrEmpty(near))
            {
                // the server cuts the quote at ~80 chars and may have normalised line ends
                var probe = near.Length > 40 ? near.Substring(0, 40) : near;
                probe = probe.Replace("\r", "");
                idx = text.Replace("\r", "\u0001").IndexOf(probe, lineStart, StringComparison.Ordinal);
                if (idx < 0) idx = text.IndexOf(probe.Split('\n')[0], lineStart, StringComparison.Ordinal);
            }
            else idx = text.TrimEnd().Length;
            if (idx < 0) idx = lineStart;
            e.Offset = s.Start + idx;
            e.Line = s.StartLine + CountLines(text, idx);
        }

        private static bool PrecededByName(SqlStatement s, int offset)
        {
            if (s == null || offset < 0) return false;
            var prev = s.SignificantTokens.LastOrDefault(t => t.End <= offset);
            return prev.Kind == TokenKind.Word && (prev.IsWord("FROM") || prev.IsWord("TABLE") || prev.IsWord("INTO") || prev.IsWord("JOIN") || prev.IsWord("UPDATE")) ||
                   prev.IsSymbol(",") || prev.IsSymbol("(");
        }

        private static string PlaceName(string where)
        {
            switch ((where ?? "").ToLowerInvariant())
            {
                case "field list": return "in the SELECT list or SET";
                case "where clause": return "in WHERE";
                case "order clause": return "in ORDER BY";
                case "group statement": return "in GROUP BY";
                case "having clause": return "in HAVING";
                case "on clause": return "in ON";
                default: return null;
            }
        }

        private static bool LooksLikeText(SqlStatement s, string col)
        {
            if (s == null) return false;
            foreach (var t in s.Tokens)
                if (t.Kind == TokenKind.String && t.Text.StartsWith("\"", StringComparison.Ordinal) || t.Kind == TokenKind.QuotedIdent)
                    if (string.Equals(t.IdentName.Trim('"'), col, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void AddReplaceName(ErrorExplanation e, SqlStatement s, string wrong, string right, string label)
        {
            foreach (var t in s.Tokens)
            {
                if (!(t.Kind == TokenKind.Word || t.Kind == TokenKind.QuotedIdent)) continue;
                if (!string.Equals(t.IdentName, wrong, StringComparison.OrdinalIgnoreCase)) continue;
                e.FixLabel = label;
                e.Fixes.Add(new TextFix(t.Start, t.Length, t.Kind == TokenKind.QuotedIdent || !SqlLexer.IsPlainName(right) ? SqlLexer.Quote(right) : right));
                e.Offset = t.Start;
                return;
            }
        }

        private static void AddReplaceFirstBare(ErrorExplanation e, SqlStatement s, string col, string replacement, string label)
        {
            var tok = s.SignificantTokens;
            for (int i = 0; i < tok.Count; i++)
            {
                var t = tok[i];
                if (!(t.Kind == TokenKind.Word || t.Kind == TokenKind.QuotedIdent) || !string.Equals(t.IdentName, col, StringComparison.OrdinalIgnoreCase)) continue;
                if (i > 0 && tok[i - 1].IsSymbol(".")) continue;
                if (i + 1 < tok.Count && tok[i + 1].IsSymbol(".")) continue;
                e.FixLabel = label;
                e.Fixes.Add(new TextFix(t.Start, t.Length, replacement));
                e.Offset = t.Start;
                return;
            }
        }

        private static void PointAtName(ErrorExplanation e, SqlStatement s, string name)
        {
            foreach (var t in s.Tokens)
                if ((t.Kind == TokenKind.Word || t.Kind == TokenKind.QuotedIdent) && string.Equals(t.IdentName, name, StringComparison.OrdinalIgnoreCase))
                { e.Offset = t.Start; return; }
        }

        private static void AddAfterWord(ErrorExplanation e, SqlStatement s, string word, string insert, string label)
        {
            if (s == null) return;
            var tok = s.SignificantTokens;
            for (int i = 0; i < tok.Count && i < 6; i++)
            {
                if (!tok[i].IsWord(word)) continue;
                if (i + 1 < tok.Count && tok[i + 1].IsWord("IF")) return;
                e.FixLabel = label;
                e.Fixes.Add(new TextFix(tok[i].End, 0, insert));
                e.Offset = tok[i].Start;
                return;
            }
        }

        private static string Singular(string table)
        {
            if (table.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) return table.Substring(0, table.Length - 3) + "y";
            if (table.EndsWith("ses", StringComparison.OrdinalIgnoreCase)) return table.Substring(0, table.Length - 2);
            if (table.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !table.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) return table.Substring(0, table.Length - 1);
            return table;
        }

        private static string Shorten(string s, int max = 60)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
