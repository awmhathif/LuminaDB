using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LuminaDB.Core;
using LuminaDB.Core.Sql;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One entry on the receipt tape (the output log). Server output is attached to the latest slip.</summary>
    public sealed class ReceiptSlip : ObservableObject
    {
        public const int VisibleLines = 6;
        private int _hidden;
        private string _message;
        private string _kind;

        public ReceiptSlip(string title, string message, string kind)
        {
            Time = DateTime.Now.ToString("HH:mm:ss");
            Title = title;
            _message = message;
            _kind = kind;
        }

        public string Time { get; }
        public string Title { get; }
        public string Message { get => _message; set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrEmpty(_message);
        /// <summary>ok | bad | info | run</summary>
        public string Kind { get => _kind; set => Set(ref _kind, value); }
        public ObservableCollection<string> Lines { get; } = new ObservableCollection<string>();
        public string HiddenText => _hidden == 0 ? null : $"… {_hidden} earlier line{(_hidden == 1 ? "" : "s")} in the log";
        public bool HasHidden => _hidden > 0;

        private static readonly System.Text.RegularExpressions.Regex Noise = new System.Text.RegularExpressions.Regex(
            @"^\s*(\d{4}-\d\d-\d\d|\d{6})\s+\d{1,2}:\d\d:\d\d\s+(\d+\s+)?(\[Note\]\s*)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>"2026-11-23 16:59:14 0 [Note] InnoDB: ..." → "InnoDB: ..." (warnings and errors keep their tag).</summary>
        public static string Tidy(string line) => line == null ? "" : Noise.Replace(line, "").Trim();

        public void AddLine(string line)
        {
            line = Tidy(line);
            if (line.Length == 0) return;
            Lines.Add(line);
            if (Lines.Count > VisibleLines)
            {
                Lines.RemoveAt(0);
                _hidden++;
                OnPropertyChanged(nameof(HiddenText));
                OnPropertyChanged(nameof(HasHidden));
            }
        }
    }

    /// <summary>Base for tear-off confirmation slips shown over the page.</summary>
    public abstract class SlipViewModel : ObservableObject
    {
        private string _problem;
        private bool _busy;

        protected SlipViewModel(Action close)
        {
            Close = close ?? (() => { (this as ChoiceSlip)?.Resolve(SlipChoice.Cancel); (this as TextSlip)?.Resolve(null); });
            CancelCommand = new RelayCommand(() => Close());
        }

        public Action Close { get; }
        public ICommand CancelCommand { get; }
        public string Eyebrow { get; protected set; }
        public string Big { get; protected set; }
        public string Text { get; protected set; }
        public string Problem { get => _problem; set { if (Set(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }
        public bool HasProblem => !string.IsNullOrEmpty(_problem);
        public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
        /// <summary>Red slips (errors, danger) vs neutral ones.</summary>
        public bool IsAlarm { get; protected set; }
    }

    public sealed class MessageSlip : SlipViewModel
    {
        public MessageSlip(Action close, string eyebrow, string big, string text, string detail, bool alarm, Action copy, Action openLogs) : base(close)
        {
            Eyebrow = eyebrow; Big = big; Text = text; Detail = detail; IsAlarm = alarm;
            CopyCommand = new RelayCommand(copy, () => copy != null);
            OpenLogsCommand = new RelayCommand(openLogs, () => openLogs != null);
        }

        public string Detail { get; }
        public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
        public ICommand CopyCommand { get; }
        public ICommand OpenLogsCommand { get; }
    }

    public sealed class NewDatabaseSlip : SlipViewModel
    {
        private readonly IReadOnlyCollection<string> _existing;
        private string _name = "";

        public NewDatabaseSlip(Action close, IReadOnlyCollection<string> existing, Func<string, Task> create) : base(close)
        {
            _existing = existing;
            Eyebrow = "NEW DATABASE";
            Big = "Name it.";
            Text = "Letters, digits and _ only, so it works in PHP and every tool. It uses utf8mb4, so Sinhala, Tamil and emoji are stored correctly.";
            CreateCommand = new AsyncCommand(async () =>
            {
                Problem = DatabaseCatalog.ValidateName(Name, _existing);
                if (Problem != null) return;
                IsBusy = true;
                try { await create(Name); Close(); }
                catch (LuminaException ex) { Problem = ex.Message; }
                catch (MySqlConnector.MySqlException ex) { Problem = ex.Message; }
                finally { IsBusy = false; }
            }, () => !IsBusy && DatabaseCatalog.ValidateName(Name, _existing) == null);
        }

        public string Name
        {
            get => _name;
            set
            {
                if (!Set(ref _name, value ?? "")) return;
                Problem = _name.Length == 0 ? null : DatabaseCatalog.ValidateName(_name, _existing);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand CreateCommand { get; }
        public string Suggestion => _existing.Contains("college") ? "shop" : "college";
    }

    public enum SlipChoice { Cancel, Primary, Secondary }

    /// <summary>A question with up to two answers (Run anyway / Add a WHERE / Cancel). Await <see cref="Result"/>.</summary>
    public sealed class ChoiceSlip : SlipViewModel
    {
        private readonly TaskCompletionSource<SlipChoice> _tcs = new TaskCompletionSource<SlipChoice>();

        public ChoiceSlip(string eyebrow, string big, string text, bool alarm, string primary, string secondary, string detail) : base(null)
        {
            Eyebrow = eyebrow; Big = big; Text = text; IsAlarm = alarm;
            PrimaryLabel = primary; SecondaryLabel = secondary; Detail = detail;
            PrimaryCommand = new RelayCommand(() => Resolve(SlipChoice.Primary));
            SecondaryCommand = new RelayCommand(() => Resolve(SlipChoice.Secondary), () => secondary != null);
        }

        /// <summary>Set by the host: removes the slip from the screen.</summary>
        internal Action Dismiss { get; set; }
        public string PrimaryLabel { get; }
        public string SecondaryLabel { get; }
        public bool HasSecondary => SecondaryLabel != null;
        public bool IsCalm => !IsAlarm;
        public string Detail { get; }
        public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
        public ICommand PrimaryCommand { get; }
        public ICommand SecondaryCommand { get; }
        public Task<SlipChoice> Result => _tcs.Task;

        public void Resolve(SlipChoice c)
        {
            if (_tcs.TrySetResult(c)) Dismiss?.Invoke();
        }
    }

    /// <summary>A view-model a PasswordBox can push its text into (PasswordSink).</summary>
    public interface IPasswordTarget { void SetPassword(string field, string value); }

    /// <summary>A one-line question (a new password for a user, say). Await <see cref="Result"/>: null when cancelled.</summary>
    public sealed class TextSlip : SlipViewModel
    {
        private readonly TaskCompletionSource<string> _tcs = new TaskCompletionSource<string>();
        private readonly Func<string, string> _check;
        private string _value;

        public TextSlip(string eyebrow, string big, string text, string primary, string initial, Func<string, string> check, bool shown = true) : base(null)
        {
            Eyebrow = eyebrow; Big = big; Text = text; PrimaryLabel = primary; _value = initial ?? ""; _check = check; IsShown = shown;
            OkCommand = new RelayCommand(() =>
            {
                var p = _check?.Invoke(_value);
                if (p != null) { Problem = p; return; }
                Resolve(_value);
            });
        }

        internal Action Dismiss { get; set; }
        public string PrimaryLabel { get; }
        public bool IsShown { get; }
        public string Value { get => _value; set { if (Set(ref _value, value ?? "")) Problem = null; } }
        public ICommand OkCommand { get; }
        public Task<string> Result => _tcs.Task;

        public void Resolve(string v)
        {
            if (_tcs.TrySetResult(v)) Dismiss?.Invoke();
        }
    }

    /// <summary>Sets or removes the root password on every root account at once (SPEC §5.7).</summary>
    public sealed class RootPasswordSlip : SlipViewModel, IPasswordTarget
    {
        private string _password = "", _confirm = "";

        public RootPasswordSlip(Action close, bool hasPassword, Func<string, Task> set) : base(close)
        {
            HasPassword = hasPassword;
            Eyebrow = "ROOT PASSWORD";
            Big = hasPassword ? "Change it." : "Set one.";
            Text = "Changed on every root account at once (localhost, 127.0.0.1, ::1), so no way in is left open. LuminaDB keeps it encrypted for your Windows account; " +
                   "PHP code that logs in as root needs it too (config.php). phpMyAdmin will ask for it.";
            SetCommand = new AsyncCommand(async () =>
            {
                var p = Core.Admin.Accounts.CheckPassword(_password, allowEmpty: false) ?? (_password != _confirm ? "The two passwords are different." : null);
                if (p != null) { Problem = p; return; }
                await Run(set, _password);
            }, () => !IsBusy);
            ClearCommand = new AsyncCommand(() => Run(set, ""), () => !IsBusy && HasPassword);
        }

        private async Task Run(Func<string, Task> set, string pwd)
        {
            IsBusy = true;
            Problem = null;
            try { await set(pwd); Close(); }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException) { Problem = ex.Message; }
            finally { IsBusy = false; }
        }

        public bool HasPassword { get; }
        public ICommand SetCommand { get; }
        public ICommand ClearCommand { get; }
        public void SetPassword(string field, string value)
        {
            if (field == "confirm") _confirm = value ?? ""; else _password = value ?? "";
            Problem = null;
        }
    }

    public sealed class PasswordSlip : SlipViewModel, IPasswordTarget
    {
        public PasswordSlip(Action close, Func<string, Task<bool>> unlock) : base(close)
        {
            Eyebrow = "ROOT PASSWORD";
            Big = "Once, on this PC.";
            Text = "The database has a root password that this PC doesn't know yet (for example, LuminaDB was copied from another PC). Type it once; it's then stored encrypted for your Windows account.";
            UnlockCommand = new AsyncCommand(async () =>
            {
                IsBusy = true;
                try
                {
                    if (await unlock(Password ?? "")) Close();
                    else Problem = "That's not it. The password is case-sensitive.";
                }
                finally { IsBusy = false; }
            }, () => !IsBusy);
        }

        public string Password { get; set; }
        public void SetPassword(string field, string value) => Password = value;
        public ICommand UnlockCommand { get; }
    }

    /// <summary>Save SQL as your own snippet: a word to type, a name for the list, and the SQL with optional ${1:blanks}.</summary>
    public sealed class SaveSnippetSlip : SlipViewModel
    {
        private readonly Func<string, bool> _isMine;
        private string _trigger = "";
        private string _title = "";
        private string _sql;

        public SaveSnippetSlip(Action close, string sql, Func<string, bool> isMine, Action<string, string, string> save) : base(close)
        {
            _isMine = isMine;
            _sql = sql ?? "";
            Eyebrow = "NEW SNIPPET";
            Big = "Give it a word.";
            Text = "Type the word in the notebook, press Tab, and this SQL is written for you.";
            SaveCommand = new RelayCommand(() =>
            {
                Problem = SqlSnippets.TriggerProblem(Trigger) ?? (Sql.Trim().Length == 0 ? "Write the SQL the snippet should bring in." : null);
                if (Problem != null) return;
                try { save(Trigger.Trim(), Title, Sql); Close(); }
                catch (LuminaException ex) { Problem = ex.Message; }
                catch (System.IO.IOException ex) { Problem = "Couldn't save it: " + ex.Message; }
                catch (UnauthorizedAccessException ex) { Problem = "Couldn't save it: " + ex.Message; }
            }, () => SqlSnippets.TriggerProblem(Trigger) == null && Sql.Trim().Length > 0);
        }

        public string Trigger
        {
            get => _trigger;
            set
            {
                if (!Set(ref _trigger, value ?? "")) return;
                Problem = _trigger.Length == 0 ? null : SqlSnippets.TriggerProblem(_trigger);
                OnPropertyChanged(nameof(TriggerNote));
                OnPropertyChanged(nameof(HasTriggerNote));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand SaveCommand { get; }
        public string Title { get => _title; set => Set(ref _title, value ?? ""); }

        public string Sql
        {
            get => _sql;
            set
            {
                if (!Set(ref _sql, value ?? "")) return;
                OnPropertyChanged(nameof(BlankNote));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>"Replaces your snippet top." when the word is already one of yours.</summary>
        public string TriggerNote => !HasProblem && _trigger.Trim().Length > 0 && _isMine(_trigger) ? "You already have " + _trigger.Trim() + ": saving replaces it." : null;
        public bool HasTriggerNote => TriggerNote != null;

        public string BlankNote
        {
            get
            {
                int n = SnippetTemplate.FieldCount(_sql);
                return n == 0
                    ? "Tip: write ${1:table_name} where a name changes each time. Tab jumps to each blank in number order, and the same number twice is filled in once."
                    : n + (n == 1 ? " blank" : " blanks") + " for Tab to jump to.";
            }
        }
    }
}
