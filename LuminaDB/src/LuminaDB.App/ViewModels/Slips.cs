using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LuminaDB.Core;

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
            Close = close ?? (() => (this as ChoiceSlip)?.Resolve(SlipChoice.Cancel));
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

    public sealed class PasswordSlip : SlipViewModel
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
        public ICommand UnlockCommand { get; }
    }
}
