using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LuminaDB.Core.Mail
{
    public sealed class MailAttachment
    {
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public byte[] Data { get; set; }
        public string ContentId { get; set; }
        public string SizeText => DatabaseSummary.FormatSize(Data?.LongLength ?? 0);
    }

    /// <summary>
    /// A parsed email (RFC 5322 + MIME): headers with encoded words decoded, the text and HTML bodies, attachments.
    /// Handles what PHP's mail(), PHPMailer and Laravel send: multipart/alternative, mixed, related, base64 and
    /// quoted-printable, any charset .NET knows.
    /// </summary>
    public sealed class MimeMessage
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public List<KeyValuePair<string, string>> Headers { get; } = new List<KeyValuePair<string, string>>();
        public string Subject => Header("Subject") ?? "";
        public string From => Header("From") ?? Header("X-Lumina-Envelope-From") ?? "";
        public string To => Header("To") ?? Header("X-Lumina-Envelope-To") ?? "";
        public string Cc => Header("Cc") ?? "";
        public DateTime Received { get; private set; }
        public string TextBody { get; private set; }
        public string HtmlBody { get; private set; }
        public List<MailAttachment> Attachments { get; } = new List<MailAttachment>();
        public string Raw { get; private set; }
        public string FilePath { get; private set; }

        public string Header(string name) =>
            Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();

        /// <summary>The HTML to show: the HTML body with cid: images inlined, or the text body as HTML.</summary>
        public string DisplayHtml()
        {
            if (HtmlBody != null)
            {
                var html = HtmlBody;
                foreach (var a in Attachments.Where(a => !string.IsNullOrEmpty(a.ContentId) && a.Data != null))
                    html = html.Replace("cid:" + a.ContentId, "data:" + a.ContentType + ";base64," + Convert.ToBase64String(a.Data));
                return html;
            }
            var text = System.Net.WebUtility.HtmlEncode(TextBody ?? "");
            return "<pre style=\"white-space:pre-wrap;font:14px/1.5 Consolas,monospace\">" + text + "</pre>";
        }

        public static MimeMessage Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var m = Parse(bytes);
            m.FilePath = path;
            if (m.Received == default) m.Received = File.GetLastWriteTime(path);
            return m;
        }

        public static MimeMessage Parse(byte[] bytes)
        {
            var m = new MimeMessage { Raw = Latin1.GetString(bytes) };
            var part = MimePart.Parse(bytes);
            foreach (var h in part.Headers) m.Headers.Add(new KeyValuePair<string, string>(h.Key, DecodeWords(h.Value)));
            var received = m.Header("X-Lumina-Received");
            if (received != null && DateTime.TryParseExact(received, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r))
                m.Received = r;
            m.Collect(part);
            return m;
        }

        private void Collect(MimePart part)
        {
            var type = part.MediaType;
            if (type.StartsWith("multipart/", StringComparison.Ordinal))
            {
                foreach (var child in part.Children) Collect(child);
                return;
            }
            if (type == "message/rfc822")
            {
                Attachments.Add(new MailAttachment { FileName = part.FileName ?? "message.eml", ContentType = type, Data = part.Body });
                return;
            }
            bool attachment = part.Disposition == "attachment" || (part.FileName != null && part.Disposition != "inline") ||
                              (!type.StartsWith("text/", StringComparison.Ordinal) && part.Children.Count == 0);
            if (!attachment && type == "text/html" && HtmlBody == null) { HtmlBody = part.Text(); return; }
            if (!attachment && type == "text/plain" && TextBody == null) { TextBody = part.Text(); return; }
            Attachments.Add(new MailAttachment
            {
                FileName = part.FileName ?? (part.ContentId != null ? part.ContentId : "attachment") + Extension(type),
                ContentType = type,
                Data = part.Body,
                ContentId = part.ContentId,
            });
        }

        private static string Extension(string mediaType)
        {
            switch (mediaType)
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "application/pdf": return ".pdf";
                case "text/plain": return ".txt";
                case "text/html": return ".html";
                case "text/calendar": return ".ics";
                default: return ".bin";
            }
        }

        // ------------------------------------------------------------------ RFC 2047 / 2231

        private static readonly Regex EncodedWord = new Regex(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=", RegexOptions.Compiled);

        /// <summary>Decodes =?UTF-8?B?...?= and =?charset?Q?...?= words (whitespace between two words is dropped).</summary>
        public static string DecodeWords(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf("=?", StringComparison.Ordinal) < 0) return value;
            value = Regex.Replace(value, @"(\?=)\s+(=\?)", "$1$2");
            return EncodedWord.Replace(value, m =>
            {
                var enc = GetEncoding(m.Groups[1].Value);
                var data = m.Groups[3].Value;
                try
                {
                    byte[] bytes = char.ToUpperInvariant(m.Groups[2].Value[0]) == 'B'
                        ? Convert.FromBase64String(PadBase64(data))
                        : DecodeQuotedPrintable(Latin1.GetBytes(data.Replace('_', ' ')), header: true);
                    return enc.GetString(bytes);
                }
                catch (FormatException) { return m.Value; }
            });
        }

        private static string PadBase64(string s)
        {
            s = s.Trim();
            int r = s.Length % 4;
            return r == 0 ? s : s + new string('=', 4 - r);
        }

        internal static Encoding GetEncoding(string charset)
        {
            if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
            var c = charset.Trim().Trim('"');
            int star = c.IndexOf('*');   // RFC 2231 language suffix: utf-8*en
            if (star > 0) c = c.Substring(0, star);
            if (c.Equals("utf8", StringComparison.OrdinalIgnoreCase)) c = "utf-8";
            try { return Encoding.GetEncoding(c); }
            catch (ArgumentException) { return Encoding.UTF8; }
        }

        internal static byte[] DecodeQuotedPrintable(byte[] input, bool header = false)
        {
            var o = new MemoryStream(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                if (b == (byte)'=' )
                {
                    // Soft line break: "=" at the end of a line.
                    if (i + 1 < input.Length && (input[i + 1] == 13 || input[i + 1] == 10))
                    {
                        i++;
                        if (input[i] == 13 && i + 1 < input.Length && input[i + 1] == 10) i++;
                        continue;
                    }
                    if (i + 2 < input.Length && IsHex(input[i + 1]) && IsHex(input[i + 2]))
                    {
                        o.WriteByte((byte)(Hex(input[i + 1]) * 16 + Hex(input[i + 2])));
                        i += 2;
                        continue;
                    }
                }
                o.WriteByte(b);
            }
            return o.ToArray();
        }

        private static bool IsHex(byte b) => (b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') || (b >= 'a' && b <= 'f');
        private static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

        // ------------------------------------------------------------------ parts

        private sealed class MimePart
        {
            public List<KeyValuePair<string, string>> Headers { get; } = new List<KeyValuePair<string, string>>();
            public List<MimePart> Children { get; } = new List<MimePart>();
            public byte[] Body { get; private set; } = new byte[0];
            public string MediaType { get; private set; } = "text/plain";
            public string Charset { get; private set; }
            public string Disposition { get; private set; }
            public string FileName { get; private set; }
            public string ContentId { get; private set; }

            public string Text() => GetEncoding(Charset).GetString(Body);

            private string Get(string name) =>
                Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();

            public static MimePart Parse(byte[] data, int depth = 0)
            {
                var p = new MimePart();
                int bodyStart = SplitHeaders(data, p.Headers);
                var ct = ParseParams(p.Get("Content-Type") ?? "text/plain");
                p.MediaType = ct.Value.ToLowerInvariant();
                if (!p.MediaType.Contains("/")) p.MediaType = "text/plain";
                ct.Params.TryGetValue("charset", out var cs);
                p.Charset = cs;
                var cd = p.Get("Content-Disposition");
                if (cd != null)
                {
                    var d = ParseParams(cd);
                    p.Disposition = d.Value.ToLowerInvariant();
                    if (d.Params.TryGetValue("filename", out var fn)) p.FileName = DecodeWords(fn);
                }
                if (p.FileName == null && ct.Params.TryGetValue("name", out var n)) p.FileName = DecodeWords(n);
                var cid = p.Get("Content-ID");
                if (cid != null) p.ContentId = cid.Trim().Trim('<', '>');

                var raw = new byte[Math.Max(0, data.Length - bodyStart)];
                Array.Copy(data, bodyStart, raw, 0, raw.Length);

                if (p.MediaType.StartsWith("multipart/", StringComparison.Ordinal) && depth < 20 &&
                    ct.Params.TryGetValue("boundary", out var boundary) && boundary.Length > 0)
                {
                    foreach (var chunk in SplitMultipart(raw, boundary)) p.Children.Add(Parse(chunk, depth + 1));
                    return p;
                }
                var cte = (p.Get("Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant();
                if (cte == "base64")
                {
                    var s = new string(Latin1.GetString(raw).Where(c => !char.IsWhiteSpace(c)).ToArray());
                    try { p.Body = Convert.FromBase64String(PadBase64(s)); } catch (FormatException) { p.Body = raw; }
                }
                else if (cte == "quoted-printable") p.Body = DecodeQuotedPrintable(raw);
                else p.Body = raw;
                return p;
            }

            /// <summary>Reads (unfolded) header lines; returns where the body starts.</summary>
            private static int SplitHeaders(byte[] data, List<KeyValuePair<string, string>> headers)
            {
                int i = 0;
                string current = null;
                while (i < data.Length)
                {
                    int end = Array.IndexOf(data, (byte)10, i);
                    if (end < 0) end = data.Length;
                    int lineEnd = end > i && data[end - 1] == 13 ? end - 1 : end;
                    var line = Encoding.UTF8.GetString(data, i, lineEnd - i);
                    i = end + 1;
                    if (line.Length == 0) break;
                    if ((line[0] == ' ' || line[0] == '\t') && current != null) { current += " " + line.Trim(); continue; }
                    if (current != null) Add(headers, current);
                    current = line;
                }
                if (current != null) Add(headers, current);
                return Math.Min(i, data.Length);
            }

            private static void Add(List<KeyValuePair<string, string>> headers, string line)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) return;
                headers.Add(new KeyValuePair<string, string>(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()));
            }

            private static IEnumerable<byte[]> SplitMultipart(byte[] body, string boundary)
            {
                var text = Latin1.GetString(body);
                var marker = "--" + boundary;
                var parts = new List<byte[]>();
                int pos = IndexOfLine(text, marker, 0);
                while (pos >= 0)
                {
                    int start = text.IndexOf('\n', pos);
                    if (start < 0) break;
                    start++;
                    if (text.Length >= pos + marker.Length + 2 && text.Substring(pos + marker.Length, 2) == "--") break;
                    int next = IndexOfLine(text, marker, start);
                    int end = next < 0 ? text.Length : next;
                    // The CRLF before the next boundary belongs to the boundary.
                    if (end > start && text[end - 1] == '\n') end--;
                    if (end > start && text[end - 1] == '\r') end--;
                    parts.Add(Latin1.GetBytes(text.Substring(start, Math.Max(0, end - start))));
                    if (next < 0) break;
                    pos = next;
                    if (text.Length >= pos + marker.Length + 2 && text.Substring(pos + marker.Length, 2) == "--") break;
                }
                return parts;
            }

            private static int IndexOfLine(string text, string marker, int from)
            {
                int i = from;
                while (true)
                {
                    i = text.IndexOf(marker, i, StringComparison.Ordinal);
                    if (i < 0) return -1;
                    if (i == 0 || text[i - 1] == '\n') return i;
                    i += marker.Length;
                }
            }
        }

        private sealed class HeaderValue
        {
            public string Value;
            public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>"text/html; charset=\"utf-8\"; name*=UTF-8''r%C3%A9sum%C3%A9.pdf" → value plus parameters.</summary>
        private static HeaderValue ParseParams(string header)
        {
            var result = new HeaderValue();
            var pieces = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (var ch in header)
            {
                if (ch == '"') quoted = !quoted;
                if (ch == ';' && !quoted) { pieces.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(ch);
            }
            pieces.Add(sb.ToString());
            result.Value = pieces[0].Trim();
            var continued = new SortedDictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var piece in pieces.Skip(1))
            {
                int eq = piece.IndexOf('=');
                if (eq <= 0) continue;
                var key = piece.Substring(0, eq).Trim();
                var val = piece.Substring(eq + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"') val = val.Substring(1, val.Length - 2).Replace("\\\"", "\"");
                // RFC 2231: name*0*=..., name*1*=..., or name*=charset''percent-encoded
                var m = Regex.Match(key, @"^(?<k>[^*]+)\*(?<n>\d+)?\*?$");
                if (m.Success)
                {
                    var k = m.Groups["k"].Value;
                    int n = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 0;
                    if (!continued.TryGetValue(k, out var dict)) continued[k] = dict = new SortedDictionary<int, string>();
                    dict[n] = val;
                    continue;
                }
                result.Params[key] = val;
            }
            foreach (var kv in continued)
            {
                var joined = string.Concat(kv.Value.Values);
                var m = Regex.Match(joined, "^(?<cs>[^']*)'[^']*'(?<v>.*)$");
                if (m.Success)
                {
                    var bytes = PercentDecode(m.Groups["v"].Value);
                    result.Params[kv.Key] = GetEncoding(m.Groups["cs"].Value).GetString(bytes);
                }
                else result.Params[kv.Key] = joined;
            }
            return result;
        }

        private static byte[] PercentDecode(string s)
        {
            var o = new MemoryStream();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                {
                    o.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else o.WriteByte((byte)s[i]);
            }
            return o.ToArray();
        }
    }
}
