using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;

namespace LuminaStudio
{
    // PROTOTYPE: everything here runs on mock data. No real MariaDB is started or queried.
    public partial class MainWindow : Window
    {
        static Brush B(string key) => (Brush)Application.Current.Resources[key];

        readonly ObservableCollection<CheckItem> _checks = new ObservableCollection<CheckItem>();
        readonly ObservableCollection<LogEntry> _log = new ObservableCollection<LogEntry>();
        readonly ObservableCollection<BackupItem> _backups = new ObservableCollection<BackupItem>();
        readonly Dictionary<string, DataTable> _tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
        readonly Stack<KeyValuePair<string, DataTable>> _undo = new Stack<KeyValuePair<string, DataTable>>();
        readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        bool _serverRunning = true;
        string _pendingSql;
        string _guardFixSql;
        int _logNo;

        static readonly string[] Keywords =
        {
            "SELECT","FROM","WHERE","INSERT","INTO","VALUES","UPDATE","SET","DELETE","CREATE","TABLE","DROP",
            "ALTER","JOIN","LEFT","INNER","ON","GROUP","BY","ORDER","LIMIT","AND","OR","NOT","NULL","AS",
            "DESCRIBE","SHOW","TABLES","USE","DATABASE","PRIMARY","KEY","AUTO_INCREMENT","INT","VARCHAR",
            "DECIMAL","DATE","FOREIGN","REFERENCES","DISTINCT","COUNT","AVG","SUM","DESC","ASC","HAVING","IN","LIKE"
        };

        public MainWindow()
        {
            InitializeComponent();
            SeedMockData();

            Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
            Editor.Text = "-- Welcome! Press Ctrl+Enter to run.\nSELECT id, name, email, gpa\nFROM students\nWHERE gpa >= 3.0\nORDER BY gpa DESC;";
            Editor.TextChanged += (s, e) => UpdateGuard();
            Editor.PreviewKeyDown += Editor_PreviewKeyDown;

            ChecksList.ItemsSource = _checks;
            OutputGrid.ItemsSource = _log;
            BackupGrid.ItemsSource = _backups;
            ResultGrid.AutoGeneratingColumn += (s, e) => { e.Column.Header = e.PropertyName.Replace("_", "__"); e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star); };

            _toastTimer.Tick += (s, e) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

            BuildSchemaTree();
            UpdateGuard();
            UpdateDoctorSummary();
            ShowTable("students", "SELECT * FROM students");
            AddLog(true, "Server started", "MariaDB 10.4 ready on 127.0.0.1:3307 (3306 busy → switched)", "1.8 s");
        }

