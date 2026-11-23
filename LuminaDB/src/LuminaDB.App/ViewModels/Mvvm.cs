using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LuminaDB.App.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _run;
        private readonly Func<object, bool> _can;

        public RelayCommand(Action<object> run, Func<object, bool> can = null) { _run = run; _can = can; }
        public RelayCommand(Action run, Func<bool> can = null) : this(_ => run(), can == null ? (Func<object, bool>)null : _ => can()) { }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object p) => _can == null || _can(p);
        public void Execute(object p) => _run(p);
    }

    /// <summary>Runs an async action; disabled while it runs so a double-click can't start twice.</summary>
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<object, Task> _run;
        private readonly Func<object, bool> _can;
        private bool _running;

        public AsyncCommand(Func<object, Task> run, Func<object, bool> can = null) { _run = run; _can = can; }
        public AsyncCommand(Func<Task> run, Func<bool> can = null) : this(_ => run(), can == null ? (Func<object, bool>)null : _ => can()) { }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object p) => !_running && (_can == null || _can(p));

        public async void Execute(object p)
        {
            if (!CanExecute(p)) return;
            _running = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _run(p); }
            catch (Exception ex) { AppLog.Error("command failed", ex); }
            finally
            {
                _running = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
