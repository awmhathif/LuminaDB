using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Mail;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One caught message in the list (headers only; the body loads when it is opened).</summary>
    public sealed class MailItem : ObservableObject
    {
        private bool _selected;
        private bool _unread;

        public MailItem(string path, MimeMessage m, bool unread)
        {
            FilePath = path;
            Subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
            From = m.From;
            To = m.To;
            Received = m.Received;
            HasAttachments = m.Attachments.Count > 0;
            var text = m.TextBody ?? StripTags(m.HtmlBody ?? "");
            Preview = Regex.Replace(text, @"\s+", " ").Trim();
            if (Preview.Length > 120) Preview = Preview.Substring(0, 120) + "…";
            _unread = unread;
        }

        public string FilePath { get; }
        public string Subject { get; }
        public string From { get; }
        public string To { get; }
        public DateTime Received { get; }
        public string When => Received.Date == DateTime.Today
            ? Received.ToString("HH:mm", CultureInfo.InvariantCulture)
            : Received.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        public string Preview { get; }
        public bool HasAttachments { get; }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        public bool IsUnread { get => _unread; set => Set(ref _unread, value); }

        internal static string StripTags(string html) =>
            System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]+>", " "));
    }

    public sealed class AttachmentRow
    {
        public AttachmentRow(MailAttachment a) { Attachment = a; }
        public MailAttachment Attachment { get; }
        public string FileName => string.IsNullOrEmpty(Attachment.FileName) ? "(unnamed)" : Attachment.FileName;
        public string Detail => Attachment.ContentType + " · " + Attachment.SizeText;
    }

    /// <summary>
    /// The Mail section (SPEC §5.11): every message a project sends with mail() lands here instead of the internet.
    /// </summary>
    public sealed class MailViewModel : ObservableObject, IDisposable
    {
        public const int MaxMessages = 500;
        private readonly MainViewModel _main;
        private readonly AppPaths _paths;
        private readonly Dispatcher _ui;
        private readonly MailCatcher _catcher;
        private MailItem _selected;
        private MimeMessage _message;
        private string _tab = "html";
        private bool _shown;

        public MailViewModel(MainViewModel main, AppPaths paths, Dispatcher ui, MailCatcher catcher)
        {
            _main = main;
            _paths = paths;
            _ui = ui;
            _catcher = catcher;
            foreach (var k in new[] { "html", "text", "source", "files" }) Tabs.Add(new PageChip(k, TabTitle(k)));
            Tabs[0].IsSelected = true;

            SelectCommand = new RelayCommand(p => Open(p as MailItem));
            SelectTabCommand = new RelayCommand(p => Tab = (p as PageChip)?.Key ?? "html");
            DeleteCommand = new RelayCommand(Delete, () => Selected != null);
            DeleteAllCommand = new AsyncCommand(DeleteAllAsync, () => Messages.Count > 0);
            OpenFolderCommand = new RelayCommand(() =>
            {
                Directory.CreateDirectory(_paths.Mail);
                try { Process.Start("explorer.exe", "\"" + _paths.Mail + "\"")?.Dispose(); }
                catch (Exception ex) { _main.Toast("Couldn't open the folder: " + ex.Message); }
            });
            SaveAttachmentCommand = new RelayCommand(p => SaveAttachment(p as AttachmentRow), p => p is AttachmentRow);
            CopyAddressCommand = new RelayCommand(() => _main.CopyText(SmtpLine, "Copied the SMTP settings."), () => _catcher?.IsRunning == true);

            if (_catcher != null) _catcher.MessageReceived += path => _ui.BeginInvoke(new Action(() => OnReceived(path)));
            Reload();
        }

        public ObservableCollection<MailItem> Messages { get; } = new ObservableCollection<MailItem>();
        public ObservableCollection<PageChip> Tabs { get; } = new ObservableCollection<PageChip>();
        public ObservableCollection<AttachmentRow> Attachments { get; } = new ObservableCollection<AttachmentRow>();
        public ICommand SelectCommand { get; }
        public ICommand SelectTabCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand DeleteAllCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand SaveAttachmentCommand { get; }
        public ICommand CopyAddressCommand { get; }

        /// <summary>Raised when the HTML preview must be redrawn (the view owns the WebBrowser).</summary>
        public event Action HtmlChanged;

        public bool HasMessages => Messages.Count > 0;
        public bool NoMessages => Messages.Count == 0;
        public bool HasSelection => _message != null;
        public bool NoSelection => _message == null;

        public string Heading
        {
            get
            {
                int n = Messages.Count;
                if (n == 0) return "No mail yet.";
                int unread = Messages.Count(m => m.IsUnread);
                return MainViewModel.Capitalise(MainViewModel.NumberWord(n)) + (n == 1 ? " message" : " messages") + (unread > 0 ? ", " + MainViewModel.NumberWord(unread) + " new." : ".");
            }
        }

        public string CatcherNote => _catcher?.IsRunning == true
            ? "Your projects' mail() goes to 127.0.0.1:" + _catcher.Port.ToString(CultureInfo.InvariantCulture) + " and stops here. Nothing reaches a real inbox, so test sign-up and password-reset mail freely."
            : "The mail catcher isn't listening, so mail() fails. Another program may be using port " + WebSettingsPort() + ".";

        public string SmtpLine => "SMTP host 127.0.0.1 · port " + (_catcher?.Port.ToString(CultureInfo.InvariantCulture) ?? "") + " · no username or password · no TLS";

        private string WebSettingsPort() => Core.Web.WebSettings.Load(_paths).PreferredMailPort.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ list

        private void Reload()
        {
            var keep = _selected?.FilePath;
            Messages.Clear();
            foreach (var f in MailStore.List(_paths).Take(MaxMessages))
            {
                var item = TryRead(f, unread: false);
                if (item != null) Messages.Add(item);
            }
            ListChanged();
            Open(Messages.FirstOrDefault(m => m.FilePath == keep) ?? Messages.FirstOrDefault());
        }

        private static MailItem TryRead(string path, bool unread)
        {
            try { return new MailItem(path, MimeMessage.Load(path), unread); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (Exception ex) { AppLog.Error("couldn't read " + path, ex); return null; }
        }

        private void OnReceived(string path)
        {
            var item = TryRead(path, unread: !_shown);
            if (item == null) return;
            Messages.Insert(0, item);
            while (Messages.Count > MaxMessages) Messages.RemoveAt(Messages.Count - 1);
            ListChanged();
            if (_shown || _message == null) Open(item);
            if (!_shown) _main.Toast("Mail caught: " + item.Subject);
        }

        private void ListChanged()
        {
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(NoMessages));
            OnPropertyChanged(nameof(Heading));
            var section = _main.Sections.FirstOrDefault(s => s.Key == "mail");
            if (section != null) section.HasAlert = Messages.Any(m => m.IsUnread);
            CommandManager.InvalidateRequerySuggested();
        }

        public void OnShown(bool shown)
        {
            _shown = shown;
            if (!shown) return;
            if (_selected != null) _selected.IsUnread = false;
            ListChanged();
        }

        // ------------------------------------------------------------------ one message

        public MailItem Selected => _selected;
        public MimeMessage Message => _message;
        public string Subject => _selected?.Subject ?? "";
        public string FromLine => _message == null ? "" : _message.From;
        public string ToLine => _message == null ? "" : _message.To + (string.IsNullOrEmpty(_message.Cc) ? "" : " · cc " + _message.Cc);
        public string WhenLine => _message == null ? "" : _message.Received.ToString("dddd d MMMM yyyy · HH:mm:ss", CultureInfo.InvariantCulture);
        public string TextBody => _message == null ? "" : _message.TextBody ?? MailItem.StripTags(_message.HtmlBody ?? "").Trim();
        public string Source => _message?.Raw ?? "";
        public bool HasHtml => !string.IsNullOrEmpty(_message?.HtmlBody);
        public bool HasAttachments => Attachments.Count > 0;
        public string AttachmentsCaption => Attachments.Count == 0 ? "No attachments." : MainViewModel.Capitalise(MainViewModel.NumberWord(Attachments.Count)) + (Attachments.Count == 1 ? " attachment." : " attachments.");

        /// <summary>The HTML to show, made inert: no scripts, no forms, no remote loads that would tell a tracker it was opened.</summary>
        public string SafeHtml
        {
            get
            {
                if (_message == null) return null;
                var html = HasHtml ? _message.DisplayHtml() : "<pre>" + System.Net.WebUtility.HtmlEncode(_message.TextBody ?? "") + "</pre>";
                return Sanitize(html);
            }
        }

        internal static string Sanitize(string html)
        {
            html = Regex.Replace(html, @"<script\b.*?</script\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(iframe|object|embed|form|meta|base)\b[^>]*>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"(href|src)\s*=\s*([""'])\s*javascript:[^""']*\2", "$1=\"#\"", RegexOptions.IgnoreCase);
            const string head = "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\">" +
                                "<style>body{font-family:Segoe UI,sans-serif;font-size:14px;color:#1c1b19;background:#fbfaf7;margin:18px;}img{max-width:100%;}</style>";
            return "<!DOCTYPE html><html><head>" + head + "</head><body>" + html + "</body></html>";
        }

        public string Tab
        {
            get => _tab;
            set
            {
                if (!Set(ref _tab, value ?? "html")) return;
                foreach (var t in Tabs) t.IsSelected = t.Key == _tab;
                OnPropertyChanged(nameof(IsHtml)); OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsSource)); OnPropertyChanged(nameof(IsFiles));
                HtmlChanged?.Invoke();
            }
        }

        public bool IsHtml => _tab == "html";
        public bool IsText => _tab == "text";
        public bool IsSource => _tab == "source";
        public bool IsFiles => _tab == "files";

        private static string TabTitle(string k)
        {
            switch (k)
            {
                case "html": return "As sent";
                case "text": return "Plain text";
                case "source": return "Source";
                default: return "Attachments";
            }
        }

        private void Open(MailItem item)
        {
            foreach (var m in Messages) m.IsSelected = m == item;
            _selected = item;
            _message = null;
            Attachments.Clear();
            if (item != null)
            {
                try { _message = MimeMessage.Load(item.FilePath); }
                catch (IOException ex) { _main.Toast("Couldn't read that message: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { _main.Toast("Couldn't read that message: " + ex.Message); }
                if (_message != null)
                {
                    foreach (var a in _message.Attachments.Where(a => string.IsNullOrEmpty(a.ContentId) || !string.IsNullOrEmpty(a.FileName)))
                        Attachments.Add(new AttachmentRow(a));
                    if (_shown) item.IsUnread = false;
                }
            }
            foreach (var n in new[] { nameof(Selected), nameof(Message), nameof(HasSelection), nameof(NoSelection), nameof(Subject), nameof(FromLine), nameof(ToLine),
                                      nameof(WhenLine), nameof(TextBody), nameof(Source), nameof(HasHtml), nameof(HasAttachments), nameof(AttachmentsCaption), nameof(SafeHtml) })
                OnPropertyChanged(n);
            ListChanged();
            HtmlChanged?.Invoke();
        }

        private void Delete()
        {
            var item = _selected;
            if (item == null) return;
            int index = Messages.IndexOf(item);
            try { MailStore.Delete(item.FilePath); }
            catch (IOException ex) { _main.Toast("Couldn't delete it: " + ex.Message); return; }
            Messages.Remove(item);
            ListChanged();
            Open(Messages.Count == 0 ? null : Messages[Math.Min(index, Messages.Count - 1)]);
        }

        private async System.Threading.Tasks.Task DeleteAllAsync()
        {
            var q = new ChoiceSlip("EMPTY MAIL", Messages.Count == 1 ? "Delete the one message?" : "Delete all " + Messages.Count + " messages?",
                "They are test messages caught from your projects; deleting them doesn't affect the projects.", true, "Delete all", null, _paths.Mail);
            if (await _main.AskAsync(q) != SlipChoice.Primary) return;
            int n;
            try { n = MailStore.DeleteAll(_paths); }
            catch (IOException ex) { _main.Toast("Couldn't delete them all: " + ex.Message); n = 0; }
            Reload();
            _main.Toast("Deleted " + n + (n == 1 ? " message." : " messages."));
        }

        private void SaveAttachment(AttachmentRow row)
        {
            if (row == null) return;
            var name = string.Join("_", (row.Attachment.FileName ?? "attachment").Split(Path.GetInvalidFileNameChars()));
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Title = "Save attachment" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, row.Attachment.Data ?? new byte[0]);
                _main.Toast("Saved " + Path.GetFileName(dlg.FileName) + ".");
            }
            catch (IOException ex) { _main.Toast("Couldn't save it: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { _main.Toast("Couldn't save it: " + ex.Message); }
        }

        public void Dispose() { }
    }
}
