using System;
using System.IO;

namespace LuminaDB.Core.Import
{
    /// <summary>One command read from a script file.</summary>
    public sealed class ScriptStatement
    {
        public string Text { get; set; }
        /// <summary>1-based line of its first real character.</summary>
        public int Line { get; set; }
        public string Delimiter { get; set; }
        /// <summary>False for a last command with no delimiter after it.</summary>
        public bool Terminated { get; set; }
    }

    /// <summary>
    /// Reads commands from a script of any size without loading it whole: the same rules as the mysql client and
    /// <see cref="Sql.SqlSplitter"/> (';' or a DELIMITER-set marker outside strings, names and comments), but over a
    /// TextReader, keeping only the command being read in memory. /*!…*/ version comments count as commands;
    /// plain comments don't.
    /// </summary>
    public sealed class SqlStreamSplitter
    {
        private const int Chunk = 1 << 20;   // 1M chars at a time

        private enum Mode { Normal, Quote, LineComment, BlockComment }

        private readonly TextReader _reader;
        private char[] _b = new char[Chunk * 2];
        private int _len, _i, _start;
        private bool _eof;

        private Mode _mode = Mode.Normal;
        private char _quote;
        private string _delim = ";";
        private bool _atStart = true;      // only spaces and plain comments since the last command ended
        private bool _lineStart = true;    // only spaces since the last line break
        private int _line = 1, _stmtLine = 1;

        public SqlStreamSplitter(TextReader reader) { _reader = reader ?? throw new ArgumentNullException(nameof(reader)); }

        public string Delimiter => _delim;

        /// <summary>The next command, or null at the end of the script.</summary>
        public ScriptStatement Next()
        {
            while (true)
            {
                bool more = true;
                while (_i >= _len && (more = Fill())) { }   // an escape can step past the end of what was read
                if (!more)
                {
                    var last = Emit(_len, false);
                    _start = _len;
                    _i = _len;
                    return last;   // null when only blanks/comments were left
                }
                char c = _b[_i];
                switch (_mode)
                {
                    case Mode.Quote:
                        if (c == '\\' && _quote != '`') { _i += 2; continue; }   // may step past the end: Fill keeps _i
                        if (c == _quote)
                        {
                            if (Ahead(1) && _b[_i + 1] == _quote) { _i += 2; continue; }
                            _mode = Mode.Normal;
                        }
                        else if (c == '\n') _line++;
                        _i++;
                        continue;

                    case Mode.LineComment:
                        if (c == '\n') { _mode = Mode.Normal; _line++; _lineStart = true; }
                        _i++;
                        continue;

                    case Mode.BlockComment:
                        if (c == '*' && Ahead(1) && _b[_i + 1] == '/') { _mode = Mode.Normal; _i += 2; continue; }
                        if (c == '\n') _line++;
                        _i++;
                        continue;
                }

                // ---- normal text
                if (_atStart && _lineStart && (c == 'D' || c == 'd') && TryDelimiterLine()) continue;
                if (c == '\n') { _line++; _lineStart = true; _i++; continue; }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || char.IsWhiteSpace(c)) { _i++; continue; }

                if (c == '#') { _mode = Mode.LineComment; _i++; continue; }
                if (c == '-' && Ahead(1) && _b[_i + 1] == '-' && (!Ahead(2) || char.IsWhiteSpace(_b[_i + 2]) || char.IsControl(_b[_i + 2])))
                {
                    _mode = Mode.LineComment; _i += 2; continue;
                }
                if (c == '/' && Ahead(1) && _b[_i + 1] == '*' && !_delim.StartsWith("/*", StringComparison.Ordinal))
                {
                    bool version = Ahead(2) && (_b[_i + 2] == '!' || _b[_i + 2] == 'M' && Ahead(3) && _b[_i + 3] == '!');
                    if (version) Content();
                    _mode = Mode.BlockComment; _i += 2; _lineStart = false;
                    continue;
                }

                if (MatchesDelimiter())
                {
                    var s = Emit(_i, true);
                    _i += _delim.Length;
                    _start = _i;
                    _atStart = true;
                    _lineStart = false;
                    if (s != null) return s;
                    continue;
                }

                Content();
                _lineStart = false;
                if (c == '\'' || c == '"' || c == '`') { _mode = Mode.Quote; _quote = c; }
                _i++;
            }
        }

