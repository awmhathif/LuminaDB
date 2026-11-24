using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LuminaDB.Core.Mail
{
    /// <summary>
    /// A tiny SMTP server on 127.0.0.1 (SPEC §5.11, replaces XAMPP's Mercury). php.ini's SMTP/smtp_port point at it,
    /// so every mail() a project sends lands in mail\ as an .eml file and in the Mail tab. Nothing leaves the PC.
    /// </summary>
    public sealed class MailCatcher : IDisposable
    {
        public const int MaxMessageBytes = 25 * 1024 * 1024;
        private readonly AppPaths _paths;
        private readonly PortManager _ports;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private int _counter;

        public MailCatcher(AppPaths paths, PortManager ports = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _ports = ports ?? new PortManager();
        }

        /// <summary>Raised on a worker thread with the saved .eml path.</summary>
        public event Action<string> MessageReceived;

        public int Port { get; private set; }
        public bool IsRunning => _listener != null;
        public string Folder => _paths.Mail;

        public void Start(int preferredPort = 2525, ICollection<int> taken = null)
        {
            if (IsRunning) return;
            Directory.CreateDirectory(_paths.Mail);
            var st = LuminaState.Load(_paths);
            Port = _ports.Pick(preferredPort, st.MailPort, taken);
            var l = new TcpListener(IPAddress.Loopback, Port) { ExclusiveAddressUse = true };
            l.Start();
            _listener = l;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoop(l, ct));
            st = LuminaState.Load(_paths);
            st.MailPort = Port;
            st.Save(_paths);
        }

        public void Stop()
        {
            var l = _listener;
            _listener = null;
            _cts?.Cancel();
            try { l?.Stop(); } catch (SocketException) { }
            _cts?.Dispose();
            _cts = null;
        }

        private async Task AcceptLoop(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
                catch (InvalidOperationException) { return; }
                _ = Task.Run(() => Serve(client, ct));
            }
        }

        private async Task Serve(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.ReceiveTimeout = 60000;
                client.SendTimeout = 60000;
                var stream = client.GetStream();
                var reader = new SmtpLineReader(stream);
                var latin1 = Encoding.GetEncoding(28591);
                async Task Say(string line)
                {
                    var b = latin1.GetBytes(line + "\r\n");
                    await stream.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
                }

                string from = null;
                var to = new List<string>();
                try
                {
                    await Say("220 lumina.localhost LuminaDB mail catcher ready (nothing leaves this PC)").ConfigureAwait(false);
                    while (!ct.IsCancellationRequested)
                    {
                        var raw = await reader.ReadLineAsync(4096, ct).ConfigureAwait(false);
                        if (raw == null) return;
                        var line = latin1.GetString(raw);
                        var verb = (line.Length >= 4 ? line.Substring(0, 4) : line).ToUpperInvariant();
                        switch (verb)
                        {
                            case "EHLO":
                                await Say("250-lumina.localhost").ConfigureAwait(false);
                                await Say("250-SIZE " + MaxMessageBytes.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                                await Say("250-8BITMIME").ConfigureAwait(false);
                                await Say("250 SMTPUTF8").ConfigureAwait(false);
                                break;
                            case "HELO":
                                await Say("250 lumina.localhost").ConfigureAwait(false);
                                break;
                            case "MAIL":
                                from = Address(line);
                                to.Clear();
                                await Say("250 OK").ConfigureAwait(false);
                                break;
                            case "RCPT":
                                if (from == null) { await Say("503 Send MAIL FROM first").ConfigureAwait(false); break; }
                                to.Add(Address(line));
                                await Say("250 OK").ConfigureAwait(false);
                                break;
                            case "DATA":
                                if (to.Count == 0) { await Say("503 Send RCPT TO first").ConfigureAwait(false); break; }
                                await Say("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                                var body = await ReadData(reader, ct).ConfigureAwait(false);
                                if (body == null) { await Say("552 Message is bigger than 25 MB").ConfigureAwait(false); return; }
                                var file = Save(from, to, body);
                                await Say("250 OK: kept as " + Path.GetFileName(file)).ConfigureAwait(false);
                                try { MessageReceived?.Invoke(file); } catch { }
                                from = null;
                                to.Clear();
                                break;
                            case "RSET":
                                from = null;
                                to.Clear();
                                await Say("250 OK").ConfigureAwait(false);
                                break;
                            case "NOOP":
                                await Say("250 OK").ConfigureAwait(false);
                                break;
                            case "QUIT":
                                await Say("221 Bye").ConfigureAwait(false);
                                return;
                            case "AUTH":
                                // Projects copied from real hosting often log in; accept anything, it's all local.
                                await Say("235 Authentication accepted (LuminaDB keeps all mail on this PC)").ConfigureAwait(false);
                                break;
                            case "STAR": // STARTTLS
                                await Say("454 TLS not available; mail stays on this PC").ConfigureAwait(false);
                                break;
                            default:
                                await Say("502 Command not implemented").ConfigureAwait(false);
                                break;
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            }
        }

        /// <summary>Reads DATA up to the lone "." line, undoing dot-stuffing. Null if it is too big.</summary>
        private static async Task<byte[]> ReadData(SmtpLineReader reader, CancellationToken ct)
        {
            var ms = new MemoryStream();
            bool tooBig = false;
            while (true)
            {
                var line = await reader.ReadLineAsync(1000 * 1000, ct).ConfigureAwait(false);
                if (line == null) throw new IOException("Connection closed during DATA.");
                if (line.Length == 1 && line[0] == (byte)'.') break;
                int start = line.Length > 0 && line[0] == (byte)'.' ? 1 : 0;
                if (ms.Length + line.Length > MaxMessageBytes) { tooBig = true; continue; }
                ms.Write(line, start, line.Length - start);
                ms.WriteByte(13);
                ms.WriteByte(10);
            }
            return tooBig ? null : ms.ToArray();
        }

        private string Save(string from, List<string> to, byte[] body)
        {
            var stamp = DateTime.Now;
            int n = Interlocked.Increment(ref _counter);
            var name = stamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + n.ToString("D3", CultureInfo.InvariantCulture) + ".eml";
            var path = Path.Combine(_paths.Mail, name);
            var envelope = "X-Lumina-Received: " + stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" +
                           "X-Lumina-Envelope-From: " + (from ?? "") + "\r\n" +
                           "X-Lumina-Envelope-To: " + string.Join(", ", to) + "\r\n";
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var head = Encoding.ASCII.GetBytes(envelope);
                fs.Write(head, 0, head.Length);
                fs.Write(body, 0, body.Length);
            }
            return path;
        }

        /// <summary>"MAIL FROM:&lt;a@b&gt; SIZE=12" → "a@b".</summary>
        internal static string Address(string line)
        {
            int colon = line.IndexOf(':');
            var rest = colon < 0 ? "" : line.Substring(colon + 1).Trim();
            int lt = rest.IndexOf('<'), gt = rest.IndexOf('>');
            if (lt >= 0 && gt > lt) return rest.Substring(lt + 1, gt - lt - 1).Trim();
            int sp = rest.IndexOf(' ');
            return sp < 0 ? rest : rest.Substring(0, sp);
        }

        public void Dispose() => Stop();

        /// <summary>Reads CRLF (or LF) terminated lines as bytes, so 8-bit mail bodies are kept exactly.</summary>
        private sealed class SmtpLineReader
        {
            private readonly Stream _s;
            private readonly byte[] _buf = new byte[8192];
            private int _pos, _len;

            public SmtpLineReader(Stream s) { _s = s; }

            public async Task<byte[]> ReadLineAsync(int max, CancellationToken ct)
            {
                var line = new MemoryStream();
                while (true)
                {
                    if (_pos >= _len)
                    {
                        _len = await _s.ReadAsync(_buf, 0, _buf.Length, ct).ConfigureAwait(false);
                        _pos = 0;
                        if (_len <= 0) return line.Length > 0 ? line.ToArray() : null;
                    }
                    byte b = _buf[_pos++];
                    if (b == 10)
                    {
                        var arr = line.ToArray();
                        int n = arr.Length > 0 && arr[arr.Length - 1] == 13 ? arr.Length - 1 : arr.Length;
                        if (n == arr.Length) return arr;
                        var trimmed = new byte[n];
                        Array.Copy(arr, trimmed, n);
                        return trimmed;
                    }
                    if (line.Length < max) line.WriteByte(b);
                }
            }
        }
    }

    /// <summary>The caught messages in mail\ (newest first).</summary>
    public static class MailStore
    {
        public static List<string> List(AppPaths paths) =>
            Directory.Exists(paths.Mail)
                ? Directory.GetFiles(paths.Mail, "*.eml").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
                : new List<string>();

        public static void Delete(string file)
        {
            if (File.Exists(file)) File.Delete(file);
        }

        public static int DeleteAll(AppPaths paths)
        {
            int n = 0;
            foreach (var f in List(paths)) { try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return n;
        }
    }
}