        // ------------------------------------------------------------------ mock data
        void SeedMockData()
        {
            var students = new DataTable("students");
            students.Columns.Add("id", typeof(int));
            students.Columns.Add("name", typeof(string));
            students.Columns.Add("email", typeof(string));
            students.Columns.Add("gpa", typeof(decimal));
            students.Columns.Add("course_id", typeof(int));
            string[] names = { "Nimal Perera", "Ayesha Fernando", "Kasun Silva", "Tharushi Jayasinghe", "Ravindu Bandara",
                               "Sanduni Wickramasinghe", "Dilan Rathnayake", "Hiruni Gunawardena", "Malith Senanayake",
                               "Fathima Rizna", "Chamod Herath", "Ishara Dissanayake" };
            decimal[] gpas = { 3.62m, 3.91m, 2.74m, 3.45m, 2.98m, 3.80m, 3.12m, 3.55m, 2.41m, 3.70m, 3.05m, 2.88m };
            for (int i = 0; i < names.Length; i++)
            {
                var first = names[i].Split(' ')[0].ToLowerInvariant();
                students.Rows.Add(i + 1, names[i], first + "@student.lk", gpas[i], (i % 3) + 1);
            }
            _tables["students"] = students;

            var courses = new DataTable("courses");
            courses.Columns.Add("id", typeof(int));
            courses.Columns.Add("title", typeof(string));
            courses.Columns.Add("credits", typeof(int));
            courses.Rows.Add(1, "Database Management Systems", 4);
            courses.Rows.Add(2, "Web Programming", 3);
            courses.Rows.Add(3, "Software Engineering", 4);
            _tables["courses"] = courses;

            var lecturers = new DataTable("lecturers");
            lecturers.Columns.Add("id", typeof(int));
            lecturers.Columns.Add("name", typeof(string));
            lecturers.Columns.Add("department", typeof(string));
            lecturers.Rows.Add(1, "Dr. S. Kumara", "IT");
            lecturers.Rows.Add(2, "Ms. P. Abeysekara", "IT");
            _tables["lecturers"] = lecturers;

            var enrollments = new DataTable("enrollments");
            enrollments.Columns.Add("student_id", typeof(int));
            enrollments.Columns.Add("course_id", typeof(int));
            enrollments.Columns.Add("year", typeof(int));
            for (int i = 1; i <= 12; i++) enrollments.Rows.Add(i, (i % 3) + 1, 2026);
            _tables["enrollments"] = enrollments;

            foreach (var t in new[] { "products", "orders", "customers" })
            {
                var dt = new DataTable(t);
                dt.Columns.Add("id", typeof(int));
                dt.Columns.Add("name", typeof(string));
                dt.Rows.Add(1, "Sample " + t.TrimEnd('s'));
                _tables[t] = dt;
            }

            DbGrid.ItemsSource = new[]
            {
                new { Name = "college", Tables = 4, Rows = 29, Size = "96 KB", Changed = "Today 11:02" },
                new { Name = "shop", Tables = 3, Rows = 3, Size = "48 KB", Changed = "Yesterday" },
                new { Name = "hndit_assignment_2", Tables = 6, Rows = 140, Size = "212 KB", Changed = "Nov 18" },
            };

            _checks.Add(new CheckItem("Port 3306 is busy", "Skype.exe (PID 4120) is using it. LuminaDB switched to 3307 automatically, so nothing is broken.",
                CheckStatus.Warn, "Free port 3306", "Port 3306 is free. Server will use 3306 next start."));
            _checks.Add(new CheckItem("Last shutdown was not clean", "The PC lost power at 10:42. 2 tables in 'college' may be damaged (aria log).",
                CheckStatus.Bad, "Repair (backup first)", "Backed up, then repaired 2 tables. No data lost."));
            _checks.Add(new CheckItem("Antivirus blocked mysqld.exe", "Windows Defender quarantined it once today. The server may fail to start next time.",
                CheckStatus.Bad, "Restore + allow", "Restored from the bundle and added an exclusion for the LuminaDB folder."));
            _checks.Add(new CheckItem("Visual C++ runtime", "Bundled inside LuminaDB. Nothing to install.", CheckStatus.Ok));
            _checks.Add(new CheckItem("Data folder path", "D:\\LuminaDB\\data has no spaces or special characters.", CheckStatus.Ok));
            _checks.Add(new CheckItem("Disk space", "18.4 GB free on D:.", CheckStatus.Ok));
            _checks.Add(new CheckItem("Firewall", "Server only listens on 127.0.0.1, so Windows Firewall will not ask.", CheckStatus.Ok));
            _checks.Add(new CheckItem("Config file (my.ini)", "Valid. Managed by LuminaDB; you never need to edit it.", CheckStatus.Ok));

            _backups.Add(new BackupItem("Today 11:20", "Automatic (every 30 min)", "college, shop", "142 KB"));
            _backups.Add(new BackupItem("Today 10:58", "Before DROP TABLE marks", "college", "118 KB"));
            _backups.Add(new BackupItem("Today 10:50", "Automatic (every 30 min)", "college, shop", "140 KB"));
            _backups.Add(new BackupItem("Yesterday 16:05", "App closed", "college, shop, hndit_assignment_2", "352 KB"));
        }

        void BuildSchemaTree()
        {
            SchemaTree.Items.Clear();
            var schemas = new Dictionary<string, string[]>
            {
                { "college", new[] { "students", "courses", "enrollments", "lecturers" } },
                { "shop", new[] { "products", "orders", "customers" } },
            };
            foreach (var kv in schemas)
            {
                var db = new TreeViewItem { Header = TreeHeader("\uE8B7", kv.Key, true), IsExpanded = kv.Key == "college" };
                foreach (var t in kv.Value)
                {
                    var ti = new TreeViewItem { Header = TreeHeader("\uE80A", t, false), Tag = t };
                    if (_tables.TryGetValue(t, out var dt))
                        foreach (DataColumn c in dt.Columns)
                            ti.Items.Add(new TreeViewItem { Header = ColumnHeader(c), Focusable = false });
                    db.Items.Add(ti);
                }
                SchemaTree.Items.Add(db);
            }
        }

