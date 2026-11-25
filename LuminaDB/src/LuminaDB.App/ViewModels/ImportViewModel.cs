using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Import;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>A line of "what LuminaDB changed or skipped" in a .sql file.</summary>
    public sealed class RewriteLine
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public int Count { get; set; }
        public int FirstLine { get; set; }
        public string Example { get; set; }
        public string CountText => Count == 1 ? "once, line " + FirstLine.ToString(CultureInfo.InvariantCulture) : Count.ToString("N0", CultureInfo.InvariantCulture) + " times, first on line " + FirstLine.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class ProblemLine
    {
        public int Line { get; set; }
        public string Headline { get; set; }
        public string Text { get; set; }
        public string Server { get; set; }
        public string LineText => Line > 0 ? "line " + Line.ToString("N0", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>A column of a CSV/Excel file, editable before the import.</summary>
    public sealed class ColumnLine : ObservableObject
    {
        private bool _include = true;
        private string _name;
        private string _type;

        public ColumnLine(ImportColumn c)
        {
            Source = c;
            _include = c.Include;
            _name = c.Name;
            _type = c.Type;
        }

        public ImportColumn Source { get; }
        public string Header => Source.Header;
        public string Examples => string.Join(" · ", Source.Examples);
        public bool Include { get => _include; set { if (Set(ref _include, value)) Source.Include = value; } }
        public string Name { get => _name; set { if (Set(ref _name, value)) Source.Name = (value ?? "").Trim(); } }
        public string Type { get => _type; set { if (Set(ref _type, value)) Source.Type = (value ?? "").Trim(); } }
    }

    public sealed class ExportFileLine
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Size => File.Exists(Path) ? DatabaseSummary.FormatSize(new FileInfo(Path).Length) : "";
    }

    /// <summary>
    /// Import (F6, SPEC §5.6): .sql files from XAMPP, Workbench and phpMyAdmin with MySQL 8 parts fixed on the way in,
    /// CSV and Excel into tables, and export to .sql, CSV, JSON and Excel. Whatever an import will change is backed
    /// up first (SPEC §5.10).
    /// </summary>
    public sealed class ImportViewModel : ObservableObject
    {
        public static readonly string[] Types =
        {
            "INT", "BIGINT", "DECIMAL(10,2)", "DOUBLE", "DATE", "DATETIME", "TIME", "VARCHAR(50)", "VARCHAR(100)", "VARCHAR(255)", "VARCHAR(1000)", "TEXT", "TINYINT(1)",
        };
        public const string AllDatabases = "(all databases)";
        public const string WholeDatabase = "(whole database)";

        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        private string _page = "sql";
        private bool _busy;
        private CancellationTokenSource _cts;
        private double _progress;
        private string _progressText = "";
        private string _error = "";

        public ImportViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            Pages.Add(new PageChip("sql", ".sql file"));
            Pages.Add(new PageChip("table", "CSV or Excel → table"));
            Pages.Add(new PageChip("export", "Export"));
            Pages[0].IsSelected = true;
            SelectPageCommand = new RelayCommand(p => Page = (p as PageChip)?.Key ?? "sql");

            ChooseSqlCommand = new RelayCommand(ChooseSql, () => !_busy);
            ImportSqlCommand = new AsyncCommand(ImportSqlAsync, () => CanRun && SqlPreview != null);
            ClearSqlCommand = new RelayCommand(() => { SqlPreview = null; SqlResult = null; }, () => !_busy);
            OpenImportedCommand = new AsyncCommand(p => _main.OpenDatabaseOnAsync(p as string, ImportedDatabase), _ => HasImportedDatabase && _main.IsRunning && !_busy);

            ChooseTableFileCommand = new RelayCommand(ChooseTableFile, () => !_busy);
            ImportTableCommand = new AsyncCommand(ImportTableAsync, () => CanRun && TablePreview != null && !string.IsNullOrWhiteSpace(TableName) && !string.IsNullOrWhiteSpace(TableDatabase));
            ClearTableCommand = new RelayCommand(() => { TablePreview = null; TableResult = null; }, () => !_busy);

            ExportCommand = new AsyncCommand(ExportAsync, () => CanRun && !string.IsNullOrEmpty(ExportDatabase));
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy && _cts != null);
            ShowFileCommand = new RelayCommand(p => ShowFile((p as ExportFileLine)?.Path ?? p as string));
            OpenExportsCommand = new RelayCommand(() => OpenFolder(_main.Paths.Exports));
        }

        public ObservableCollection<PageChip> Pages { get; } = new ObservableCollection<PageChip>();
        public ICommand SelectPageCommand { get; }
        public ICommand ChooseSqlCommand { get; }
        public ICommand ImportSqlCommand { get; }
        public ICommand ClearSqlCommand { get; }
        public ICommand ChooseTableFileCommand { get; }
        public ICommand ImportTableCommand { get; }
        public ICommand ClearTableCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ShowFileCommand { get; }
        public ICommand OpenExportsCommand { get; }

        public string Page
        {
            get => _page;
            set
            {
                if (!Set(ref _page, value)) return;
                foreach (var c in Pages) c.IsSelected = c.Key == _page;
                OnPropertyChanged(nameof(IsSqlPage));
                OnPropertyChanged(nameof(IsTablePage));
                OnPropertyChanged(nameof(IsExportPage));
                if (_page == "export") _ = LoadExportTablesAsync();
            }
        }
        public bool IsSqlPage => _page == "sql";
        public bool IsTablePage => _page == "table";
        public bool IsExportPage => _page == "export";

        private bool CanRun => !_busy && _main.IsRunning && !_main.NeedsPassword;
        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        public bool IsIndeterminate => _progress < 0;
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value ?? ""); }
        public string Error { get => _error; private set { if (Set(ref _error, value ?? "")) OnPropertyChanged(nameof(HasError)); } }
        public bool HasError => _error.Length > 0;

        public string ServerNote => _main.IsRunning ? "" : "Start the database server on Today (F1) to import or export.";
        public bool ServerStopped => !_main.IsRunning;
        public string EngineNote => "Imports into MariaDB " + (_main.Server?.ServerVersion?.Replace("-MariaDB", "") ?? EngineSet.FolderName(_main.Kind))
                                    + (_main.Kind == EngineSetKind.Legacy ? " (the XAMPP-compatible set): MySQL 8 and MariaDB 11 collations are changed to ones it has." : ".");

        public ObservableCollection<string> DatabaseNames { get; } = new ObservableCollection<string>();

        /// <summary>When the tab is shown: the list of databases, and the export page's tables.</summary>
        public void OnShown()
        {
            ReloadDatabases();
            OnPropertyChanged(nameof(ServerNote));
            OnPropertyChanged(nameof(ServerStopped));
            OnPropertyChanged(nameof(EngineNote));
            if (IsExportPage) _ = LoadExportTablesAsync();
        }

        private void ReloadDatabases()
        {
            var names = _main.Databases.Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            DatabaseNames.Sync(names);
            var export = new List<string>();
            if (names.Count > 1) export.Add(AllDatabases);
            export.AddRange(names);
            ExportDatabases.Sync(export);
            if (string.IsNullOrEmpty(_exportDb) || !ExportDatabases.Contains(_exportDb)) ExportDatabase = names.FirstOrDefault();
        }

        public static bool CanOpen(string file)
        {
            var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
            return SqlImporter.Extensions.Contains(ext) || TableImporter.Extensions.Contains(ext);
        }

        /// <summary>
        /// From Projects: an imported folder's .sql dump, ready to check and load. A dump that doesn't name its database
        /// goes into the one the project's PHP connects to.
        /// </summary>
        public void OpenSqlFor(string file, string database)
        {
            if (_busy || string.IsNullOrEmpty(file)) return;
            Page = "sql";
            SqlTarget = "";
            LoadSql(file);
            if (SqlPreview != null && SqlPreview.NeedsDatabase && !string.IsNullOrEmpty(database)) SqlTarget = database;
        }

        /// <summary>A file dropped on the page goes to the right panel.</summary>
        public void OpenDropped(string file)
        {
            if (_busy || string.IsNullOrEmpty(file)) return;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (SqlImporter.Extensions.Contains(ext)) { Page = "sql"; LoadSql(file); }
            else if (TableImporter.Extensions.Contains(ext)) { Page = "table"; LoadTableFile(file); }
            else _main.Toast("LuminaDB imports .sql, .gz and .zip dumps, and .csv, .tsv and .xlsx files.");
        }

        // ====================================================================== .sql files

        private ImportPreview _sqlPreview;
        private ImportResult _sqlResult;
        private string _sqlTarget = "";

        public ObservableCollection<RewriteLine> SqlRewrites { get; } = new ObservableCollection<RewriteLine>();
        public ObservableCollection<RewriteLine> SqlSkipped { get; } = new ObservableCollection<RewriteLine>();
        public ObservableCollection<ProblemLine> SqlProblems { get; } = new ObservableCollection<ProblemLine>();

        public ImportPreview SqlPreview
        {
            get => _sqlPreview;
            private set
            {
                _sqlPreview = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSqlPreview));
                OnPropertyChanged(nameof(SqlFileName));
                OnPropertyChanged(nameof(SqlFileInfo));
                OnPropertyChanged(nameof(SqlWhatText));
                OnPropertyChanged(nameof(SqlDropsText));
                OnPropertyChanged(nameof(HasSqlDrops));
                OnPropertyChanged(nameof(SqlNeedsDatabase));
                OnPropertyChanged(nameof(SqlFirstLines));
                SqlRewrites.Clear();
                if (value != null) foreach (var r in value.Rewrites) SqlRewrites.Add(Line(r));
                OnPropertyChanged(nameof(SqlRewritesHeading));
                OnPropertyChanged(nameof(HasSqlRewrites));
                if (value != null && value.NeedsDatabase && string.IsNullOrWhiteSpace(_sqlTarget))
                    SqlTarget = TableImporter.TableName(Path.GetFileNameWithoutExtension(value.File));
            }
        }

        public bool HasSqlPreview => _sqlPreview != null;
        public string SqlFileName => _sqlPreview == null ? "" : Path.GetFileName(_sqlPreview.File);
        public string SqlFileInfo => _sqlPreview == null ? "" : DatabaseSummary.FormatSize(_sqlPreview.Bytes) + " · " + _sqlPreview.SourceText
            + (_sqlPreview.SawWholeFile ? " · " + _sqlPreview.StatementsSeen.ToString("N0", CultureInfo.InvariantCulture) + " commands" : "");
        public string SqlWhatText
        {
            get
            {
                if (_sqlPreview == null) return "";
                if (_sqlPreview.NeedsDatabase) return "The file doesn't say which database it belongs in. Choose one below: it is made if it isn't there.";
                var dbs = _sqlPreview.Databases;
                var existing = dbs.Where(d => _main.Databases.Any(x => string.Equals(x.Name, d, StringComparison.OrdinalIgnoreCase))).ToList();
                string text = (dbs.Count == 1 ? "Goes into the database " : "Goes into the databases ") + string.Join(", ", dbs) + ".";
                if (existing.Count > 0) text += " " + string.Join(", ", existing) + (existing.Count == 1 ? " is" : " are") + " already there and will be backed up first.";
                return text + (_sqlPreview.SawWholeFile ? "" : " (Read from the start of the file; it may name more.)");
            }
        }
        public bool HasSqlDrops => _sqlPreview != null && _sqlPreview.Drops.Count > 0;
        public string SqlDropsText => !HasSqlDrops ? "" : "It deletes before it creates: " + string.Join(", ", _sqlPreview.Drops.Take(8)) + (_sqlPreview.Drops.Count > 8 ? " and " + (_sqlPreview.Drops.Count - 8) + " more" : "") + ". What's there now is backed up first.";
        public bool SqlNeedsDatabase => _sqlPreview != null && _sqlPreview.NeedsDatabase;
        public string SqlFirstLines => _sqlPreview?.FirstLines ?? "";
        public bool HasSqlRewrites => SqlRewrites.Count > 0;
        public string SqlRewritesHeading => SqlRewrites.Count == 0 ? "" : "LUMINADB WILL CHANGE " + (SqlRewrites.Count == 1 ? "ONE THING" : MainViewModel.NumberWord(SqlRewrites.Count).ToUpperInvariant() + " THINGS") + " SO THIS SERVER RUNS IT";

        public string SqlTarget { get => _sqlTarget; set => Set(ref _sqlTarget, (value ?? "").Trim()); }

        public ImportResult SqlResult
        {
            get => _sqlResult;
            private set
            {
                _sqlResult = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSqlResult));
                OnPropertyChanged(nameof(HasImportedDatabase));
                OnPropertyChanged(nameof(SqlResultHeading));
                OnPropertyChanged(nameof(SqlResultNote));
                OnPropertyChanged(nameof(SqlResultTone));
                SqlSkipped.Clear();
                SqlProblems.Clear();
                if (value == null) return;
                foreach (var s in value.Skipped) SqlSkipped.Add(Line(s));
                foreach (var p in value.Problems) SqlProblems.Add(new ProblemLine { Line = p.Line, Headline = p.Headline, Text = p.Text, Server = "#" + p.Code + " " + p.Message });
                SqlRewrites.Clear();
                foreach (var r in value.Rewrites) SqlRewrites.Add(Line(r));
                OnPropertyChanged(nameof(HasSqlRewrites));
                OnPropertyChanged(nameof(SqlRewritesHeading));
            }
        }
        public bool HasSqlResult => _sqlResult != null;

        private string _importedDb;
        /// <summary>The database the last .sql went into (named in the file, or chosen here).</summary>
        public string ImportedDatabase { get => _importedDb; private set { if (Set(ref _importedDb, value)) OnPropertyChanged(nameof(HasImportedDatabase)); } }
        public bool HasImportedDatabase => _importedDb != null && _sqlResult != null;
        public ICommand OpenImportedCommand { get; private set; }
        public string SqlResultTone => _sqlResult == null ? "info" : _sqlResult.Clean ? "ok" : _sqlResult.Complete ? "warn" : "bad";
        public string SqlResultHeading
        {
            get
            {
                var r = _sqlResult;
                if (r == null) return "";
                if (r.Cancelled) return "Stopped. " + Commands(r.Succeeded) + " ran before that.";
                if (r.StoppedBecause != null) return "Stopped at line " + r.StoppedAtLine.ToString("N0", CultureInfo.InvariantCulture) + ".";
                if (r.Problems.Count == 0) return "Imported: " + Commands(r.Statements) + ", no errors.";
                return "Imported with " + (r.Problems.Count == 1 ? "one error" : MainViewModel.NumberWord(r.Problems.Count) + " errors") + ": " + Commands(r.Succeeded) + " of " + r.Statements.ToString("N0", CultureInfo.InvariantCulture) + " ran.";
            }
        }
        public string SqlResultNote
        {
            get
            {
                var r = _sqlResult;
                if (r == null) return "";
                var parts = new List<string>();
                if (r.StoppedBecause != null) parts.Add(r.StoppedBecause);
                if (r.Databases.Count > 0) parts.Add("Databases: " + string.Join(", ", r.Databases) + ".");
                if (r.RowsChanged > 0) parts.Add(r.RowsChanged.ToString("N0", CultureInfo.InvariantCulture) + " rows added or changed.");
                parts.Add("Took " + Took(r.Duration) + ".");
                if (r.Problems.Count > 0 || r.Cancelled || r.StoppedBecause != null) parts.Add("The backup taken first is in Backups (F10) if you want to go back.");
                return string.Join(" ", parts);
            }
        }

        private void ChooseSql()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a .sql file",
                Filter = "SQL dumps (*.sql, *.sql.gz, *.zip)|*.sql;*.gz;*.zip|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == true) LoadSql(dlg.FileName);
        }

        private void LoadSql(string file)
        {
            Error = "";
            SqlResult = null;
            SqlTarget = "";
            try { SqlPreview = SqlImporter.Preview(file); }
            catch (Exception ex) when (ex is LuminaException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                SqlPreview = null;
                Error = "Couldn't read " + Path.GetFileName(file) + ": " + ex.Message;
            }
        }

        private async Task ImportSqlAsync()
        {
            var preview = _sqlPreview;
            if (preview == null) return;
            if (preview.NeedsDatabase && !ValidName(SqlTarget)) { Error = "Type a name for the database (letters, digits and _)."; return; }
            Error = "";
            var target = preview.NeedsDatabase ? SqlTarget : null;
            var touched = (target != null ? new[] { target } : preview.Databases.ToArray())
                .Where(d => _main.Databases.Any(x => string.Equals(x.Name, d, StringComparison.OrdinalIgnoreCase))).ToList();
            await RunAsync("Importing " + Path.GetFileName(preview.File), async ct =>
            {
                if (touched.Count > 0 && !await _main.BackUpBeforeAsync(touched, "Before importing " + Path.GetFileName(preview.File)))
                {
                    _main.Receipt("import · " + Path.GetFileName(preview.File), "not run: no backup", "info");
                    return;
                }
                var slip = _main.Receipt("import · " + Path.GetFileName(preview.File), "running…", "run");
                var progress = new Progress<ImportProgress>(p =>
                {
                    Progress = p.Fraction;
                    ProgressText = p.Statements.ToString("N0", CultureInfo.InvariantCulture) + " commands · line " + p.Line.ToString("N0", CultureInfo.InvariantCulture)
                                   + (p.Errors > 0 ? " · " + p.Errors + (p.Errors == 1 ? " error" : " errors") : "") + " · " + p.Current;
                });
                var result = await SqlImporter.ImportAsync(_main.Server, new ImportOptions { File = preview.File, Database = target, Target = ImportRewriter.TargetFor(_main.Kind) }, progress, ct);
                ImportedDatabase = target ?? result.Databases.FirstOrDefault(d => !Core.Sql.SchemaSnapshot.IsSystem(d));
                SqlResult = result;
                slip.Kind = result.Clean ? "ok" : result.Complete ? "warn" : "bad";
                slip.Message = SqlResultHeading + (result.Rewrites.Count > 0 ? " · " + result.Rewrites.Sum(n => n.Count) + " MySQL 8 parts fixed" : "");
                if (result.Clean) _main.Toast("Imported " + Path.GetFileName(preview.File) + ".");
            });
            _main.DataChanged();
        }

        // ====================================================================== CSV and Excel into a table

        private TableFilePreview _tablePreview;
        private TableImportResult _tableResult;
        private string _tableDb = "";
        private string _tableName = "";
        private bool _toExisting;
        private bool _hasHeader = true;
        private string _delimiter = ",";
        private string _sheet;
        private bool _monthFirst;
        private DataView _previewRows;
        private bool _reloading;

        public ObservableCollection<ColumnLine> Columns { get; } = new ObservableCollection<ColumnLine>();
        public ObservableCollection<ProblemLine> TableProblems { get; } = new ObservableCollection<ProblemLine>();
        public ObservableCollection<string> Sheets { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> ExistingTables { get; } = new ObservableCollection<string>();
        public IReadOnlyList<string> TypeChoices => Types;
        public IReadOnlyList<string> Delimiters { get; } = new[] { "comma ( , )", "semicolon ( ; )", "tab", "bar ( | )" };

        public TableFilePreview TablePreview
        {
            get => _tablePreview;
            private set
            {
                _tablePreview = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTablePreview));
                OnPropertyChanged(nameof(TableFileName));
                OnPropertyChanged(nameof(TableFileInfo));
                OnPropertyChanged(nameof(IsCsv));
                OnPropertyChanged(nameof(HasSheets));
                Columns.Clear();
                if (value == null) { PreviewRows = null; return; }
                foreach (var c in value.Columns) Columns.Add(new ColumnLine(c));
                PreviewRows = BuildRows(value);
                OnPropertyChanged(nameof(NewTableNote));
            }
        }
        public bool HasTablePreview => _tablePreview != null;
        public string TableFileName => _tablePreview == null ? "" : Path.GetFileName(_tablePreview.File);
        public string TableFileInfo
        {
            get
            {
                var p = _tablePreview;
                if (p == null) return "";
                return (p.IsExcel ? "Excel" : p.EncodingName + " text") + " · " + Columns.Count + " columns · "
                       + (p.SawWholeFile ? p.SampledRows.ToString("N0", CultureInfo.InvariantCulture) + " rows" : "first " + p.SampledRows.ToString("N0", CultureInfo.InvariantCulture) + " rows looked at")
                       + " · dates read " + (p.DateOrder == DateOrder.DayFirst ? "day first (25/12/2024)" : "month first (12/25/2024)");
            }
        }
        public bool IsCsv => _tablePreview != null && !_tablePreview.IsExcel;
        public bool HasSheets => _tablePreview != null && _tablePreview.IsExcel && Sheets.Count > 1;
        public DataView PreviewRows { get => _previewRows; private set => Set(ref _previewRows, value); }

        public string TableDatabase
        {
            get => _tableDb;
            set { if (Set(ref _tableDb, (value ?? "").Trim())) _ = LoadExistingTablesAsync(); }
        }
        public string TableName { get => _tableName; set { if (Set(ref _tableName, (value ?? "").Trim())) OnPropertyChanged(nameof(NewTableNote)); } }
        public bool ToExistingTable
        {
            get => _toExisting;
            set
            {
                if (!Set(ref _toExisting, value)) return;
                OnPropertyChanged(nameof(ToNewTable));
                OnPropertyChanged(nameof(NewTableNote));
                if (value && !ExistingTables.Contains(_tableName, StringComparer.OrdinalIgnoreCase)) TableName = ExistingTables.FirstOrDefault() ?? "";
                if (!value && _tablePreview != null) TableName = _tablePreview.SuggestedTable;
            }
        }
        public bool ToNewTable { get => !_toExisting; set => ToExistingTable = !value; }
        public string NewTableNote
        {
            get
            {
                if (_tablePreview == null) return "";
                if (_toExisting) return "Columns are matched by name. Ones the table doesn't have are left out, and the table is backed up first.";
                bool hasId = Columns.Any(c => c.Include && string.Equals(c.Name, "id", StringComparison.OrdinalIgnoreCase));
                return hasId ? "A new table with the columns ticked below. Its id column becomes the primary key when every id is filled in and different."
                             : "A new table with the columns ticked below, plus an id column (1, 2, 3…) as its primary key.";
            }
        }

        public bool HasHeader { get => _hasHeader; set { if (Set(ref _hasHeader, value)) Reload(); } }
        public bool MonthFirst { get => _monthFirst; set { if (Set(ref _monthFirst, value)) { if (_tablePreview != null) _tablePreview.DateOrder = value ? DateOrder.MonthFirst : DateOrder.DayFirst; OnPropertyChanged(nameof(TableFileInfo)); } } }
        public string Delimiter
        {
            get => _delimiter == "," ? Delimiters[0] : _delimiter == ";" ? Delimiters[1] : _delimiter == "\t" ? Delimiters[2] : Delimiters[3];
            set
            {
                var d = value == Delimiters[1] ? ";" : value == Delimiters[2] ? "\t" : value == Delimiters[3] ? "|" : ",";
                if (d == _delimiter) return;
                _delimiter = d;
                OnPropertyChanged();
                Reload();
            }
        }
        public string Sheet { get => _sheet; set { if (Set(ref _sheet, value)) Reload(); } }

        public TableImportResult TableResult
        {
            get => _tableResult;
            private set
            {
                _tableResult = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTableResult));
                OnPropertyChanged(nameof(TableResultHeading));
                OnPropertyChanged(nameof(TableResultNote));
                OnPropertyChanged(nameof(TableResultTone));
                TableProblems.Clear();
                if (value != null)
                    foreach (var p in value.Problems) TableProblems.Add(new ProblemLine { Line = p.Line, Headline = p.Headline, Text = p.Text, Server = "#" + p.Code + " " + p.Message });
            }
        }
        public bool HasTableResult => _tableResult != null;
        public string TableResultTone => _tableResult == null ? "info" : _tableResult.Clean ? "ok" : _tableResult.Inserted > 0 ? "warn" : "bad";
        public string TableResultHeading
        {
            get
            {
                var r = _tableResult;
                if (r == null) return "";
                string rows = Rows(r.Inserted);
                if (r.Cancelled) return "Stopped. " + rows + " went in before that.";
                if (r.Failed == 0) return rows + " added to " + r.Database + "." + r.Table + ".";
                return rows + " added to " + r.Database + "." + r.Table + "; " + Rows(r.Failed).ToLowerInvariant() + " couldn't go in.";
            }
        }
        public string TableResultNote
        {
            get
            {
                var r = _tableResult;
                if (r == null) return "";
                var parts = new List<string>();
                if (r.Created) parts.Add("Made the table" + (r.AddedId ? " with an id column." : "."));
                if (r.Skipped.Count > 0) parts.Add("Left out (no column of that name): " + string.Join(", ", r.Skipped) + ".");
                if (r.StoppedBecause != null) parts.Add(r.StoppedBecause);
                if (r.Problems.Count < r.Failed) parts.Add("The first " + r.Problems.Count + " problems are listed.");
                parts.Add("Took " + Took(r.Duration) + ". Open it in Tables (F4).");
                return string.Join(" ", parts);
            }
        }

        private void ChooseTableFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a CSV or Excel file",
                Filter = "Spreadsheets (*.csv, *.tsv, *.txt, *.xlsx)|*.csv;*.tsv;*.txt;*.xlsx|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == true) LoadTableFile(dlg.FileName);
        }

        private void LoadTableFile(string file)
        {
            Error = "";
            TableResult = null;
            _reloading = true;
            try
            {
                var p = TableImporter.Preview(file);
                Sheets.Clear();
                foreach (var s in p.Sheets) Sheets.Add(s);
                _sheet = p.Sheet; OnPropertyChanged(nameof(Sheet));
                _delimiter = p.Delimiter.ToString(); OnPropertyChanged(nameof(Delimiter));
                _hasHeader = p.HasHeader; OnPropertyChanged(nameof(HasHeader));
                _monthFirst = p.DateOrder == DateOrder.MonthFirst; OnPropertyChanged(nameof(MonthFirst));
                TablePreview = p;
                if (string.IsNullOrWhiteSpace(_tableDb)) TableDatabase = DatabaseNames.FirstOrDefault() ?? "imported";
                if (!_toExisting) TableName = p.SuggestedTable;
            }
            catch (Exception ex) when (ex is LuminaException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                TablePreview = null;
                Error = "Couldn't read " + Path.GetFileName(file) + ": " + ex.Message;
            }
            finally { _reloading = false; }
        }

        /// <summary>The delimiter, header or sheet changed: read the file again with it.</summary>
        private void Reload()
        {
            if (_reloading || _tablePreview == null) return;
            try
            {
                var p = TableImporter.Preview(_tablePreview.File, _sheet, _delimiter[0], _hasHeader);
                p.DateOrder = _monthFirst ? DateOrder.MonthFirst : DateOrder.DayFirst;
                TablePreview = p;
                Error = "";
            }
            catch (Exception ex) when (ex is LuminaException || ex is IOException || ex is InvalidDataException) { Error = ex.Message; }
        }

        private static DataView BuildRows(TableFilePreview p)
        {
            var t = new DataTable();
            foreach (var c in p.Columns)
            {
                var name = c.Header;
                while (t.Columns.Contains(name)) name += " ";
                t.Columns.Add(name, typeof(string));
            }
            foreach (var row in p.Rows.Skip(p.HasHeader ? 1 : 0).Take(12))
                t.Rows.Add(p.Columns.Select(c => (object)(c.Index < row.Length ? row[c.Index] : "")).ToArray());
            return t.DefaultView;
        }

        private async Task LoadExistingTablesAsync()
        {
            var db = _tableDb;
            ExistingTables.Clear();
            if (!_main.IsRunning || string.IsNullOrEmpty(db)) return;
            try
            {
                foreach (var t in await TableNamesAsync(db)) ExistingTables.Add(t);
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("listing tables for import failed", ex); }
            if (_toExisting && !ExistingTables.Contains(_tableName, StringComparer.OrdinalIgnoreCase)) TableName = ExistingTables.FirstOrDefault() ?? "";
        }

        private async Task ImportTableAsync()
        {
            var p = _tablePreview;
            if (p == null) return;
            if (!ValidName(TableDatabase)) { Error = "Type a name for the database (letters, digits and _)."; return; }
            if (!_toExisting && !ValidName(TableName)) { Error = "Type a name for the new table (letters, digits and _)."; return; }
            var bad = Columns.Where(c => c.Include && !ValidName(c.Name)).Select(c => c.Header).ToList();
            if (!_toExisting && bad.Count > 0) { Error = "Give these columns a name of letters, digits and _: " + string.Join(", ", bad) + "."; return; }
            var dup = Columns.Where(c => c.Include).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0) { Error = "Two columns can't have the same name: " + string.Join(", ", dup) + "."; return; }
            Error = "";
            var options = new TableImportOptions
            {
                File = p.File, Sheet = p.Sheet, Delimiter = _delimiter[0], HasHeader = _hasHeader,
                DateOrder = _monthFirst ? DateOrder.MonthFirst : DateOrder.DayFirst,
                Database = TableDatabase, Table = TableName, CreateTable = !_toExisting,
                Columns = Columns.Select(c => c.Source).ToList(),
            };
            string title = "import · " + Path.GetFileName(p.File) + " → " + options.Database + "." + options.Table;
            await RunAsync("Importing " + Path.GetFileName(p.File), async ct =>
            {
                if (_toExisting && !await _main.BackUpBeforeAsync(new[] { options.Database }, "Before adding " + Path.GetFileName(p.File) + " to " + options.Table))
                {
                    _main.Receipt(title, "not run: no backup", "info");
                    return;
                }
                var slip = _main.Receipt(title, "running…", "run");
                var progress = new Progress<ImportProgress>(x =>
                {
                    Progress = x.Fraction;
                    ProgressText = x.Done.ToString("N0", CultureInfo.InvariantCulture) + " rows in" + (x.Errors > 0 ? " · " + x.Errors + " couldn't go in" : "");
                });
                var result = await TableImporter.ImportAsync(_main.Server, options, progress, ct);
                TableResult = result;
                slip.Kind = result.Clean ? "ok" : result.Inserted > 0 ? "warn" : "bad";
                slip.Message = TableResultHeading;
                if (result.Clean) _main.Toast(TableResultHeading);
            });
            _main.DataChanged();
            _ = LoadExistingTablesAsync();
        }

        // ====================================================================== export

        private string _exportDb;
        private string _exportTable = WholeDatabase;
        private ExportFormat _format = ExportFormat.Sql;
        private bool _compatible = true;
        private string _exportNote = "";

        public ObservableCollection<string> ExportDatabases { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> ExportTables { get; } = new ObservableCollection<string>();
        public ObservableCollection<ExportFileLine> ExportedFiles { get; } = new ObservableCollection<ExportFileLine>();

        public string ExportDatabase
        {
            get => _exportDb;
            set { if (Set(ref _exportDb, value)) { OnPropertyChanged(nameof(CanPickTable)); _ = LoadExportTablesAsync(); } }
        }
        public string ExportTable { get => _exportTable; set => Set(ref _exportTable, value ?? WholeDatabase); }
        public bool CanPickTable => _exportDb != null && _exportDb != AllDatabases;

        public bool FormatSql { get => _format == ExportFormat.Sql; set { if (value) SetFormat(ExportFormat.Sql); } }
        public bool FormatCsv { get => _format == ExportFormat.Csv; set { if (value) SetFormat(ExportFormat.Csv); } }
        public bool FormatJson { get => _format == ExportFormat.Json; set { if (value) SetFormat(ExportFormat.Json); } }
        public bool FormatExcel { get => _format == ExportFormat.Excel; set { if (value) SetFormat(ExportFormat.Excel); } }
        public bool Compatible { get => _compatible; set => Set(ref _compatible, value); }
        public IReadOnlyList<string> Formats { get; } = new[] { ".sql file", "CSV", "JSON", "Excel (.xlsx)" };
        public string FormatName
        {
            get => Formats[(int)_format];
            set { int i = Array.IndexOf((string[])Formats, value); if (i >= 0) SetFormat((ExportFormat)i); }
        }
        public string FormatNote
        {
            get
            {
                switch (_format)
                {
                    case ExportFormat.Sql: return "Tables, data, views, procedures and triggers, as one .sql file. Open it with LuminaDB, XAMPP's phpMyAdmin or Workbench.";
                    case ExportFormat.Csv: return "One .csv per table (UTF-8, so Excel shows Sinhala and Tamil). Several tables go in a folder.";
                    case ExportFormat.Json: return "One .json per table: a list of rows, numbers as numbers.";
                    default: return "One .xlsx workbook with a sheet for each table and the header row in bold.";
                }
            }
        }
        public string ExportNote { get => _exportNote; private set { if (Set(ref _exportNote, value ?? "")) OnPropertyChanged(nameof(HasExported)); } }
        public bool HasExported => ExportedFiles.Count > 0 || _exportNote.Length > 0;
        public string ExportsFolder => _main.Paths.Exports;

        private void SetFormat(ExportFormat f)
        {
            _format = f;
            OnPropertyChanged(nameof(FormatSql));
            OnPropertyChanged(nameof(FormatCsv));
            OnPropertyChanged(nameof(FormatJson));
            OnPropertyChanged(nameof(FormatExcel));
            OnPropertyChanged(nameof(FormatNote));
            OnPropertyChanged(nameof(FormatName));
        }

        private async Task LoadExportTablesAsync()
        {
            var db = _exportDb;
            var keep = _exportTable;
            ExportTables.Clear();
            ExportTables.Add(WholeDatabase);
            if (_main.IsRunning && CanPickTable)
            {
                try { foreach (var t in await TableNamesAsync(db)) ExportTables.Add(t); }
                catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("listing tables for export failed", ex); }
            }
            _exportTable = ExportTables.Contains(keep) ? keep : WholeDatabase;
            OnPropertyChanged(nameof(ExportTable));   // the list was rebuilt under the combo: show the choice again
        }

        private async Task ExportAsync()
        {
            var req = new ExportRequest { Format = _format, Flavour = _compatible ? SqlFlavour.Compatible : SqlFlavour.MariaDb, Folder = _main.Paths.Exports };
            if (_exportDb == AllDatabases) req.Databases.AddRange(_main.Databases.Select(d => d.Name));
            else req.Databases.Add(_exportDb);
            if (CanPickTable && !string.IsNullOrEmpty(_exportTable) && _exportTable != WholeDatabase) req.Tables.Add(_exportTable);
            string what = req.Tables.Count == 1 ? req.Databases[0] + "." + req.Tables[0] : req.Databases.Count == 1 ? req.Databases[0] : req.Databases.Count + " databases";
            Error = "";
            ExportedFiles.Clear();
            ExportNote = "";
            await RunAsync("Exporting " + what, async ct =>
            {
                Progress = -1;
                var progress = new Progress<string>(t => ProgressText = t);
                var result = await DataExporter.ExportAsync(_main.Server, req, progress, ct);
                foreach (var f in result.Files) ExportedFiles.Add(new ExportFileLine { Path = f });
                ExportNote = (req.Format == ExportFormat.Sql ? "" : Rows(result.Rows) + " written. ") + string.Join(" ", result.Notes);
                _main.Receipt("export · " + what, string.Join(", ", result.Files.Select(Path.GetFileName)) + " · " + DatabaseSummary.FormatSize(result.Bytes), "ok");
            });
        }

        // ====================================================================== shared

        private async Task RunAsync(string what, Func<CancellationToken, Task> work)
        {
            if (_busy) return;
            IsBusy = true;
            Progress = 0;
            ProgressText = what + "…";
            OnPropertyChanged(nameof(IsIndeterminate));
            _cts = new CancellationTokenSource();
            try { await work(_cts.Token); }
            catch (OperationCanceledException) { Error = what + ": stopped."; }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                AppLog.Error(what + " failed", ex);
                Error = ex.Message;
                _main.Receipt(what.ToLowerInvariant(), ex.Message, "bad");
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                IsBusy = false;
                ProgressText = "";
                Progress = 0;
                OnPropertyChanged(nameof(IsIndeterminate));
            }
        }

        private async Task<List<string>> TableNamesAsync(string db)
        {
            var list = new List<string>();
            using (var c = await _main.Server.OpenConnectionAsync())
            using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @d AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) list.Add(r.GetString(0));
            }
            return list;
        }

        /// <summary>Names LuminaDB makes: letters (any script), digits and _, up to 64.</summary>
        public static bool ValidName(string s) =>
            !string.IsNullOrWhiteSpace(s) && s.Length <= 64 && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' || ch > 127 && !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch));

        private static RewriteLine Line(RewriteNote n) => new RewriteLine { Id = n.Id, Text = n.Text, Count = n.Count, FirstLine = n.FirstLine, Example = n.Example };
        private static string Commands(int n) => n == 1 ? "one command" : n.ToString("N0", CultureInfo.InvariantCulture) + " commands";
        private static string Rows(long n) => n == 1 ? "One row" : n.ToString("N0", CultureInfo.InvariantCulture) + " rows";
        private static string Took(TimeSpan t) => t.TotalSeconds < 1 ? "under a second" : t.TotalMinutes < 1 ? t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s" : ((int)t.TotalMinutes) + " min " + t.Seconds + " s";

        private void ShowFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            try { Process.Start("explorer.exe", "/select,\"" + file + "\"")?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }

        private void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\"")?.Dispose(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }
    }
}
