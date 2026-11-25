using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Admin;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One account in the index on the left.</summary>
    public sealed class AccountRow : ObservableObject
    {
        private bool _selected;
        public AccountRow(Account a) { Account = a; }
        public Account Account { get; }
        public string Name => Account.User;
        public string Sub => Account.IsRole ? "role" : "@" + Account.Host + (Account.IsLocked ? " · locked" : "") + (Account.IsSystem ? " · MariaDB's own" : "");
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    }

    /// <summary>One privilege checkbox for the place chosen (a database, a table or a column).</summary>
    public sealed class PrivilegeCheck : ObservableObject
    {
        private readonly Action<PrivilegeCheck> _changed;
        private bool _checked;
        public PrivilegeCheck(Privileges.Info info, bool on, Action<PrivilegeCheck> changed) { Info = info; _checked = on; _changed = changed; }
        public Privileges.Info Info { get; }
        public string Name => Info.Name;
        public string Words => Info.Words;
        public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) _changed(this); } }
        internal void SetQuietly(bool v) { if (_checked != v) { _checked = v; OnPropertyChanged(nameof(IsChecked)); } }
    }

    /// <summary>One line of "what this account can do": a place and its privileges.</summary>
    public sealed class GrantLine
    {
        public string Place { get; set; }
        public string What { get; set; }
        internal List<Grant> Grants { get; set; }
    }

    /// <summary>
    /// Users (SPEC §5.7): every account, privileges per database/table/column with checkboxes, roles, and the GRANT
    /// statements shown before anything runs. root is looked after by LuminaDB (password on every root host at once).
    /// </summary>
    public sealed class UsersViewModel : ObservableObject
    {
        public const string EveryTable = "(every table)";
        public const string EveryColumn = "(every column)";
        public static readonly string[] HostChoices = { "localhost", "%", "127.0.0.1" };

        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        private AccountRow _selected;
        private List<Grant> _before = new List<Grant>();
        private HashSet<Grant> _after = new HashSet<Grant>();
        private bool _busy, _creating, _creatingRole;
        private string _busyText = "", _problem;
        private string _database, _table = EveryTable, _column = EveryColumn;
        private List<string> _shownGrants = new List<string>();
        private string _newName = "", _newHost = "localhost", _newPassword = "", _newPreset = "Read and write", _newDatabase;
        private string _roleToGive;

        public UsersViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            SelectCommand = new RelayCommand(p => { if (p is AccountRow r) Select(r); });
            NewUserCommand = new RelayCommand(() => StartNew(role: false), () => CanAct);
            NewRoleCommand = new RelayCommand(() => StartNew(role: true), () => CanAct);
            CancelNewCommand = new RelayCommand(() => { Creating = false; Select(_selected ?? Accounts.FirstOrDefault()); });
            CreateCommand = new AsyncCommand(CreateAsync, () => CanAct && NewProblem == null);
            PresetCommand = new RelayCommand(p => ApplyPreset(p as string), p => CanEdit && _database != null);
            RemoveLineCommand = new RelayCommand(p => { if (p is GrantLine l) { foreach (var g in l.Grants) _after.Remove(g); Changed(); } }, p => CanEdit);
            ApplyCommand = new AsyncCommand(ApplyAsync, () => CanAct && HasChanges);
            UndoChangesCommand = new RelayCommand(() => { _after = new HashSet<Grant>(_before); Changed(); }, () => HasChanges);
            PasswordCommand = new AsyncCommand(ChangePasswordAsync, () => CanAct && IsUser);
            LockCommand = new AsyncCommand(ToggleLockAsync, () => CanAct && IsUser);
            DropCommand = new AsyncCommand(DropAsync, () => CanAct && CanEdit);
            GiveRoleCommand = new AsyncCommand(GiveRoleAsync, () => CanAct && IsUser && !string.IsNullOrEmpty(_roleToGive));
            TakeRoleCommand = new AsyncCommand(p => TakeRoleAsync(p as string), p => CanAct && IsUser && p is string);
            RootPasswordCommand = new RelayCommand(() => _main.OpenRootPasswordSlip(), () => CanAct);
            RefreshCommand = new AsyncCommand(() => ReloadAsync(), () => _main.IsRunning && !_busy);
        }

        public ObservableCollection<AccountRow> Accounts { get; } = new ObservableCollection<AccountRow>();
        public ObservableCollection<PrivilegeCheck> Checks { get; } = new ObservableCollection<PrivilegeCheck>();
        public ObservableCollection<GrantLine> Lines { get; } = new ObservableCollection<GrantLine>();
        public ObservableCollection<string> Databases { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Tables { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Columns { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> GivenRoles { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> OtherRoles { get; } = new ObservableCollection<string>();
        public string[] Hosts => HostChoices;
        public string[] Presets => new[] { "Nothing yet", "Read only", "Read and write", "Everything on it" };

        public ICommand SelectCommand { get; }
        public ICommand NewUserCommand { get; }
        public ICommand NewRoleCommand { get; }
        public ICommand CancelNewCommand { get; }
        public ICommand CreateCommand { get; }
        public ICommand PresetCommand { get; }
        public ICommand RemoveLineCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand UndoChangesCommand { get; }
        public ICommand PasswordCommand { get; }
        public ICommand LockCommand { get; }
        public ICommand DropCommand { get; }
        public ICommand GiveRoleCommand { get; }
        public ICommand TakeRoleCommand { get; }
        public ICommand RootPasswordCommand { get; }
        public ICommand RefreshCommand { get; }

        private bool CanAct => !_busy && _main.IsRunning && !_main.NeedsPassword;
        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(BusyText)); CommandManager.InvalidateRequerySuggested(); } } }
        public string BusyText => _busy ? _busyText : "";
        public string Problem { get => _problem; private set { if (Set(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }
        public bool HasProblem => !string.IsNullOrEmpty(_problem);
        public bool IsAvailable => _main.IsRunning && !_main.NeedsPassword;
        public string OfflineNote => _main.NeedsPassword ? "Enter the root password first (the slip on Today asks for it)." : "Start the server to see and change accounts.";

        public Account Selected => _selected?.Account;
        public bool HasSelection => _selected != null && !_creating;
        public bool IsSystem => Selected?.IsSystem ?? false;
        public bool CanEdit => Selected != null && !Selected.IsSystem && !_creating;
        public bool IsUser => CanEdit && !Selected.IsRole;
        public bool IsRole => CanEdit && Selected.IsRole;
        public bool IsRoot => !_creating && Selected != null && string.Equals(Selected.User, "root", StringComparison.OrdinalIgnoreCase);
        public string SelectedTitle => Selected == null ? "" : Selected.IsRole ? Selected.User : Selected.User + "@" + Selected.Host;
        public string SelectedNote
        {
            get
            {
                var a = Selected;
                if (a == null) return "";
                if (string.Equals(a.User, "root", StringComparison.OrdinalIgnoreCase))
                    return "root can do everything. LuminaDB uses it to run the server and keeps its password the same on every root account (localhost, 127.0.0.1, ::1).";
                if (a.IsSystem) return "This account belongs to MariaDB itself. It's shown so you know it's there; leave it alone.";
                if (a.IsRole) return "A role is a named set of privileges. Give it to users and they get everything it has; change the role and they all change.";
                var how = a.PluginNote;
                return (a.Host == "%" ? "Can log in from any computer (if the server allowed the network; LuminaDB keeps it to this PC)." :
                        a.Host == "localhost" ? "Logs in from this PC. In PHP: new mysqli(\"localhost\", \"" + a.User + "\", \"…\", \"database\")." :
                        "Logs in from " + a.Host + ".") + " Login: " + how + "." + (a.IsLocked ? " Locked: logins are refused until you unlock it." : "");
            }
        }
        public string LockLabel => Selected?.IsLocked == true ? "Unlock" : "Lock";
        public string ShownGrants => string.Join(";\n", _shownGrants) + (_shownGrants.Count > 0 ? ";" : "");

        // ------------------------------------------------------------------ the place being edited

        public string Database
        {
            get => _database;
            set { if (Set(ref _database, value)) { _table = EveryTable; _column = EveryColumn; OnPropertyChanged(nameof(Table)); OnPropertyChanged(nameof(Column)); _ = LoadTablesAsync(); BuildChecks(); } }
        }

        public string Table
        {
            get => _table;
            set { if (Set(ref _table, value ?? EveryTable)) { _column = EveryColumn; OnPropertyChanged(nameof(Column)); _ = LoadColumnsAsync(); BuildChecks(); } }
        }

        public string Column
        {
            get => _column;
            set { if (Set(ref _column, value ?? EveryColumn)) BuildChecks(); }
        }

        private PrivilegeLevel Level => _table == EveryTable || string.IsNullOrEmpty(_table) ? PrivilegeLevel.Database
                                      : _column == EveryColumn || string.IsNullOrEmpty(_column) ? PrivilegeLevel.Table : PrivilegeLevel.Column;
        private string TableName => Level == PrivilegeLevel.Database ? null : _table;
        private string ColumnName => Level == PrivilegeLevel.Column ? _column : null;

        public string PlaceText => _database == null ? "" : Level == PrivilegeLevel.Database ? "Everything in " + _database
                                 : Level == PrivilegeLevel.Table ? "The table " + _database + "." + _table
                                 : "The column " + _table + "." + _column + " only";
        public bool CanPreset => Level == PrivilegeLevel.Database;

        // ------------------------------------------------------------------ pending changes

        public List<string> PendingSql => Selected == null || !CanEdit ? new List<string>() : Core.Admin.Accounts.Diff(Selected, _before, _after);
        public bool HasChanges => PendingSql.Count > 0;
        public string PendingScript => HasChanges ? Core.Admin.Accounts.Script(PendingSql) : "";
        public bool HasLines => Lines.Count > 0;
        public string NoLinesText => Selected?.IsRole == true ? "This role holds no privileges yet. Tick some below."
                                   : "Can log in, but can't see any database yet. Tick what it may do below.";

        // ------------------------------------------------------------------ new user / role

        public bool Creating { get => _creating; private set { if (Set(ref _creating, value)) { OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsUser)); OnPropertyChanged(nameof(IsRole)); OnPropertyChanged(nameof(IsRoot)); } } }
        public bool CreatingRole { get => _creatingRole; private set { if (Set(ref _creatingRole, value)) { OnPropertyChanged(nameof(CreatingUser)); OnPropertyChanged(nameof(NewHeading)); } } }
        public bool CreatingUser => !_creatingRole;
        public string NewHeading => _creatingRole ? "New role" : "New user";
        public string NewName { get => _newName; set { if (Set(ref _newName, value ?? "")) NewChanged(); } }
        public string NewHost { get => _newHost; set { if (Set(ref _newHost, value ?? "localhost")) NewChanged(); } }
        public string NewPassword { get => _newPassword; set { if (Set(ref _newPassword, value ?? "")) NewChanged(); } }
        public string NewPreset { get => _newPreset; set { if (Set(ref _newPreset, value)) NewChanged(); } }
        public string NewDatabase { get => _newDatabase; set { if (Set(ref _newDatabase, value)) NewChanged(); } }
        public string NewProblem
        {
            get
            {
                if (!IsAvailable) return OfflineNote;
                var p = Core.Admin.Accounts.CheckName(_newName, Accounts.Select(r => r.Account), _creatingRole ? null : _newHost, _creatingRole);
                if (p != null) return p;
                if (!_creatingRole) p = Core.Admin.Accounts.CheckPassword(_newPassword, allowEmpty: false);
                if (p != null) return p;
                if (_newPreset != "Nothing yet" && string.IsNullOrEmpty(_newDatabase)) return "Choose the database, or start with nothing.";
                return null;
            }
        }
        public string NewScript => NewProblem != null && string.IsNullOrWhiteSpace(_newName) ? "" : Core.Admin.Accounts.Script(NewStatements());
        public string NewPhp => _creatingRole || string.IsNullOrWhiteSpace(_newName) ? ""
            : "$db = new mysqli(\"" + (_newHost == "%" ? "localhost" : _newHost) + "\", \"" + _newName + "\", \"" + _newPassword.Replace("\"", "\\\"") + "\", \"" + (_newDatabase ?? "database") + "\");";

        private void NewChanged()
        {
            OnPropertyChanged(nameof(NewProblem));
            OnPropertyChanged(nameof(NewScript));
            OnPropertyChanged(nameof(NewPhp));
            CommandManager.InvalidateRequerySuggested();
        }

        private Account NewAccount() => new Account { User = _newName, Host = _creatingRole ? "" : _newHost, IsRole = _creatingRole };

        private List<string> NewStatements()
        {
            var a = NewAccount();
            var sql = _creatingRole ? Core.Admin.Accounts.CreateRole(_newName) : Core.Admin.Accounts.CreateUser(_newName, _newHost, withPassword: true);
            var privs = PresetList(_newPreset);
            if (privs != null && !string.IsNullOrEmpty(_newDatabase))
                sql.AddRange(Core.Admin.Accounts.Diff(a, null, privs.Select(p => new Grant(PrivilegeLevel.Database, p, _newDatabase))));
            return sql;
        }

        private static IReadOnlyList<string> PresetList(string preset)
        {
            switch (preset)
            {
                case "Read only": return Privileges.ReadOnly;
                case "Read and write": return Privileges.ReadWrite;
                case "Everything on it": return Privileges.Full;
                case "Nothing yet": return new string[0];
                default: return null;
            }
        }

        private void StartNew(bool role)
        {
            CreatingRole = role;
            _newName = ""; _newPassword = ""; _newHost = "localhost"; _newPreset = role ? "Nothing yet" : "Read and write";
            _newDatabase = Databases.FirstOrDefault(d => !IsSystemDatabase(d));
            foreach (var n in new[] { nameof(NewName), nameof(NewPassword), nameof(NewHost), nameof(NewPreset), nameof(NewDatabase) }) OnPropertyChanged(n);
            Creating = true;
            NewChanged();
        }

        // ------------------------------------------------------------------ loading

        public void OnShown()
        {
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(OfflineNote));
            if (IsAvailable) _ = ReloadAsync();
            else { Accounts.Clear(); Select(null); }
        }

        public async Task ReloadAsync(string keepUser = null, string keepHost = null)
        {
            if (!IsAvailable) return;
            keepUser = keepUser ?? Selected?.User;
            keepHost = keepHost ?? Selected?.Host;
            try
            {
                List<Account> list;
                using (var c = await _main.Server.OpenConnectionAsync())
                    list = await Core.Admin.Accounts.ListAsync(c);
                Accounts.Clear();
                foreach (var a in list.Where(a => !string.IsNullOrEmpty(a.User))) Accounts.Add(new AccountRow(a));
                Databases.Sync(_main.Databases.Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
                var keep = Accounts.FirstOrDefault(r => r.Account.User == keepUser && string.Equals(r.Account.Host, keepHost, StringComparison.OrdinalIgnoreCase))
                           ?? Accounts.FirstOrDefault(r => !r.Account.IsSystem) ?? Accounts.FirstOrDefault();
                await SelectAsync(keep);
                Problem = null;
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException)
            {
                Problem = ex.Message;
                AppLog.Error("users: reload failed", ex);
            }
        }

        private void Select(AccountRow r) => _ = SelectAsync(r);

        private async Task SelectAsync(AccountRow r)
        {
            if (_selected != null) _selected.IsSelected = false;
            _selected = r;
            if (r != null) r.IsSelected = true;
            Creating = false;
            _before = new List<Grant>();
            _shownGrants = new List<string>();
            GivenRoles.Clear();
            OtherRoles.Clear();
            if (r != null && IsAvailable)
            {
                try
                {
                    using (var c = await _main.Server.OpenConnectionAsync())
                    {
                        _before = await Core.Admin.Accounts.ReadGrantsAsync(c, r.Account);
                        try { _shownGrants = await Core.Admin.Accounts.ShowGrantsAsync(c, r.Account); }
                        catch (MySqlException) { _shownGrants = new List<string>(); }
                    }
                }
                catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { Problem = ex.Message; }
                foreach (var role in r.Account.Roles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) GivenRoles.Add(role);
                foreach (var role in Accounts.Where(x => x.Account.IsRole && !x.Account.IsSystem && !r.Account.Roles.Contains(x.Account.User)).Select(x => x.Account.User)) OtherRoles.Add(role);
                _roleToGive = OtherRoles.FirstOrDefault();
                OnPropertyChanged(nameof(RoleToGive));
            }
            _after = new HashSet<Grant>(_before);
            // Start on the database this account already uses, else the first of the student's own.
            var first = _before.Where(g => g.Database != null).Select(g => g.Database).FirstOrDefault(d => Databases.Contains(d, StringComparer.OrdinalIgnoreCase))
                        ?? Databases.FirstOrDefault(d => !IsSystemDatabase(d)) ?? Databases.FirstOrDefault();
            _database = first; _table = EveryTable; _column = EveryColumn;
            OnPropertyChanged(nameof(Database)); OnPropertyChanged(nameof(Table)); OnPropertyChanged(nameof(Column));
            await LoadTablesAsync();
            foreach (var n in new[] { nameof(Selected), nameof(HasSelection), nameof(IsSystem), nameof(CanEdit), nameof(IsUser), nameof(IsRole), nameof(IsRoot), nameof(SelectedTitle),
                                      nameof(SelectedNote), nameof(LockLabel), nameof(ShownGrants), nameof(HasRoles), nameof(HasOtherRoles) }) OnPropertyChanged(n);
            Changed();
        }

        public static bool IsSystemDatabase(string d) =>
            new[] { "mysql", "information_schema", "performance_schema", "sys" }.Contains(d, StringComparer.OrdinalIgnoreCase);

        // The "(every …)" entry stays first and is never removed: clearing a combo's whole list makes WPF push null into
        // its SelectedItem and the combo stays blank. Only the names after it are replaced; then the choice is re-announced.
        private async Task LoadTablesAsync()
        {
            try { await FillTablesAsync(); }
            finally { OnPropertyChanged(nameof(Table)); OnPropertyChanged(nameof(Column)); }
        }

        private async Task LoadColumnsAsync()
        {
            try { await FillColumnsAsync(); }
            finally { OnPropertyChanged(nameof(Column)); }
        }

        private static void KeepOnly(System.Collections.ObjectModel.ObservableCollection<string> list, string first)
        {
            if (list.Count == 0 || list[0] != first) { list.Clear(); list.Add(first); }
            while (list.Count > 1) list.RemoveAt(list.Count - 1);
        }

        private async Task FillTablesAsync()
        {
            KeepOnly(Tables, EveryTable);
            KeepOnly(Columns, EveryColumn);
            if (_database == null || !IsAvailable) return;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME", c))
                {
                    cmd.Parameters.AddWithValue("@db", _database);
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync()) Tables.Add(r.GetString(0));
                }
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("users: tables", ex); }
        }

        private async Task FillColumnsAsync()
        {
            KeepOnly(Columns, EveryColumn);
            if (_database == null || Level == PrivilegeLevel.Database || !IsAvailable) return;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION", c))
                {
                    cmd.Parameters.AddWithValue("@db", _database);
                    cmd.Parameters.AddWithValue("@t", _table);
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync()) Columns.Add(r.GetString(0));
                }
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("users: columns", ex); }
        }

        // ------------------------------------------------------------------ checkboxes

        private void BuildChecks()
        {
            Checks.Clear();
            if (_database != null && Selected != null)
            {
                var list = Level == PrivilegeLevel.Database ? Privileges.Database : Level == PrivilegeLevel.Table ? Privileges.Table : Privileges.Column;
                foreach (var info in list)
                    Checks.Add(new PrivilegeCheck(info, _after.Contains(Make(info.Name)), OnCheck));
            }
            OnPropertyChanged(nameof(PlaceText));
            OnPropertyChanged(nameof(CanPreset));
        }

        private Grant Make(string privilege) => new Grant(Level, privilege, _database, TableName, ColumnName);

        private void OnCheck(PrivilegeCheck c)
        {
            var g = Make(c.Name);
            if (c.IsChecked) _after.Add(g); else _after.Remove(g);
            Changed(rebuildChecks: false);
        }

        private void ApplyPreset(string preset)
        {
            if (_database == null) return;
            var keep = PresetList(preset);
            if (keep == null) return;
            foreach (var g in _after.Where(g => g.Level == PrivilegeLevel.Database && string.Equals(g.Database, _database, StringComparison.OrdinalIgnoreCase)).ToList()) _after.Remove(g);
            foreach (var p in keep) _after.Add(new Grant(PrivilegeLevel.Database, p, _database));
            Changed();
        }

        private void Changed(bool rebuildChecks = true)
        {
            if (rebuildChecks) BuildChecks();
            Lines.Clear();
            foreach (var place in _after.GroupBy(g => g.Level == PrivilegeLevel.Column ? g.Database + "." + g.Table + "." + g.Column : g.On)
                                        .OrderBy(x => x.First().Level).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var first = place.First();
                var where = first.Level == PrivilegeLevel.Global ? "every database (*.*)"
                          : first.Level == PrivilegeLevel.Database ? first.Database + ".*"
                          : first.Level == PrivilegeLevel.Table ? first.Database + "." + first.Table
                          : first.Database + "." + first.Table + " (" + first.Column + ")";
                Lines.Add(new GrantLine { Place = where, What = string.Join(", ", place.Select(g => g.Privilege).OrderBy(p => p, StringComparer.Ordinal)), Grants = place.ToList() });
            }
            OnPropertyChanged(nameof(HasLines));
            OnPropertyChanged(nameof(NoLinesText));
            OnPropertyChanged(nameof(PendingScript));
            OnPropertyChanged(nameof(HasChanges));
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ roles

        public string RoleToGive { get => _roleToGive; set { if (Set(ref _roleToGive, value)) CommandManager.InvalidateRequerySuggested(); } }
        public bool HasRoles => GivenRoles.Count > 0;
        public bool HasOtherRoles => OtherRoles.Count > 0;

        // ------------------------------------------------------------------ actions

        private async Task Busy(string text, Func<MySqlConnection, Task> work, string title)
        {
            _busyText = text;
            IsBusy = true;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync()) await work(c);
                Problem = null;
            }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlException)
            {
                var msg = ex is MySqlException m ? ExplainSql(m) : ex.Message;
                _main.Receipt(title, msg, "bad");
                await _main.AskAsync(new ChoiceSlip("USERS", "That didn't work.", msg, alarm: true, primary: "OK", secondary: null, detail: ex.Message));
                AppLog.Error("users action failed", ex);
            }
            finally { IsBusy = false; }
        }

        private static string ExplainSql(MySqlException ex)
        {
            var e = Core.Sql.ErrorExplainer.Explain(ex.Number, ex.Message, null, null, null);
            return e == null ? ex.Message : e.Title + ". " + e.Body;
        }

        private Task CreateAsync()
        {
            var sql = NewStatements();
            var a = NewAccount();
            var pwd = _newPassword;
            bool role = _creatingRole;
            return Busy(role ? "Creating the role…" : "Creating the user…", async c =>
            {
                await Core.Admin.Accounts.RunAsync(c, sql, pwd);
                _main.Receipt(role ? "role created" : "user created", a.Display + (sql.Count > 1 ? " · " + (sql.Count - 1) + " grant" + (sql.Count > 2 ? "s" : "") : ""), "ok");
                _main.Toast((role ? "Role " : "User ") + a.Display + " created.");
                Creating = false;
                await ReloadAsync(a.User, a.Host);
            }, role ? "new role" : "new user");
        }

        private Task ApplyAsync()
        {
            var a = Selected;
            var sql = PendingSql;
            return Busy("Changing privileges…", async c =>
            {
                await Core.Admin.Accounts.RunAsync(c, sql);
                _main.Receipt("privileges · " + a.Display, sql.Count + (sql.Count == 1 ? " statement" : " statements"), "ok");
                _main.Toast("Privileges for " + a.Display + " changed.");
                await ReloadAsync(a.User, a.Host);
            }, "privileges");
        }

        private async Task ChangePasswordAsync()
        {
            var a = Selected;
            var slip = new TextSlip("PASSWORD · " + a.Display.ToUpperInvariant(), "A new password.",
                "PHP code that logs in as " + a.User + " needs the new one too (config.php). Leave it empty to remove the password (only sensible for practice accounts).",
                "Change password", "", p => Core.Admin.Accounts.CheckPassword(p, allowEmpty: true), shown: true);
            var pwd = await _main.AskTextAsync(slip);
            if (pwd == null) return;
            await Busy("Changing the password…", async c =>
            {
                await Core.Admin.Accounts.RunAsync(c, new[] { Core.Admin.Accounts.SetPassword(a, pwd.Length == 0) }, pwd);
                _main.Receipt("password · " + a.Display, pwd.Length == 0 ? "removed" : "changed", "ok");
                _main.Toast(pwd.Length == 0 ? "The password was removed." : "Password changed.");
                await ReloadAsync(a.User, a.Host);
            }, "password");
        }

        private Task ToggleLockAsync()
        {
            var a = Selected;
            bool lockIt = !a.IsLocked;
            return Busy(lockIt ? "Locking…" : "Unlocking…", async c =>
            {
                await Core.Admin.Accounts.RunAsync(c, new[] { Core.Admin.Accounts.Lock(a, lockIt) });
                _main.Receipt((lockIt ? "locked · " : "unlocked · ") + a.Display, lockIt ? "logins refused" : "logins allowed", "ok");
                await ReloadAsync(a.User, a.Host);
            }, "lock");
        }

        private async Task DropAsync()
        {
            var a = Selected;
            var choice = await _main.AskAsync(new ChoiceSlip(a.IsRole ? "DELETE ROLE" : "DELETE USER", "Delete " + a.Display + "?",
                a.IsRole ? "Users who have this role lose what it gave them. The role can't be brought back, but you can make it again."
                         : "PHP code that logs in as " + a.User + " will stop working. Databases and tables are not touched.",
                alarm: true, primary: "Delete " + a.User, secondary: "Keep it", detail: Core.Admin.Accounts.Drop(a) + ";"));
            if (choice != SlipChoice.Primary) return;
            await Busy("Deleting…", async c =>
            {
                await Core.Admin.Accounts.RunAsync(c, new[] { Core.Admin.Accounts.Drop(a) });
                _main.Receipt((a.IsRole ? "role deleted · " : "user deleted · ") + a.Display, "", "ok");
                _main.Toast(a.Display + " deleted.");
                _selected = null;
                await ReloadAsync("", "");
            }, "delete");
        }

        private Task GiveRoleAsync()
        {
            var a = Selected;
            var role = _roleToGive;
            return Busy("Giving the role…", async c =>
            {
                // The default role is active as soon as the user logs in (otherwise they'd need SET ROLE first).
                var sql = new List<string> { Core.Admin.Accounts.GrantRole(role, a) };
                if (a.Roles.Count == 0) sql.Add(Core.Admin.Accounts.DefaultRole(role, a));
                await Core.Admin.Accounts.RunAsync(c, sql);
                _main.Receipt("role · " + a.Display, "given " + role, "ok");
                await ReloadAsync(a.User, a.Host);
            }, "role");
        }

        private Task TakeRoleAsync(string role)
        {
            var a = Selected;
            return Busy("Taking the role back…", async c =>
            {
                var sql = new List<string> { Core.Admin.Accounts.RevokeRole(role, a) };
                var left = a.Roles.Where(r => r != role).ToList();
                sql.Add(Core.Admin.Accounts.DefaultRole(left.FirstOrDefault(), a));
                await Core.Admin.Accounts.RunAsync(c, sql);
                _main.Receipt("role · " + a.Display, "took back " + role, "ok");
                await ReloadAsync(a.User, a.Host);
            }, "role");
        }
    }
}