        static StackPanel TreeHeader(string glyph, string text, bool bold)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = bold ? B("Accent") : B("Muted"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 0), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
            return sp;
        }

        static StackPanel ColumnHeader(DataColumn c)
        {
            var type = c.DataType == typeof(int) ? "INT" : c.DataType == typeof(decimal) ? "DECIMAL(3,2)" : "VARCHAR(100)";
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = c.ColumnName == "id" ? "\uE192" : "\uE8A4", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = c.ColumnName == "id" ? B("Warn") : B("Muted"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = c.ColumnName, Margin = new Thickness(6, 0, 0, 0) });
            sp.Children.Add(new TextBlock { Text = type, Margin = new Thickness(6, 0, 0, 0), Foreground = B("Muted"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        // ------------------------------------------------------------------ navigation
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageDashboard == null) return; // fires during InitializeComponent
            PageDashboard.Visibility = NavDashboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageQuery.Visibility = NavQuery.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageDoctor.Visibility = NavDoctor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageBackups.Visibility = NavBackups.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (NavDashboard.IsChecked == true) SetTitle("Dashboard", "Your local database server at a glance");
            else if (NavQuery.IsChecked == true) { SetTitle("SQL Editor", "Write MySQL. Mistakes are caught before they run."); Editor.Focus(); }
            else if (NavDoctor.IsChecked == true) SetTitle("Doctor", "Finds and fixes the problems that usually break local MySQL");
            else if (NavBackups.IsChecked == true) SetTitle("Backups", "Every change is recoverable");
            else SetTitle("Settings", "Sensible defaults. Most people never change these.");
        }

        void SetTitle(string t, string s) { PageTitle.Text = t; PageSubtitle.Text = s; }
        void GoQuery_Click(object sender, RoutedEventArgs e) => NavQuery.IsChecked = true;
        void GoDoctor_Click(object sender, RoutedEventArgs e) => NavDoctor.IsChecked = true;
        void GoBackups_Click(object sender, RoutedEventArgs e) => NavBackups.IsChecked = true;

        // ------------------------------------------------------------------ server
        void ServerToggle_Click(object sender, RoutedEventArgs e)
        {
            _serverRunning = !_serverRunning;
            if (_serverRunning)
            {
                ServerPill.Background = B("OkSoft"); ServerDot.Fill = B("Ok");
                ServerPillText.Text = "MariaDB running · 127.0.0.1:3307"; ServerPillText.Foreground = new SolidColorBrush(Color.FromRgb(0x02, 0x7A, 0x48));
                ServerToggleIcon.Text = "\uE71A"; ServerToggleIcon.Foreground = B("Bad"); ServerToggleText.Text = "Stop server";
                DashServerBadge.Background = B("OkSoft"); DashServerBadgeText.Text = "Running"; DashServerBadgeText.Foreground = ServerPillText.Foreground;
                DashServerDetail.Text = "Port 3307 (3306 was busy, switched automatically) · up 0m";
                AddLog(true, "Server started", "MariaDB 10.4 ready on 127.0.0.1:3307", "1.6 s");
                ShowToast("Server started on port 3307");
            }
            else
            {
                ServerPill.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)); ServerDot.Fill = B("Muted");
                ServerPillText.Text = "Server stopped"; ServerPillText.Foreground = B("Muted");
                ServerToggleIcon.Text = "\uE768"; ServerToggleIcon.Foreground = B("Ok"); ServerToggleText.Text = "Start server";
                DashServerBadge.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)); DashServerBadgeText.Text = "Stopped"; DashServerBadgeText.Foreground = B("Muted");
                DashServerDetail.Text = "Stopped cleanly (SHUTDOWN sent, all data flushed to disk).";
                AddLog(true, "Server stopped", "Clean shutdown, data flushed to disk", "0.9 s");
                ShowToast("Server stopped cleanly. Safe to turn off the PC.");
            }
        }

        void CopyConn_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText("mysql://root@127.0.0.1:3307/college"); ShowToast("Copied: mysql://root@127.0.0.1:3307/college"); }
            catch { ShowToast("Could not access the clipboard", false); }
        }

        void NewDb_Click(object sender, RoutedEventArgs e)
        {
            NavQuery.IsChecked = true;
            Editor.Text = "CREATE DATABASE my_new_db;\nUSE my_new_db;";
            ShowToast("Template inserted. Press Ctrl+Enter to create it.");
        }

        void Import_Click(object sender, RoutedEventArgs e) =>
            ShowToast("Import: pick a .sql file (phpMyAdmin/Workbench dumps work). Not wired in prototype.");

        // ------------------------------------------------------------------ SQL editor
        void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Run_Click(sender, e);
            }
        }

        void SchemaTree_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SchemaTree.SelectedItem is TreeViewItem ti && ti.Tag is string table)
            {
                Editor.Text = $"SELECT * FROM {table} LIMIT 100;";
                Run_Click(sender, e);
            }
        }

        void Refresh_Click(object sender, RoutedEventArgs e) { BuildSchemaTree(); ShowToast("Schema refreshed"); }

        void Format_Click(object sender, RoutedEventArgs e)
        {
            var sql = Regex.Replace(Editor.Text, @"\s+", " ").Trim();
            foreach (var kw in Keywords)
                sql = Regex.Replace(sql, $@"\b{kw}\b", kw, RegexOptions.IgnoreCase);
            sql = Regex.Replace(sql, @"\s+(FROM|WHERE|ORDER BY|GROUP BY|HAVING|LIMIT|LEFT JOIN|INNER JOIN|JOIN|SET|VALUES)\b", "\n$1");
            sql = Regex.Replace(sql, @";\s*", ";\n\n").TrimEnd();
            Editor.Text = sql;
            ShowToast("Formatted");
        }

        void Templates_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            void Add(string label, string sql)
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (s, a) => { Editor.Text = sql; Editor.Focus(); };
                menu.Items.Add(mi);
            }
            Add("SELECT with JOIN", "SELECT s.name, c.title\nFROM students s\nJOIN courses c ON c.id = s.course_id\nORDER BY s.name;");
            Add("GROUP BY with average", "SELECT course_id, COUNT(*) AS students, AVG(gpa) AS avg_gpa\nFROM students\nGROUP BY course_id;");
            Add("CREATE TABLE (with keys)", "CREATE TABLE marks (\n  id INT AUTO_INCREMENT PRIMARY KEY,\n  student_id INT NOT NULL,\n  subject VARCHAR(50) NOT NULL,\n  score DECIMAL(5,2),\n  FOREIGN KEY (student_id) REFERENCES students(id)\n);");
            Add("INSERT a row", "INSERT INTO students (name, email, gpa, course_id)\nVALUES ('New Student', 'new@student.lk', 3.20, 1);");
            Add("Try a mistake: typo in table", "SELECT * FROM studnets;");
            Add("Try a mistake: DELETE without WHERE", "DELETE FROM students;");
            Add("Try a mistake: trailing comma", "SELECT id, name, FROM students;");
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
        }

        void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undo.Count == 0) { ShowToast("Nothing to undo", false); return; }
            var snap = _undo.Pop();
            _tables[snap.Key] = snap.Value;
            ShowTable(snap.Key, "Undo");
            AddLog(true, "Undo", $"Restored '{snap.Key}' from snapshot ({snap.Value.Rows.Count} rows)", "0.01 s");
            ShowToast($"Restored {snap.Value.Rows.Count} rows in '{snap.Key}'");
        }

        // Live analysis while typing: the core "error prevention" idea.
        Guard Analyze(string sql)
        {
            var s = StripComments(sql).Trim();
            if (s.Length == 0) return new Guard(CheckStatus.Ok, "Ready", "Type a query, or pick one from Templates.");

            var first = Regex.Match(s, @"^\s*(\w+)").Groups[1].Value;
            var knownStarts = new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "SHOW", "DESCRIBE", "USE", "WITH" };
            // Only judge the first word once the user has finished typing it (followed by a space)
            // and it isn't just the start of a valid command (e.g. "SEL" while typing SELECT).
            bool firstWordFinished = Regex.IsMatch(s, @"^\s*\w+\s");
            bool isPrefixOfKnown = knownStarts.Any(k => k.StartsWith(first.ToUpperInvariant(), StringComparison.Ordinal));
            if (!firstWordFinished && isPrefixOfKnown)
                return new Guard(CheckStatus.Ok, "Keep typing...", "Checks run as soon as the command word is complete.");
            if (first.Length >= 3 && firstWordFinished && !knownStarts.Contains(first.ToUpperInvariant()))
            {
                var sug = Closest(first.ToUpperInvariant(), knownStarts);
                if (sug != null)
                    return new Guard(CheckStatus.Bad, $"Unknown command \"{first}\"", $"Did you mean {sug}?", $"Change to {sug}",
                        Regex.Replace(sql, $@"\b{Regex.Escape(first)}\b", sug));
            }

            if (Regex.IsMatch(s, @",\s*FROM\b", RegexOptions.IgnoreCase))
                return new Guard(CheckStatus.Bad, "Extra comma before FROM", "MySQL error 1064 would happen here. Remove the comma after the last column.",
                    "Remove comma", Regex.Replace(sql, @",(\s*FROM\b)", "$1", RegexOptions.IgnoreCase));

            if (CountOf(s, '\'') % 2 != 0)
                return new Guard(CheckStatus.Bad, "A text value is missing its closing quote '", "Every 'text' needs a quote on both sides.");

            if (CountOf(s, '(') != CountOf(s, ')'))
                return new Guard(CheckStatus.Bad, "Brackets don't match", $"{CountOf(s, '(')} opening vs {CountOf(s, ')')} closing brackets.");

            foreach (Match m in Regex.Matches(s, @"\b(?:FROM|JOIN|INTO|UPDATE)\s+`?(\w+)`?", RegexOptions.IgnoreCase))
            {
                var t = m.Groups[1].Value;
                if (Regex.IsMatch(s, @"^\s*CREATE\s+TABLE", RegexOptions.IgnoreCase)) break;
                if (!_tables.ContainsKey(t))
                {
                    var sug = Closest(t, _tables.Keys.ToArray());
                    return sug != null
                        ? new Guard(CheckStatus.Bad, $"Table \"{t}\" doesn't exist", $"Did you mean \"{sug}\"? (would be MySQL error 1146)", $"Use {sug}",
                            Regex.Replace(sql, $@"\b{Regex.Escape(t)}\b", sug))
                        : new Guard(CheckStatus.Bad, $"Table \"{t}\" doesn't exist", "Check the schema list on the left.");
                }
            }

            var dm = Regex.Match(s, @"^\s*(DELETE\s+FROM|UPDATE)\s+`?(\w+)`?", RegexOptions.IgnoreCase);
            if (dm.Success && !Regex.IsMatch(s, @"\bWHERE\b", RegexOptions.IgnoreCase))
            {
                var t = dm.Groups[2].Value;
                int n = _tables.TryGetValue(t, out var dt) ? dt.Rows.Count : 0;
                var verb = dm.Groups[1].Value.ToUpperInvariant().StartsWith("DELETE") ? "delete" : "change";
                return new Guard(CheckStatus.Warn, $"No WHERE: this will {verb} ALL {n} rows in {t}", "Safe updates will ask you before running. Add WHERE to target specific rows.",
                    "Add WHERE id = 1", s.TrimEnd(';', ' ') + " WHERE id = 1;");
            }

            if (Regex.IsMatch(s, @"^\s*DROP\s+(TABLE|DATABASE)", RegexOptions.IgnoreCase))
                return new Guard(CheckStatus.Warn, "DROP permanently removes it", "A snapshot will be taken first so you can Undo.");

            var tm = Regex.Match(s, @"\bFROM\s+`?(\w+)`?", RegexOptions.IgnoreCase);
            var tbl = tm.Success ? tm.Groups[1].Value : "the table";
            if (first.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var parts = new List<string> { $"Reads rows from {tbl}" };
                if (Regex.IsMatch(s, @"\bJOIN\b", RegexOptions.IgnoreCase)) parts.Add("combines a related table");
                if (Regex.IsMatch(s, @"\bWHERE\b", RegexOptions.IgnoreCase)) parts.Add("keeps only matching rows");
                if (Regex.IsMatch(s, @"\bGROUP\s+BY\b", RegexOptions.IgnoreCase)) parts.Add("groups them");
                if (Regex.IsMatch(s, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase)) parts.Add("sorts them");
                return new Guard(CheckStatus.Ok, "Looks good", string.Join(", ", parts) + ". Read-only, safe to run.");
            }
            return new Guard(CheckStatus.Ok, "Looks good", "No problems found. Changes are snapshotted, so you can Undo.");
        }

        void UpdateGuard()
        {
            var g = Analyze(Editor.Text);
            GuardTitle.Text = g.Title;
            GuardDetail.Text = g.Detail;
            GuardIcon.Text = Glyph(g.Status);
            GuardIcon.Foreground = StatusBrush(g.Status);
            GuardBar.Background = SoftBrush(g.Status);
            _guardFixSql = g.FixSql;
            GuardFixBtn.Visibility = g.FixSql != null ? Visibility.Visible : Visibility.Collapsed;
            GuardFixBtn.Content = g.FixLabel ?? "";
        }

        void GuardFix_Click(object sender, RoutedEventArgs e)
        {
            if (_guardFixSql == null) return;
            Editor.Text = _guardFixSql;
            ShowToast("Fixed. Review it, then press Ctrl+Enter.");
        }

        void Run_Click(object sender, RoutedEventArgs e)
        {
            var text = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Text;
            var statements = SplitStatements(text);
            if (statements.Count == 0) { ShowToast("Nothing to run", false); return; }

            if (!_serverRunning)
            {
                AddLog(false, Short(statements[0]), "Server is stopped. Click 'Start server' (top right).", "-");
                FocusOutput();
                return;
            }

            foreach (var st in statements)
            {
                var g = Analyze(st);
                if (g.Status == CheckStatus.Warn && SafeModeCheck.IsChecked == true && Regex.IsMatch(st, @"^\s*(DELETE|UPDATE|DROP)", RegexOptions.IgnoreCase))
                {
                    _pendingSql = st;
                    _guardFixSql = g.FixSql;
                    ConfirmTitle.Text = g.Title;
                    ConfirmMessage.Text = g.Detail;
                    ConfirmSql.Text = st.Trim();
                    ConfirmAltBtn.Visibility = g.FixSql != null ? Visibility.Visible : Visibility.Collapsed;
                    ConfirmOverlay.Visibility = Visibility.Visible;
                    return;
                }
                if (!Execute(st)) break; // stop on first error, like Workbench
            }
        }

        bool Execute(string st)
        {
            var g = Analyze(st);
            var s = StripComments(st).Trim().TrimEnd(';');
            if (g.Status == CheckStatus.Bad)
            {
                AddLog(false, Short(s), g.Title + ". " + g.Detail, "-");
                FocusOutput();
                return false;
            }

            var ms = (0.3 + new Random().NextDouble() * 3).ToString("0.0", CultureInfo.InvariantCulture) + " ms";

            var sel = Regex.Match(s, @"^SELECT\b[\s\S]*?\bFROM\s+`?(\w+)`?", RegexOptions.IgnoreCase);
            if (sel.Success)
            {
                var dt = _tables[sel.Groups[1].Value];
                var view = dt.Copy();
                var w = Regex.Match(s, @"\bWHERE\s+(\w+)\s*(>=|<=|=|>|<)\s*'?([\w.@]+)'?", RegexOptions.IgnoreCase);
                if (w.Success && view.Columns.Contains(w.Groups[1].Value))
                {
                    try
                    {
                        var col = w.Groups[1].Value; var op = w.Groups[2].Value; var val = w.Groups[3].Value;
                        var isText = view.Columns[col].DataType == typeof(string);
                        view = view.Select($"[{col}] {op} {(isText ? "'" + val.Replace("'", "''") + "'" : val)}").CopyToDataTableOrEmpty(view);
                    }
                    catch { /* prototype: ignore filters it can't evaluate */ }
                }
                if (Regex.IsMatch(s, @"ORDER\s+BY\s+gpa\s+DESC", RegexOptions.IgnoreCase) && view.Columns.Contains("gpa"))
                {
                    view.DefaultView.Sort = "gpa DESC";
                    view = view.DefaultView.ToTable();
                }
                ShowTable(view, s);
                AddLog(true, Short(s), $"{view.Rows.Count} row(s) returned", ms);
                return true;
            }

            var del = Regex.Match(s, @"^DELETE\s+FROM\s+`?(\w+)`?(?:\s+WHERE\s+(\w+)\s*=\s*'?([\w.@]+)'?)?", RegexOptions.IgnoreCase);
            if (del.Success)
            {
                var name = del.Groups[1].Value;
                var dt = _tables[name];
                _undo.Push(new KeyValuePair<string, DataTable>(name, dt.Copy()));
                int affected;
                if (del.Groups[2].Success && dt.Columns.Contains(del.Groups[2].Value))
                {
                    var rows = dt.AsEnumerable().Where(r => string.Equals(Convert.ToString(r[del.Groups[2].Value], CultureInfo.InvariantCulture), del.Groups[3].Value, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var r in rows) dt.Rows.Remove(r);
                    affected = rows.Count;
                }
                else { affected = dt.Rows.Count; dt.Rows.Clear(); }
                ShowTable(name, s);
                AddLog(true, Short(s), $"{affected} row(s) affected · snapshot saved (Undo available)", ms);
                ShowToast($"{affected} row(s) deleted. Undo is available.");
                return true;
            }

            if (Regex.IsMatch(s, @"^UPDATE\b", RegexOptions.IgnoreCase))
            {
                AddLog(true, Short(s), "Row(s) updated · snapshot saved (Undo available)", ms);
                return true;
            }
            if (Regex.IsMatch(s, @"^INSERT\b", RegexOptions.IgnoreCase))
            {
                var t = Regex.Match(s, @"INTO\s+`?(\w+)`?", RegexOptions.IgnoreCase).Groups[1].Value;
                if (t.Equals("students", StringComparison.OrdinalIgnoreCase))
                {
                    var dt = _tables["students"];
                    _undo.Push(new KeyValuePair<string, DataTable>("students", dt.Copy()));
                    var nextId = dt.AsEnumerable().Select(r => r.Field<int>("id")).DefaultIfEmpty(0).Max() + 1;
                    dt.Rows.Add(nextId, "New Student", "new@student.lk", 3.20m, 1);
                    ShowTable("students", s);
                }
                AddLog(true, Short(s), "1 row(s) affected · new id generated by AUTO_INCREMENT", ms);
                return true;
            }
            if (Regex.IsMatch(s, @"^CREATE\s+TABLE\s+`?(\w+)`?", RegexOptions.IgnoreCase))
            {
                var t = Regex.Match(s, @"^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?`?(\w+)`?", RegexOptions.IgnoreCase).Groups[1].Value;
                if (_tables.ContainsKey(t))
                {
                    AddLog(false, Short(s), $"Table '{t}' already exists. Use CREATE TABLE IF NOT EXISTS, or pick another name.", "-");
                    FocusOutput();
                    return false;
                }
                var dt = new DataTable(t); dt.Columns.Add("id", typeof(int));
                _tables[t] = dt;
                BuildSchemaTree();
                AddLog(true, Short(s), $"Table '{t}' created · shown in the schema list", ms);
                return true;
            }

            AddLog(true, Short(s), "OK", ms);
            return true;
        }

        void ConfirmCancel_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            AddLog(false, Short(_pendingSql), "Cancelled by you. Nothing was changed.", "-");
            _pendingSql = null;
        }

        void ConfirmAlt_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            if (_guardFixSql != null) Editor.Text = _guardFixSql;
            _pendingSql = null;
            Editor.Focus();
            ShowToast("Added WHERE id = 1. Change the condition, then run.");
        }

        void ConfirmRun_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            if (_pendingSql != null) Execute(_pendingSql);
            _pendingSql = null;
        }

        void ShowTable(string name, string sql) => ShowTable(_tables[name], sql);

        void ShowTable(DataTable dt, string sql)
        {
            ResultGrid.ItemsSource = dt.DefaultView;
            ResultTabHeader.Text = $"Result ({dt.Rows.Count})";
            ResultEmpty.Visibility = dt.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultEmptyText.Text = "0 rows";
            ResultTabs.SelectedIndex = 0;
        }

        void FocusOutput() => ResultTabs.SelectedIndex = 1;

        void AddLog(bool ok, string action, string message, string duration)
        {
            _log.Insert(0, new LogEntry
            {
                No = ++_logNo,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Action = action,
                Message = message,
                Duration = duration,
                Glyph = ok ? "\uE73E" : "\uEA39",
                Brush = ok ? B("Ok") : B("Bad")
            });
            int errors = _log.Count(l => l.Glyph == "\uEA39");
            OutputTabHeader.Text = errors > 0 ? $"Output ({_log.Count}, {errors} error{(errors == 1 ? "" : "s")})" : $"Output ({_log.Count})";
        }

        // ------------------------------------------------------------------ doctor
        void CheckFix_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is CheckItem item) FixCheck(item);
        }

        void FixCheck(CheckItem item)
        {
            if (item.Status == CheckStatus.Ok) return;
            if (item.Title.StartsWith("Last shutdown"))
                _backups.Insert(0, new BackupItem(DateTime.Now.ToString("'Today' HH:mm"), "Before repair", "college", "120 KB"));
            item.Fix();
            UpdateDoctorSummary();
            ShowToast(item.Detail);
        }

        void FixAll_Click(object sender, RoutedEventArgs e)
        {
            var pending = _checks.Where(c => c.Status != CheckStatus.Ok).ToList();
            if (pending.Count == 0) { ShowToast("Everything is already healthy"); return; }
            foreach (var c in pending) FixCheck(c);
            ShowToast($"Fixed {pending.Count} issue(s). A backup was taken first.");
        }

        void RunChecks_Click(object sender, RoutedEventArgs e) => ShowToast($"Checked {_checks.Count} things in 0.4 s");

        void UpdateDoctorSummary()
        {
            int bad = _checks.Count(c => c.Status == CheckStatus.Bad);
            int warn = _checks.Count(c => c.Status == CheckStatus.Warn);
            int ok = _checks.Count(c => c.Status == CheckStatus.Ok);
            var worst = bad > 0 ? CheckStatus.Bad : warn > 0 ? CheckStatus.Warn : CheckStatus.Ok;

            DoctorSummaryTitle.Text = worst == CheckStatus.Ok ? $"All {ok} checks passed"
                : $"{bad} problem{(bad == 1 ? "" : "s")}, {warn} warning{(warn == 1 ? "" : "s")}, {ok} OK";
            DoctorSummaryIcon.Text = Glyph(worst);
            DoctorSummaryIcon.Foreground = StatusBrush(worst);
            DoctorFixAllBtn.IsEnabled = worst != CheckStatus.Ok;

            int attention = bad + warn;
            DashHealthIcon.Text = Glyph(worst);
            DashHealthIcon.Foreground = StatusBrush(worst);
            DashHealthTitle.Text = attention == 0 ? "Everything healthy" : $"{attention} thing{(attention == 1 ? "" : "s")} need attention";
            DashHealthDetail.Text = attention == 0 ? "No known problems. Doctor keeps checking in the background."
                : "Doctor found problems that could break your server. Each one has a 1-click fix.";
        }

        // ------------------------------------------------------------------ backups
        void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            _backups.Insert(0, new BackupItem(DateTime.Now.ToString("'Today' HH:mm"), "Manual", "college, shop", "143 KB"));
            DashBackupTitle.Text = "Last backup just now";
            ShowToast("Backup saved (college, shop)");
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is BackupItem b)
                ShowToast($"Would restore '{b.When}'. A backup of the current state is taken first.");
        }

        // ------------------------------------------------------------------ helpers
        void ShowToast(string text, bool ok = true)
        {
            ToastText.Text = text;
            ToastIcon.Text = ok ? "\uE73E" : "\uE7BA";
            ToastIcon.Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        internal static string Glyph(CheckStatus s) => s == CheckStatus.Ok ? "\uE73E" : s == CheckStatus.Warn ? "\uE7BA" : "\uEA39";
        internal static Brush StatusBrush(CheckStatus s) => B(s == CheckStatus.Ok ? "Ok" : s == CheckStatus.Warn ? "Warn" : "Bad");
        internal static Brush SoftBrush(CheckStatus s) => B(s == CheckStatus.Ok ? "OkSoft" : s == CheckStatus.Warn ? "WarnSoft" : "BadSoft");

        static int CountOf(string s, char c) => s.Count(ch => ch == c);
        static string Short(string s) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > 60 ? s.Substring(0, 57) + "..." : s; }
        static string StripComments(string s) => Regex.Replace(Regex.Replace(s ?? "", @"--[^\n]*", ""), @"/\*[\s\S]*?\*/", "");

        static List<string> SplitStatements(string text)
        {
            var list = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inStr = false;
            foreach (var ch in StripComments(text))
            {
                if (ch == '\'') inStr = !inStr;
                if (ch == ';' && !inStr) { if (cur.ToString().Trim().Length > 0) list.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(ch);
            }
            if (cur.ToString().Trim().Length > 0) list.Add(cur.ToString().Trim());
            return list;
        }

        static string Closest(string word, string[] candidates)
        {
            string best = null; int bestD = int.MaxValue;
            foreach (var c in candidates)
            {
                int d = Lev(word.ToLowerInvariant(), c.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = c; }
            }
            return bestD <= Math.Max(2, word.Length / 3) ? best : null;
        }

        static int Lev(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }

    static class DataTableExt
    {
        public static DataTable CopyToDataTableOrEmpty(this DataRow[] rows, DataTable schema)
            => rows.Length > 0 ? rows.CopyToDataTable() : schema.Clone();
    }

    public enum CheckStatus { Ok, Warn, Bad }

    sealed class Guard
    {
        public CheckStatus Status; public string Title, Detail, FixLabel, FixSql;
        public Guard(CheckStatus s, string t, string d, string fixLabel = null, string fixSql = null)
        { Status = s; Title = t; Detail = d; FixLabel = fixLabel; FixSql = fixSql; }
    }

    public sealed class CheckItem : INotifyPropertyChanged
    {
        readonly string _fixedDetail;
        public CheckItem(string title, string detail, CheckStatus status, string fixLabel = null, string fixedDetail = null)
        { Title = title; Detail = detail; Status = status; FixLabel = fixLabel; _fixedDetail = fixedDetail; }

        public string Title { get; }
        public string Detail { get; private set; }
        public CheckStatus Status { get; private set; }
        public string FixLabel { get; }
        public string StatusText => Status == CheckStatus.Ok ? "OK" : Status == CheckStatus.Warn ? "WARNING" : "PROBLEM";
        public string Glyph => MainWindow.Glyph(Status);
        public Brush Brush => MainWindow.StatusBrush(Status);
        public Brush SoftBrush => MainWindow.SoftBrush(Status);
        public Visibility FixVisibility => Status != CheckStatus.Ok && FixLabel != null ? Visibility.Visible : Visibility.Collapsed;

        public void Fix()
        {
            Status = CheckStatus.Ok;
            if (_fixedDetail != null) Detail = _fixedDetail;
            foreach (var p in new[] { nameof(Detail), nameof(Status), nameof(StatusText), nameof(Glyph), nameof(Brush), nameof(SoftBrush), nameof(FixVisibility) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class LogEntry
    {
        public int No { get; set; }
        public string Time { get; set; }
        public string Action { get; set; }
        public string Message { get; set; }
        public string Duration { get; set; }
        public string Glyph { get; set; }
        public Brush Brush { get; set; }
    }

    public sealed class BackupItem
    {
        public BackupItem(string when, string reason, string dbs, string size) { When = when; Reason = reason; Dbs = dbs; Size = size; }
        public string When { get; }
        public string Reason { get; }
        public string Dbs { get; }
        public string Size { get; }
    }
}
