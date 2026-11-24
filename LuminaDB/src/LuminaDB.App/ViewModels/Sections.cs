using System.Collections.Generic;
using System.Windows.Input;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One binder index tab (SPEC §5: twelve sections, F1–F12).</summary>
    public sealed class Section : ObservableObject
    {
        private bool _isSelected;
        private bool _hasAlert;

        public Section(string key, string title, Key fKey, int colorIndex, int buildStep, string heading, string[] plan)
        {
            Key = key; Title = title; FKey = fKey; ColorIndex = colorIndex; BuildStep = buildStep; Heading = heading; Plan = plan;
        }

        public string Key { get; }
        public string Title { get; }
        /// <summary>Title case, not capitals: the side-tab label, read left to right.</summary>
        public string TabLabel => Title;
        public Key FKey { get; }
        public string FKeyText => FKey.ToString();
        /// <summary>1–5: which binder-tab colour.</summary>
        public int ColorIndex { get; }
        /// <summary>SPEC §11 build step that delivers this section (2 = done in the shell).</summary>
        public int BuildStep { get; }
        public string Heading { get; }
        public string[] Plan { get; }
        public bool IsBuilt => BuildStep <= 6;

        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        /// <summary>Red dot on the tab (e.g. Doctor has issues).</summary>
        public bool HasAlert { get => _hasAlert; set => Set(ref _hasAlert, value); }

        public static List<Section> All() => new List<Section>
        {
            new Section("today", "Today", System.Windows.Input.Key.F1, 1, 2, "", new string[0]),
            new Section("projects", "Projects", System.Windows.Input.Key.F2, 2, 5, "Build PHP projects and open them in the browser.", new[]
            {
                "New project → folder, database and config.php made for you",
                "Open at http://<name>.localhost — no hosts file, no Apache config",
                "Friendly PHP error page with the line and a plain explanation",
                "Composer, per-project PHP settings, live logs",
                "Snapshot code + database together; submission pack zip",
            }),
            new Section("notebook", "Notebook", System.Windows.Input.Key.F3, 3, 3, "Write SQL and see mistakes before you run them.", new[]
            {
                "Margin corrections while typing: misspelt commands, tables, columns",
                "Safe updates: DELETE/UPDATE without WHERE asks first, with Undo",
                "Results grid that stays fast at 100,000 rows",
                "Receipt tape of everything that ran",
            }),
            new Section("tables", "Tables", System.Windows.Input.Key.F4, 4, 4, "Browse, edit and design tables without writing SQL.", new[]
            {
                "Browse and edit rows inline, with foreign-key lookups",
                "Structure editor: columns, keys, indexes, foreign keys",
                "Views, procedures, functions, triggers, events",
            }),
            new Section("diagram", "Diagram", System.Windows.Input.Key.F5, 5, 8, "See how tables connect.", new[]
            {
                "Diagram from an existing database",
                "Draw tables and relations, then create them",
                "Export as PNG/SVG for assignments",
            }),
            new Section("import", "Import", System.Windows.Input.Key.F6, 1, 7, "Bring in .sql dumps from XAMPP, Workbench and phpMyAdmin.", new[]
            {
                "MySQL 8 dumps fixed automatically (utf8mb4_0900_ai_ci and friends)",
                "Any file size, with progress",
                "Export .sql, CSV and Excel",
            }),
            new Section("users", "Users", System.Windows.Input.Key.F7, 2, 9, "Accounts and permissions, explained.", new[]
            {
                "Create users for each project with the right privileges",
                "Reset the root password on every root account at once",
            }),
            new Section("server", "Server", System.Windows.Input.Key.F8, 3, 9, "What the database server is doing.", new[]
            {
                "Uptime, connections, queries per second",
                "Connections with Kill, variables with explanations",
                "Error and slow query logs; mariadb console",
            }),
            new Section("doctor", "Doctor", System.Windows.Input.Key.F9, 4, 6, "The things that usually break XAMPP, checked and fixed.", new[]
            {
                "Every check with a plain explanation and a safe fix",
                "A backup is always taken before any repair",
                "Antivirus, ports, crashes, paths, disk space, broken config",
            }),
            new Section("backups", "Backups", System.Windows.Input.Key.F10, 5, 6, "Every change can be taken back.", new[]
            {
                "Automatic backups every 30 minutes and before risky changes",
                "Restore always backs up the current state first",
            }),
            new Section("mail", "Mail", System.Windows.Input.Key.F11, 1, 5, "See the emails your PHP projects send.", new[]
            {
                "A local mail catcher: nothing leaves the PC",
                "HTML preview, source and attachments",
            }),
            new Section("settings", "Settings", System.Windows.Input.Key.F12, 2, 9, "Sensible by default. Most people never come here.", new[]
            {
                "Ports, start with Windows, root password, backup schedule",
                "Move the data folder safely; theme and font size",
            }),
        };
    }

    public sealed class SelectSectionCommand : ICommand
    {
        private readonly System.Action<string> _select;
        public SelectSectionCommand(System.Action<string> select) { _select = select; }
        public event System.EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _select(parameter as string);
    }
}