        /// <summary>The command's first real character: the text starts here, so plain comments before it are left out.</summary>
        private void Content()
        {
            if (_atStart) { _atStart = false; _stmtLine = _line; _start = _i; }
        }

        /// <summary>A "DELIMITER $$" line at the start of a command: consumed here, the server never sees it.</summary>
        private bool TryDelimiterLine()
        {
            const string word = "DELIMITER";
            if (!Ahead(word.Length)) return false;
            for (int k = 0; k < word.Length; k++) if (char.ToUpperInvariant(_b[_i + k]) != word[k]) return false;
            if (!char.IsWhiteSpace(_b[_i + word.Length])) return false;
            // the whole line must be in the buffer
            int end;
            while ((end = IndexOf('\n', _i)) < 0 && Fill()) { }
            if (end < 0) end = _len;
            var rest = new string(_b, _i + word.Length, end - _i - word.Length).Trim();
            int sp = rest.IndexOfAny(new[] { ' ', '\t', '\r' });
            if (sp >= 0) rest = rest.Substring(0, sp);
            if (rest.Length > 0) _delim = rest;
            _i = end;
            _start = _i;
            return true;
        }

        private bool MatchesDelimiter()
        {
            int n = _delim.Length;
            if (!Ahead(n - 1)) return false;
            bool wordy = Sql.SqlLexer.IsWordChar(_delim[0]);
            for (int k = 0; k < n; k++)
            {
                char a = _b[_i + k], d = _delim[k];
                if (a == d) continue;
                if (wordy && char.ToUpperInvariant(a) == char.ToUpperInvariant(d)) continue;
                return false;
            }
            return true;
        }

        private ScriptStatement Emit(int end, bool terminated)
        {
            if (_atStart || end <= _start) return null;   // nothing but blanks and comments
            int a = _start, b = Math.Min(end, _len);
            while (a < b && char.IsWhiteSpace(_b[a])) a++;
            while (b > a && char.IsWhiteSpace(_b[b - 1])) b--;
            if (b <= a) return null;
            return new ScriptStatement { Text = new string(_b, a, b - a), Line = _stmtLine, Delimiter = _delim, Terminated = terminated };
        }

        private int IndexOf(char ch, int from)
        {
            for (int k = from; k < _len; k++) if (_b[k] == ch) return k;
            return -1;
        }

        /// <summary>
        /// True when the character <paramref name="n"/> places after the current one is in the buffer, reading more if
        /// needed. Relative to _i on purpose: Fill moves the text to the front of the buffer, so an absolute index
        /// worked out before the call would point past the text and look like the end of the file.
        /// </summary>
        private bool Ahead(int n)
        {
            while (_i + n >= _len) if (!Fill()) return false;
            return true;
        }

        /// <summary>Drops what was already returned, then reads the next chunk. False at the end of the input.</summary>
        private bool Fill()
        {
            if (_eof) return false;
            if (_start > 0)
            {
                int keep = _len - _start;
                Array.Copy(_b, _start, _b, 0, keep);
                _i -= _start;
                _len = keep;
                _start = 0;
            }
            if (_b.Length - _len < Chunk)
            {
                var bigger = new char[Math.Max(_b.Length * 2, _len + Chunk)];
                Array.Copy(_b, bigger, _len);
                _b = bigger;
            }
            int read = _reader.Read(_b, _len, _b.Length - _len);
            if (read <= 0) { _eof = true; return false; }
            _len += read;
            return true;
        }
    }
}
