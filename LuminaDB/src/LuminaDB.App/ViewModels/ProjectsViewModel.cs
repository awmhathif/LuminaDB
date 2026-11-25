using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Web;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One project in the left-hand list.</summary>
    public sealed class ProjectItem : ObservableObject
    {
        private bool _selected;
        public ProjectItem(Project p) { Project = p; }
        public Project Project { get; }
        public string Name => Project.Name;
        public string Detail => (Project.NeedsApache ? ".htaccess · Apache" : TemplateTitle(Project.Template)) + " · " + Project.Database;
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

        internal static string TemplateTitle(string id)
        {
            switch (id)
            {
                case "empty": return "PHP";
                case "crud": return "CRUD";
                case "login": return "login";
                case "api": return "REST API";
                case "laravel": return "Laravel";
                case "wordpress": return "WordPress";
                case "imported": return "imported";
                default: return "PHP";
            }
        }
    }

    /// <summary>A template choice in the New project slip.</summary>
    public sealed class TemplateChoice : ObservableObject
    {
        private bool _selected;
        public TemplateChoice(ProjectTemplate t) { Template = t; }
        public ProjectTemplate Template { get; }
        public string Title => Template.Title;
        public string Description => Template.IsAvailable ? Template.Description : Template.Unavailable;
        public bool IsAvailable => Template.IsAvailable;
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    }

    public sealed class ExtensionChoice : ObservableObject
    {
        private bool _on;
        public ExtensionChoice(string name, bool on) { Name = name; _on = on; }
        public string Name { get; }
        public bool IsOn { get => _on; set => Set(ref _on, value); }
    }

    public sealed class SnapshotRow
    {
        public SnapshotRow(SnapshotInfo s) { Info = s; }
        public SnapshotInfo Info { get; }
        public string When => Info.Taken.ToString("d MMM yyyy · HH:mm", CultureInfo.InvariantCulture);
        public string Note => string.IsNullOrWhiteSpace(Info.Note) ? "(no note)" : Info.Note;
        public string Contents => (Info.HasDatabase ? "code + database" : "code only") + " · " + Info.SizeText;
        public bool HasDatabase => Info.HasDatabase;
    }

    public sealed class EditorChoice
    {
        public string Name { get; set; }
        public string Exe { get; set; }
    }

    /// <summary>
    /// The Projects section (SPEC §5.2): list, create, open in browser/editor, logs, PHP settings, Composer,
    /// snapshots, demo mode and the submission pack.
    /// </summary>
    public sealed class ProjectsViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel _main;
        private readonly AppPaths _paths;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _logTimer;
        private readonly DispatcherTimer _demoClock;
        private ProjectItem _selected;
        private string _page = "overview";
        private bool _busy;
        private string _busyText;
        private CancellationTokenSource _composerCts;

        public ProjectsViewModel(MainViewModel main, AppPaths paths, Dispatcher ui)
        {
            _main = main;
            _paths = paths;
            _ui = ui;
            Service = new ProjectService(paths, main.Stack);
            Archive = new ProjectArchive(paths);

            foreach (var e in ProjectService.Editors()) Editors.Add(new EditorChoice { Name = e.Key, Exe = e.Value });
            foreach (var k in new[] { "overview", "logs", "php", "composer", "snapshots", "share" }) Pages.Add(new PageChip(k, PageTitle(k)));
            Pages[0].IsSelected = true;

            SelectProjectCommand = new RelayCommand(p => Select(p as ProjectItem));
            SelectPageCommand = new RelayCommand(p => Page = (p as PageChip)?.Key ?? "overview");
            NewProjectCommand = new RelayCommand(OpenNewProject, () => !_busy);
            ImportFolderCommand = new AsyncCommand(ImportFolderAsync, () => !_busy);
            RefreshCommand = new RelayCommand(() => Reload(), () => !_busy);
            OpenBrowserCommand = new RelayCommand(p => OpenUrl(p as string ?? HostUrl), _ => Selected != null && _main.WebRunning);
            OpenPathUrlCommand = new RelayCommand(() => OpenUrl(PathUrl), () => Selected != null && _main.WebRunning);
            OpenEditorCommand = new RelayCommand(p => OpenEditor(p as EditorChoice), p => Selected != null);
            OpenFolderCommand = new RelayCommand(() => OpenFolder(Selected?.Project.Folder), () => Selected != null);
            OpenDatabaseCommand = new AsyncCommand(async () => { _main.Select("notebook"); await _main.Notebook.OpenDatabaseAsync(Selected.Project.Database); }, () => Selected != null && _main.IsRunning);
            CopyCommand = new RelayCommand(p => _main.CopyText(p as string ?? "", "Copied " + (p as string)), p => !string.IsNullOrEmpty(p as string));
            DeleteCommand = new AsyncCommand(DeleteAsync, () => Selected != null && !_busy);
            RenameFolderCommand = new RelayCommand(p => RenameFolder(p as string), p => !_busy);
            ToggleWebCommand = new AsyncCommand(() => _main.WebRunning ? _main.StopWebAsync() : _main.StartWebAsync(), () => _main.CanServeWeb && !_main.WebBusy);

            SavePhpCommand = new RelayCommand(SavePhp, () => Selected != null);
            SaveExtensionsCommand = new AsyncCommand(SaveExtensionsAsync, () => !_busy);

            ShowPhpLogCommand = new RelayCommand(() => { LogKind = "php"; RestartLog(); });
            ShowAccessLogCommand = new RelayCommand(() => { LogKind = "access"; RestartLog(); });
            ClearLogCommand = new RelayCommand(ClearLog, () => Selected != null);
            OpenLogFileCommand = new RelayCommand(() => OpenFile(CurrentLogPath), () => Selected != null && File.Exists(CurrentLogPath ?? ""));

            RunComposerCommand = new AsyncCommand(p => RunComposerAsync(p as string ?? ComposerCommand), p => Selected != null && _main.Stack.HasComposer && _composerCts == null);
            StopComposerCommand = new RelayCommand(() => _composerCts?.Cancel(), () => _composerCts != null);

            TakeSnapshotCommand = new AsyncCommand(TakeSnapshotAsync, () => Selected != null && !_busy);
            RestoreSnapshotCommand = new AsyncCommand(p => RestoreAsync(p as SnapshotRow), p => p is SnapshotRow && !_busy);
            DeleteSnapshotCommand = new AsyncCommand(p => DeleteSnapshotAsync(p as SnapshotRow), p => p is SnapshotRow && !_busy);
            OpenSnapshotsFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(Path.Combine(_paths.Snapshots, Selected.Name)); OpenFolder(Path.Combine(_paths.Snapshots, Selected.Name)); }, () => Selected != null);

            BuildPackCommand = new AsyncCommand(BuildPackAsync, () => Selected != null && !_busy);
            ShowPackCommand = new RelayCommand(() => ShowInExplorer(LastPack), () => LastPack != null && File.Exists(LastPack));

            StartDemoCommand = new AsyncCommand(StartDemoAsync, () => Selected != null && _main.WebRunning && SelectedAddress != null && !_busy);
            StopDemoCommand = new AsyncCommand(() => _main.Web.StopDemoAsync(), () => _main.Web?.Demo != null);
            RefreshAddressesCommand = new RelayCommand(RefreshAddresses);

            _logTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) => PollLog(), ui);
            _logTimer.Stop();
            _demoClock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (s, e) => OnPropertyChanged(nameof(DemoUntil)), ui);

            var settings = JsonFile.Read<ProjectsUiSettings>(SettingsFile) ?? new ProjectsUiSettings();
            _studentId = settings.StudentId ?? "";
            _lastSelected = settings.Project;
            Reload();
            RefreshAddresses();
        }

        public ProjectService Service { get; }
        public ProjectArchive Archive { get; }
        public ObservableCollection<ProjectItem> Projects { get; } = new ObservableCollection<ProjectItem>();
        public ObservableCollection<KeyValuePair<string, string>> Unusable { get; } = new ObservableCollection<KeyValuePair<string, string>>();
        public bool HasUnusable => Unusable.Count > 0;
        public ObservableCollection<PageChip> Pages { get; } = new ObservableCollection<PageChip>();
        public ObservableCollection<EditorChoice> Editors { get; } = new ObservableCollection<EditorChoice>();
        public bool HasProjects => Projects.Count > 0;
        public bool HasSelection => Selected != null;
        public bool NoSelection => Selected == null;

        public ICommand SelectProjectCommand { get; }
        public ICommand SelectPageCommand { get; }
        public ICommand NewProjectCommand { get; }
        public ICommand ImportFolderCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenBrowserCommand { get; }
        public ICommand OpenPathUrlCommand { get; }
        public ICommand OpenEditorCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand OpenDatabaseCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand RenameFolderCommand { get; }
        public ICommand ToggleWebCommand { get; }

        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
        public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

        private static string PageTitle(string key)
        {
            switch (key)
            {
                case "overview": return "Overview";
                case "logs": return "Logs";
                case "php": return "PHP settings";
                case "composer": return "Composer";
                case "snapshots": return "Snapshots";
                default: return "Demo";
            }
        }

        // ------------------------------------------------------------------ web status (top of the list)

        public string WebStatusWord => _main.WebStatusWord;
        public string WebStatusTail => _main.WebStatusTail;
        public StatusTone WebTone => _main.WebTone;
        public string WebButtonText => _main.WebRunning ? "Stop web server" : _main.WebBusy ? "Starting…" : "Start web server";
        public string WebProblem => _main.Web?.Problem ?? _main.Stack.Missing;
        public bool HasWebProblem => !string.IsNullOrEmpty(WebProblem) && !_main.WebRunning;

        /// <summary>MainViewModel calls this when the web server's state changes.</summary>
        public void OnWebChanged()
        {
            OnPropertyChanged(nameof(WebStatusWord));
            OnPropertyChanged(nameof(WebStatusTail));
            OnPropertyChanged(nameof(WebTone));
            OnPropertyChanged(nameof(WebButtonText));
            OnPropertyChanged(nameof(WebProblem));
            OnPropertyChanged(nameof(HasWebProblem));
            OnPropertyChanged(nameof(HostUrl));
            OnPropertyChanged(nameof(PathUrl));
            OnPropertyChanged(nameof(UrlNote));
            OnDemoChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ list

        private string _lastSelected;

        public void Reload(string select = null)
        {
            var keep = select ?? Selected?.Name ?? _lastSelected;
            List<Project> list;
            try { list = Service.List(); }
            catch (Exception ex) { AppLog.Error("listing projects failed", ex); list = new List<Project>(); }
            Projects.Clear();
            foreach (var p in list) Projects.Add(new ProjectItem(p));
            Unusable.Clear();
            foreach (var u in Service.UnusableFolders()) Unusable.Add(u);
            OnPropertyChanged(nameof(HasProjects));
            OnPropertyChanged(nameof(HasUnusable));
            OnPropertyChanged(nameof(ListHeading));
            var item = Projects.FirstOrDefault(p => p.Name == keep) ?? Projects.FirstOrDefault();
            _selected = null;
            Select(item);
        }

        public string ListHeading
        {
            get
            {
                int n = Projects.Count;
                return n == 0 ? "No projects yet." : MainViewModel.Capitalise(MainViewModel.NumberWord(n)) + (n == 1 ? " project." : " projects.");
            }
        }

        public ProjectItem Selected
        {
            get => _selected;
            private set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(NoSelection));
            }
        }

        private void Select(ProjectItem item)
        {
            foreach (var p in Projects) p.IsSelected = p == item;
            Selected = item;
            if (item != null) { _lastSelected = item.Name; SaveUiSettings(); }
            LoadPhpForm();
            LoadSnapshots();
            RestartLog();
            ComposerOutput = "";
            OnPropertyChanged(nameof(HostUrl));
            OnPropertyChanged(nameof(PathUrl));
            OnPropertyChanged(nameof(UrlNote));
            OnPropertyChanged(nameof(ServedBy));
            OnPropertyChanged(nameof(FolderText));
            OnPropertyChanged(nameof(DatabaseText));
            OnPropertyChanged(nameof(ConfigText));
            OnPropertyChanged(nameof(CreatedText));
            OnPropertyChanged(nameof(PackName));
            OnDemoChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ pages

        public string Page
        {
            get => _page;
            set
            {
                if (!Set(ref _page, value ?? "overview")) return;
                foreach (var c in Pages) c.IsSelected = c.Key == _page;
                OnPropertyChanged(nameof(IsOverview)); OnPropertyChanged(nameof(IsLogs)); OnPropertyChanged(nameof(IsPhp));
                OnPropertyChanged(nameof(IsComposer)); OnPropertyChanged(nameof(IsSnapshots)); OnPropertyChanged(nameof(IsShare));
                if (_page == "logs") RestartLog(); else _logTimer.Stop();
                if (_page == "snapshots") LoadSnapshots();
                if (_page == "share") RefreshAddresses();
            }
        }

        public bool IsOverview => _page == "overview";
        public bool IsLogs => _page == "logs";
        public bool IsPhp => _page == "php";
        public bool IsComposer => _page == "composer";
        public bool IsSnapshots => _page == "snapshots";
        public bool IsShare => _page == "share";

        /// <summary>The section became visible or hidden (the log tail only polls while it can be seen).</summary>
        public void OnShown(bool shown)
        {
            if (shown) { Reload(); if (IsLogs) RestartLog(); }
            else _logTimer.Stop();
        }

        // ------------------------------------------------------------------ overview

        public string HostUrl => Selected == null ? "" : _main.Web?.WebPort > 0 ? _main.Web.HostUrl(Selected.Name) : "http://" + Selected.Name + ".localhost/";
        public string PathUrl => Selected == null ? "" : _main.Web?.WebPort > 0 ? _main.Web.PathUrl(Selected.Name) : "http://localhost/" + Selected.Name + "/";
        public string UrlNote => _main.WebRunning
            ? "Works in Chrome, Edge and Firefox with no hosts file. The second address is for older browsers."
            : "Start the web server to open it.";
        public string ServedBy => Selected == null ? "" : Selected.Project.NeedsApache
            ? "Has an .htaccess, so Apache serves it (started only for projects that need it)."
            : "Served by Caddy with PHP " + (_main.Stack.PhpVersion ?? "") + ". Add an .htaccess and Apache takes over by itself.";
        public string FolderText => Selected?.Project.Folder ?? "";
        public string DatabaseText => Selected?.Project.Database ?? "";
        public string CreatedText => Selected == null ? "" : "Made " + Selected.Project.Meta.Created.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + " from " + ProjectItem.TemplateTitle(Selected.Project.Template) + (Selected.Project.Template == "imported" ? "" : " template") + ".";
        public string ConfigText
        {
            get
            {
                if (Selected == null) return "";
                var port = _main.IsRunning ? _main.Server.Port.ToString(CultureInfo.InvariantCulture) : (LuminaState.Load(_paths).GetMariaDbPort(_main.Kind)?.ToString(CultureInfo.InvariantCulture) ?? "3306");
                return "host 127.0.0.1 · port " + port + " · database " + Selected.Project.Database + " · user root";
            }
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { _main.Toast("Couldn't open the browser: " + ex.Message); }
        }

        private void OpenEditor(EditorChoice e)
        {
            if (Selected == null) return;
            e = e ?? Editors.First();
            try { ProjectService.OpenInEditor(Selected.Project, e.Exe); }
            catch (Exception ex) { _main.Toast("Couldn't open " + e.Name + ": " + ex.Message); }
        }

        private void OpenFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try { Process.Start("explorer.exe", "\"" + folder + "\"")?.Dispose(); }
            catch (Exception ex) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }

        private void OpenFile(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            try { Process.Start("notepad.exe", "\"" + file + "\"")?.Dispose(); }
            catch (Exception ex) { _main.Toast("Couldn't open the file: " + ex.Message); }
        }

        private void ShowInExplorer(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            try { Process.Start("explorer.exe", "/select,\"" + file + "\"")?.Dispose(); }
            catch (Exception ex) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }

        // ------------------------------------------------------------------ new / import / delete / rename

        private void OpenNewProject()
        {
            var existing = Directory.Exists(_paths.Projects) ? Directory.GetDirectories(_paths.Projects).Select(Path.GetFileName).ToList() : new List<string>();
            _main.Slip = new NewProjectSlip(_main.CloseSlip, Service.Templates, existing, _main.IsRunning, async (name, template, progress) =>
            {
                await Service.CreateAsync(name, template, _main.Server, progress);
                await _main.ProjectsChangedAsync();
                Reload(name);
                Page = "overview";
                _main.DataChanged();
                _main.Toast("Created " + name + (_main.WebRunning ? ". Open it in the browser." : "."));
            });
        }

        private async Task ImportFolderAsync()
        {
            string folder;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Pick a project folder to copy into LuminaDB (for example one from XAMPP's htdocs). The original stays where it is.",
                ShowNewFolderButton = false,
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                folder = dlg.SelectedPath;
            }
            var existing = Directory.Exists(_paths.Projects) ? Directory.GetDirectories(_paths.Projects).Select(Path.GetFileName).ToList() : new List<string>();
            var name = ProjectNames.Suggest(Path.GetFileName(folder));
            for (int i = 2; existing.Contains(name, StringComparer.OrdinalIgnoreCase); i++) name = ProjectNames.Suggest(Path.GetFileName(folder)) + "-" + i;
            IsBusy = true;
            BusyText = "Copying " + Path.GetFileName(folder) + "…";
            try
            {
                var project = await Service.ImportFolderAsync(folder, name);
                await _main.ProjectsChangedAsync();
                Reload(name);
                IsBusy = false;
                BusyText = null;
                await OfferDatabaseAsync(project);
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (IOException ex) { _main.Toast("Couldn't copy the folder: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { _main.Toast("Couldn't copy the folder: " + ex.Message); }
            finally { IsBusy = false; BusyText = null; }
        }

        /// <summary>
        /// After a folder import: the PHP's database usually isn't on this server yet. Say which one it uses, and when
        /// the folder holds a .sql dump, offer to open it in Import (which shows it first and backs up before loading).
        /// </summary>
        private async Task OfferDatabaseAsync(Project project)
        {
            var db = project.Database;
            var scan = await Task.Run(() => ProjectDatabaseScan.Scan(project.Folder));
            bool here = _main.Databases.Any(d => string.Equals(d.Name, db, StringComparison.OrdinalIgnoreCase));
            string uses = scan.Database != null
                ? "Its PHP connects to the database " + db + " (" + scan.FoundIn + ")"
                : "LuminaDB couldn't find a database name in its PHP, so it uses " + db;
            if (scan.SqlFiles.Count == 0)
            {
                _main.Toast("Imported as " + project.Name + ". " + uses + (here ? ", which is already here." : ", which isn't here yet: load its .sql in Import (F6)."));
                return;
            }
            var sql = scan.SqlFiles[0];
            var more = scan.SqlFiles.Count > 1 ? " It also has " + (scan.SqlFiles.Count - 1) + " other .sql " + (scan.SqlFiles.Count == 2 ? "file" : "files") + "; Import (F6) can load them too." : "";
            var choice = await _main.AskAsync(new ChoiceSlip("DATABASE", here ? "Load " + Path.GetFileName(sql) + " again?" : "Load its database?",
                "Imported as " + project.Name + ". " + uses + (here ? ", which you already have." : ", which isn't on this server yet.") +
                " The folder has " + Path.GetFileName(sql) + ": open it in Import to see what it does, then load it." + more,
                false, "Open in Import", null, sql));
            if (choice != SlipChoice.Primary) return;
            _main.Select("import");
            _main.Import.OpenSqlFor(Path.Combine(project.Folder, sql), db);
        }

        private async Task DeleteAsync()
        {
            var p = Selected?.Project;
            if (p == null) return;
            var q = new ChoiceSlip("DELETE PROJECT", "Delete " + p.Name + "?",
                "The folder goes to the Recycle Bin, so you can bring it back. Choose whether the database " + p.Database + " goes too; that part can't be undone, so take a snapshot first if you might need it.",
                true, "Delete folder and database", "Delete folder only", p.Folder);
            var answer = await _main.AskAsync(q);
            if (answer == SlipChoice.Cancel) return;
            IsBusy = true;
            try
            {
                await Service.DeleteAsync(p, answer == SlipChoice.Primary, _main.Server);
                await _main.ProjectsChangedAsync();
                _selected = null;
                Reload();
                if (answer == SlipChoice.Primary) _main.DataChanged();
                _main.Toast(p.Name + " moved to the Recycle Bin" + (answer == SlipChoice.Primary ? "; its database was deleted." : "."));
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (MySqlConnector.MySqlException ex) { _main.Toast("The folder is in the Recycle Bin, but the database stayed: " + ex.Message); Reload(); }
            finally { IsBusy = false; }
        }

        private void RenameFolder(string folderName)
        {
            var pair = Unusable.FirstOrDefault(u => u.Key == folderName);
            if (pair.Key == null) return;
            try
            {
                Service.RenameFolder(pair.Key, pair.Value);
                _ = _main.ProjectsChangedAsync();
                Reload(pair.Value);
                _main.Toast("Renamed \"" + pair.Key + "\" to " + pair.Value + ".");
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (IOException ex) { _main.Toast("Couldn't rename it: " + ex.Message + " Close programs using the folder."); }
            catch (UnauthorizedAccessException ex) { _main.Toast("Couldn't rename it: " + ex.Message); }
        }

        // ------------------------------------------------------------------ PHP settings

        public string[] UploadSizes => ProjectPhpSettings.UploadSizes;
        public string[] MemoryLimits => ProjectPhpSettings.MemoryLimits;
        public string[] TimeLimits => new[] { "30", "60", "120", "300", "600" };
        private string _upload, _memory, _timeLimit, _timeZone, _phpProblem;
        private bool _deprecations;
        public string Upload { get => _upload; set => Set(ref _upload, value); }
        public string Memory { get => _memory; set => Set(ref _memory, value); }
        public string TimeLimit { get => _timeLimit; set => Set(ref _timeLimit, value); }
        public string TimeZone { get => _timeZone; set => Set(ref _timeZone, value); }
        public bool Deprecations { get => _deprecations; set => Set(ref _deprecations, value); }
        public string PhpProblem { get => _phpProblem; private set { if (Set(ref _phpProblem, value)) OnPropertyChanged(nameof(HasPhpProblem)); } }
        public bool HasPhpProblem => !string.IsNullOrEmpty(_phpProblem);
        public string PcTimeZone => "Empty uses this PC's time zone (" + PhpConfig.IanaTimeZone() + "), the same one MariaDB uses.";
        public ObservableCollection<ExtensionChoice> Extensions { get; } = new ObservableCollection<ExtensionChoice>();
        public ICommand SavePhpCommand { get; }
        public ICommand SaveExtensionsCommand { get; }

        private void LoadPhpForm()
        {
            var s = Selected?.Project.Meta.Php ?? new ProjectPhpSettings();
            Upload = s.UploadMax;
            Memory = s.MemoryLimit;
            TimeLimit = s.MaxExecutionSeconds.ToString(CultureInfo.InvariantCulture);
            TimeZone = s.TimeZone;
            Deprecations = s.ShowDeprecations;
            PhpProblem = null;
            Extensions.Clear();
            var on = new HashSet<string>(WebSettings.Load(_paths).Extensions, StringComparer.OrdinalIgnoreCase);
            foreach (var e in _main.Stack.AvailableExtensions) Extensions.Add(new ExtensionChoice(e, on.Contains(e)));
        }

        private void SavePhp()
        {
            if (Selected == null) return;
            if (!int.TryParse(TimeLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)) { PhpProblem = "The time limit must be a number of seconds."; return; }
            var s = new ProjectPhpSettings { UploadMax = Upload, MemoryLimit = Memory, MaxExecutionSeconds = secs, TimeZone = (TimeZone ?? "").Trim(), ShowDeprecations = Deprecations };
            try
            {
                Service.SavePhpSettings(Selected.Project, s);
                PhpProblem = null;
                _main.Toast("Saved. PHP picks it up within 5 seconds; no restart.");
            }
            catch (LuminaException ex) { PhpProblem = ex.Message; }
            catch (IOException ex) { PhpProblem = "Couldn't write .user.ini: " + ex.Message; }
            catch (UnauthorizedAccessException ex) { PhpProblem = "Couldn't write .user.ini: " + ex.Message; }
        }

        private async Task SaveExtensionsAsync()
        {
            var s = WebSettings.Load(_paths);
            s.Extensions = Extensions.Where(e => e.IsOn).Select(e => e.Name).ToList();
            IsBusy = true;
            try
            {
                if (_main.Web != null) _main.Web.SaveSettings(s); else s.Save(_paths);
                if (_main.WebRunning) await _main.Web.RestartPhpAsync(_main.PhpRuntime());
                _main.Toast(_main.WebRunning ? "Extensions saved; PHP restarted." : "Extensions saved; they apply when the web server starts.");
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            finally { IsBusy = false; }
        }

        // ------------------------------------------------------------------ logs

        private LogTail _tail;
        private string _logKind = "php";
        private string _logText = "";
        private readonly List<string> _logLines = new List<string>();
        public const int MaxLogLines = 400;
        public string LogKind { get => _logKind; set { if (Set(ref _logKind, value)) { OnPropertyChanged(nameof(IsPhpLog)); OnPropertyChanged(nameof(IsAccessLog)); } } }
        public bool IsPhpLog => _logKind == "php";
        public bool IsAccessLog => _logKind == "access";
        public string LogText { get => _logText; private set => Set(ref _logText, value); }
        public string LogEmptyText => IsPhpLog ? "No PHP errors or warnings yet. They appear here the moment a page has one." : "No requests yet. Open the project in the browser.";
        public bool LogIsEmpty => _logLines.Count == 0;
        public ICommand ShowPhpLogCommand { get; }
        public ICommand ShowAccessLogCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand OpenLogFileCommand { get; }
        public event Action LogAppended;

        private string CurrentLogPath => Selected == null ? null : IsPhpLog ? WebConfig.PhpLog(_paths, Selected.Name) : WebConfig.AccessLog(_paths, Selected.Name);

        private void RestartLog()
        {
            _logLines.Clear();
            LogText = "";
            _tail = Selected == null ? null : new LogTail(CurrentLogPath);
            OnPropertyChanged(nameof(LogEmptyText));
            if (IsLogs && _tail != null) { PollLog(); _logTimer.Start(); }
            else _logTimer.Stop();
            OnPropertyChanged(nameof(LogIsEmpty));
        }

        private void PollLog()
        {
            if (_tail == null) return;
            List<string> lines;
            try { lines = _tail.ReadNew(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (lines.Count == 0) return;
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (IsAccessLog) { var e = AccessEntry.Parse(l); _logLines.Add(e != null ? e.ToString() : l); }
                else _logLines.Add(l);
            }
            if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
            LogText = string.Join(Environment.NewLine, _logLines);
            OnPropertyChanged(nameof(LogIsEmpty));
            LogAppended?.Invoke();
        }

        private void ClearLog()
        {
            try { LogTail.Clear(CurrentLogPath); } catch (IOException ex) { _main.Toast("Couldn't clear it: " + ex.Message); return; }
            RestartLog();
        }

        // ------------------------------------------------------------------ composer

        private string _composerCommand = "install";
        private string _composerOutput = "";
        public string ComposerCommand { get => _composerCommand; set => Set(ref _composerCommand, value); }
        public string ComposerOutput { get => _composerOutput; private set => Set(ref _composerOutput, value); }
        public bool HasComposer => _main.Stack.HasComposer;
        public bool NoComposer => !HasComposer;
        public bool ComposerRunning => _composerCts != null;
        public bool HasComposerJson => Selected != null && File.Exists(Path.Combine(Selected.Project.Folder, "composer.json"));
        public ICommand RunComposerCommand { get; }
        public ICommand StopComposerCommand { get; }
        public event Action ComposerAppended;

        private async Task RunComposerAsync(string command)
        {
            if (Selected == null) return;
            var args = Composer.SplitCommandLine(command);
            if (args.Count == 0) return;
            var sb = new StringBuilder();
            void Add(string line)
            {
                _ui.BeginInvoke(new Action(() =>
                {
                    sb.AppendLine(line);
                    ComposerOutput = sb.ToString();
                    ComposerAppended?.Invoke();
                }));
            }
            _composerCts = new CancellationTokenSource();
            OnPropertyChanged(nameof(ComposerRunning));
            CommandManager.InvalidateRequerySuggested();
            try
            {
                Add("> composer " + string.Join(" ", args));
                int code = await Composer.RunAsync(_paths, _main.Stack, Selected.Project.Folder, args, Add, _composerCts.Token);
                Add(code == 0 ? "Done." : "Composer stopped with exit code " + code + ". The lines above say why.");
                OnPropertyChanged(nameof(HasComposerJson));
                if (code == 0 && _main.WebRunning) await _main.ProjectsChangedAsync();
            }
            catch (OperationCanceledException) { Add("Stopped."); }
            catch (LuminaException ex) { Add(ex.Message); }
            finally
            {
                _composerCts.Dispose();
                _composerCts = null;
                OnPropertyChanged(nameof(ComposerRunning));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        // ------------------------------------------------------------------ snapshots

        private string _snapshotNote = "";
        public string SnapshotNote { get => _snapshotNote; set => Set(ref _snapshotNote, value); }
        public ObservableCollection<SnapshotRow> Snapshots { get; } = new ObservableCollection<SnapshotRow>();
        public bool HasSnapshots => Snapshots.Count > 0;
        public ICommand TakeSnapshotCommand { get; }
        public ICommand RestoreSnapshotCommand { get; }
        public ICommand DeleteSnapshotCommand { get; }
        public ICommand OpenSnapshotsFolderCommand { get; }

        private void LoadSnapshots()
        {
            Snapshots.Clear();
            if (Selected != null)
                foreach (var s in Archive.ListSnapshots(Selected.Name)) Snapshots.Add(new SnapshotRow(s));
            OnPropertyChanged(nameof(HasSnapshots));
        }

        private async Task TakeSnapshotAsync()
        {
            var p = Selected?.Project;
            if (p == null) return;
            IsBusy = true;
            try
            {
                var progress = new Progress<string>(m => BusyText = m);
                var s = await Archive.TakeSnapshotAsync(p, _main.IsRunning ? _main.Server : null, SnapshotNote, progress);
                SnapshotNote = "";
                LoadSnapshots();
                _main.Toast(s.HasDatabase ? "Saved the code and the database." : "Saved the code. Start the database server to include the database next time.");
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (IOException ex) { _main.Toast("Couldn't save the snapshot: " + ex.Message); }
            finally { IsBusy = false; BusyText = null; }
        }

        private async Task RestoreAsync(SnapshotRow row)
        {
            var p = Selected?.Project;
            if (p == null || row == null) return;
            var q = new ChoiceSlip("RESTORE SNAPSHOT", "Back to " + row.When + "?",
                "LuminaDB saves the current state as a new snapshot first, so this can be undone. " +
                (row.HasDatabase ? "Restore the code and the database together, or only the code." : "This snapshot has only the code."),
                false, row.HasDatabase ? "Code and database" : "Restore the code", row.HasDatabase ? "Code only" : null, row.Note);
            var answer = await _main.AskAsync(q);
            if (answer == SlipChoice.Cancel) return;
            bool database = row.HasDatabase && answer == SlipChoice.Primary;
            if (database && !_main.IsRunning) { _main.Toast("Start the database server to restore the database."); return; }
            IsBusy = true;
            try
            {
                var progress = new Progress<string>(m => BusyText = m);
                await Archive.RestoreAsync(p, row.Info, code: true, database: database, _main.IsRunning ? _main.Server : null, progress);
                LoadSnapshots();
                await _main.ProjectsChangedAsync();
                Reload(p.Name);
                if (database) _main.DataChanged();
                _main.Toast("Restored. The state before is kept as the newest snapshot.");
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (IOException ex) { _main.Toast("Couldn't restore: " + ex.Message + " Close programs using the project's files."); }
            catch (UnauthorizedAccessException ex) { _main.Toast("Couldn't restore: " + ex.Message); }
            finally { IsBusy = false; BusyText = null; }
        }

        private async Task DeleteSnapshotAsync(SnapshotRow row)
        {
            var q = new ChoiceSlip("DELETE SNAPSHOT", "Delete this snapshot?", "It is removed for good. The project itself doesn't change.",
                true, "Delete snapshot", null, row.When + " · " + row.Note);
            if (await _main.AskAsync(q) != SlipChoice.Primary) return;
            Archive.DeleteSnapshot(row.Info);
            LoadSnapshots();
        }

        // ------------------------------------------------------------------ submission pack

        private string _studentId;
        private bool _includeVendor;
        private string _lastPack;
        public string StudentId { get => _studentId; set { if (Set(ref _studentId, value)) { OnPropertyChanged(nameof(PackName)); SaveUiSettings(); } } }
        public bool IncludeVendor { get => _includeVendor; set => Set(ref _includeVendor, value); }
        public string PackName => Selected == null ? "" : ProjectArchive.PackName(StudentId, Selected.Name, DateTime.Now);
        public string LastPack { get => _lastPack; private set { if (Set(ref _lastPack, value)) OnPropertyChanged(nameof(HasLastPack)); } }
        public bool HasLastPack => LastPack != null;
        public ICommand BuildPackCommand { get; }
        public ICommand ShowPackCommand { get; }

        private async Task BuildPackAsync()
        {
            var p = Selected?.Project;
            if (p == null) return;
            IsBusy = true;
            try
            {
                var progress = new Progress<string>(m => BusyText = m);
                LastPack = await Archive.BuildSubmissionPackAsync(p, _main.IsRunning ? _main.Server : null, StudentId, IncludeVendor, progress);
                _main.Toast(_main.IsRunning ? "Packed with the database." : "Packed without the database: start the server to include it.");
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (IOException ex) { _main.Toast("Couldn't build the pack: " + ex.Message); }
            finally { IsBusy = false; BusyText = null; }
        }

        // ------------------------------------------------------------------ demo mode

        public ObservableCollection<LanAddress> Addresses { get; } = new ObservableCollection<LanAddress>();
        private LanAddress _address;
        private int _demoMinutes = 30;
        public LanAddress SelectedAddress { get => _address; set { if (Set(ref _address, value)) CommandManager.InvalidateRequerySuggested(); } }
        public int[] DemoMinuteChoices => new[] { 15, 30, 60, 120 };
        public int DemoMinutes { get => _demoMinutes; set => Set(ref _demoMinutes, value); }
        public bool HasAddresses => Addresses.Count > 0;
        public bool NoAddresses => Addresses.Count == 0;
        public bool DemoOn => _main.Web?.Demo != null;
        public bool DemoOff => !DemoOn;
        public bool DemoIsThisProject => DemoOn && Selected != null && _main.Web.Demo.Project == Selected.Name;
        public string DemoUrl => _main.Web?.Demo?.Url ?? "";
        public string DemoProject => _main.Web?.Demo?.Project ?? "";
        public string DemoUntil => _main.Web?.DemoEndsAt is DateTime t ? "Turns itself off at " + t.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" + Math.Max(0, (int)Math.Ceiling((t - DateTime.Now).TotalMinutes)) + " min left)." : "";
        private ImageSource _qr;
        public ImageSource DemoQr { get => _qr; private set => Set(ref _qr, value); }
        public ICommand StartDemoCommand { get; }
        public ICommand StopDemoCommand { get; }
        public ICommand RefreshAddressesCommand { get; }

        private void RefreshAddresses()
        {
            var keep = SelectedAddress?.Address;
            Addresses.Clear();
            foreach (var a in LanAddress.Find()) Addresses.Add(a);
            SelectedAddress = Addresses.FirstOrDefault(a => a.Address == keep) ?? Addresses.FirstOrDefault();
            OnPropertyChanged(nameof(HasAddresses));
            OnPropertyChanged(nameof(NoAddresses));
        }

        private async Task StartDemoAsync()
        {
            var p = Selected?.Project;
            if (p == null || SelectedAddress == null) return;
            var q = new ChoiceSlip("DEMO MODE", "Show " + p.Name + " to the room?",
                "For " + DemoMinutes + " minutes, phones and laptops on the same Wi-Fi can open this project at " + SelectedAddress.Address +
                ". Nothing else is shared: not other projects, not the database. Windows may ask once whether to allow the web server on networks: choose Private networks.",
                false, "Start demo", null, "Error pages show no code to visitors; details stay on this PC.");
            if (await _main.AskAsync(q) != SlipChoice.Primary) return;
            IsBusy = true;
            try
            {
                await _main.Web.StartDemoAsync(p.Name, SelectedAddress.Address, DemoMinutes);
                _demoClock.Start();
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            finally { IsBusy = false; }
        }

        public void OnDemoChanged()
        {
            var url = _main.Web?.Demo?.Url;
            DemoQr = url == null ? null : RenderQr(url);
            if (url == null) _demoClock?.Stop();
            OnPropertyChanged(nameof(DemoOn));
            OnPropertyChanged(nameof(DemoOff));
            OnPropertyChanged(nameof(DemoIsThisProject));
            OnPropertyChanged(nameof(DemoUrl));
            OnPropertyChanged(nameof(DemoProject));
            OnPropertyChanged(nameof(DemoUntil));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>The QR code as a crisp bitmap: one pixel per module, scaled up without smoothing by the view.</summary>
        internal static ImageSource RenderQr(string text)
        {
            var qr = QrCode.EncodeText(text);
            int border = 4, dim = qr.Size + border * 2;
            var pixels = new byte[dim * dim];
            for (int y = 0; y < dim; y++)
                for (int x = 0; x < dim; x++)
                    pixels[y * dim + x] = qr[x - border, y - border] ? (byte)0x1C : (byte)0xFB;
            var bmp = BitmapSource.Create(dim, dim, 96, 96, PixelFormats.Gray8, null, pixels, dim);
            bmp.Freeze();
            return bmp;
        }

        // ------------------------------------------------------------------ settings

        [System.Runtime.Serialization.DataContract]
        private sealed class ProjectsUiSettings
        {
            [System.Runtime.Serialization.DataMember] public string StudentId { get; set; }
            [System.Runtime.Serialization.DataMember] public string Project { get; set; }
        }

        private string SettingsFile => Path.Combine(_paths.Config, "projects-ui.json");

        private void SaveUiSettings()
        {
            try { JsonFile.Write(SettingsFile, new ProjectsUiSettings { StudentId = _studentId, Project = _lastSelected }); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            _logTimer.Stop();
            _demoClock.Stop();
            _composerCts?.Cancel();
        }
    }

    /// <summary>The New project slip: name, template, then progress while it is made.</summary>
    public sealed class NewProjectSlip : SlipViewModel
    {
        private readonly IReadOnlyCollection<string> _existing;
        private string _name = "";
        private string _progress;

        public NewProjectSlip(Action close, IReadOnlyList<ProjectTemplate> templates, IReadOnlyCollection<string> existing, bool serverRunning,
            Func<string, string, IProgress<string>, Task> create) : base(close)
        {
            _existing = existing;
            Eyebrow = "NEW PROJECT";
            Big = "Name it.";
            Text = "It becomes the folder, the database and the web address: lowercase letters, digits and hyphens, like my-shop.";
            foreach (var t in templates) Templates.Add(new TemplateChoice(t));
            Templates.First().IsSelected = true;
            ServerNote = serverRunning ? null : "Start the database server first: every project gets its own database.";
            SelectTemplateCommand = new RelayCommand(p =>
            {
                if (!(p is TemplateChoice c) || !c.IsAvailable) return;
                foreach (var t in Templates) t.IsSelected = t == c;
            });
            CreateCommand = new AsyncCommand(async () =>
            {
                Problem = ProjectNames.Problem(Name, _existing);
                if (Problem != null) return;
                IsBusy = true;
                try
                {
                    await create(Name, Templates.First(t => t.IsSelected).Template.Id, new Progress<string>(m => ProgressText = m));
                    Close();
                }
                catch (LuminaException ex) { Problem = ex.Message; }
                catch (MySqlConnector.MySqlException ex) { Problem = ex.Message; }
                catch (IOException ex) { Problem = "Couldn't write the files: " + ex.Message; }
                catch (UnauthorizedAccessException ex) { Problem = "Couldn't write the files: " + ex.Message; }
                finally { IsBusy = false; ProgressText = null; }
            }, () => !IsBusy && serverRunning && ProjectNames.Problem(Name, _existing) == null);
        }

        public ObservableCollection<TemplateChoice> Templates { get; } = new ObservableCollection<TemplateChoice>();
        public string ServerNote { get; }
        public bool HasServerNote => ServerNote != null;
        public ICommand SelectTemplateCommand { get; }
        public ICommand CreateCommand { get; }
        public string ProgressText { get => _progress; private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(HasProgress)); } }
        public bool HasProgress => !string.IsNullOrEmpty(_progress);
        public string DatabaseHint => string.IsNullOrEmpty(_name) ? "" : "Database " + ProjectNames.DatabaseFor(_name) + " · http://" + _name + ".localhost";

        public string Name
        {
            get => _name;
            set
            {
                if (!Set(ref _name, value ?? "")) return;
                Problem = _name.Length == 0 ? null : ProjectNames.Problem(_name, _existing);
                OnPropertyChanged(nameof(DatabaseHint));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
