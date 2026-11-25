using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using LuminaDB.Core;

namespace LuminaDB.App.ViewModels
{
    /// <summary>
    /// One SQL sheet (a tab above the editor). Its text is always kept in a draft file under config, so nothing is lost
    /// when the app closes. A sheet can also belong to a .sql file the person opened or saved; Save writes that file.
    /// </summary>
    public sealed class NotebookSheet : ObservableObject
    {
        private bool _selected, _dirty;
        private string _filePath;

        public NotebookSheet(string id, string draft, int number, string text)
        {
            Id = id;
            DraftFile = draft;
            Number = number;
            Document = new TextDocument(text ?? "");
            Document.UndoStack.ClearAll();
        }

        public string Id { get; }
        /// <summary>Where LuminaDB keeps this sheet's text between runs (in config).</summary>
        public string DraftFile { get; }
        public int Number { get; }
        public TextDocument Document { get; }
        public int Caret { get; set; }
        /// <summary>The draft on disk is behind the text.</summary>
        public bool DraftStale { get; set; }

        /// <summary>The .sql file this sheet was opened from or saved to; null for a LuminaDB-only sheet.</summary>
        public string FilePath
        {
            get => _filePath;
            set { if (Set(ref _filePath, value)) { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Tip)); OnPropertyChanged(nameof(IsFile)); } }
        }
        public bool IsFile => _filePath != null;

        /// <summary>Changed since it was last saved to its file (only for file sheets).</summary>
        public bool IsDirty { get => _dirty; set { if (Set(ref _dirty, value)) OnPropertyChanged(nameof(Tip)); } }

        public string Title => _filePath != null ? Path.GetFileName(_filePath) : "Sheet " + Number.ToString(CultureInfo.InvariantCulture);
        public string Tip => _filePath == null
            ? "Kept by LuminaDB between runs. Save as (Ctrl+S) keeps it as a .sql file of your own."
            : _filePath + (_dirty ? "\nChanged since it was saved: Ctrl+S saves it." : "");

        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        private bool _canClose;
        public bool CanClose { get => _canClose; set => Set(ref _canClose, value); }
    }

    [DataContract]
    public sealed class NotebookSheetState
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Draft { get; set; }
        [DataMember] public int Number { get; set; }
        [DataMember] public string File { get; set; }
        [DataMember] public int Caret { get; set; }
        [DataMember] public bool Dirty { get; set; }
    }

    [DataContract]
    public sealed class NotebookSheetsState
    {
        [DataMember] public List<NotebookSheetState> Sheets { get; set; } = new List<NotebookSheetState>();
        [DataMember] public string Selected { get; set; }
        [DataMember] public string LastFolder { get; set; }
    }

    public sealed partial class NotebookViewModel
    {
        /// <summary>Bigger files are dumps: Import runs them in a stream; the editor would crawl.</summary>
        private const long MaxOpenBytes = 4 * 1024 * 1024;

        private NotebookSheet _sheet;
        private string _sheetsFile;
        private string _draftsFolder;
        private string _lastFolder;

        public ObservableCollection<NotebookSheet> Sheets { get; } = new ObservableCollection<NotebookSheet>();
        public NotebookSheet CurrentSheet => _sheet;
        public TextDocument Document => _sheet.Document;

        public ICommand NewSheetCommand { get; private set; }
        public ICommand SelectSheetCommand { get; private set; }
        public ICommand CloseSheetCommand { get; private set; }
        public ICommand OpenFileCommand { get; private set; }
        public ICommand SaveCommand { get; private set; }
        public ICommand SaveAsCommand { get; private set; }

        private bool CanSwitchSheet => !_executing && !_main.HasSlip;

        private void SetUpSheets(AppPaths paths)
        {
            _sheetsFile = Path.Combine(paths.Config, "notebook-sheets.json");
            _draftsFolder = Path.Combine(paths.Config, "notebook-sheets");
            var state = JsonFile.Read<NotebookSheetsState>(_sheetsFile);
            _lastFolder = state?.LastFolder;
            if (state?.Sheets != null)
                foreach (var st in state.Sheets.Where(x => x != null && !string.IsNullOrEmpty(x.Id) && !string.IsNullOrEmpty(x.Draft)))
                {
                    // a file sheet with no unsaved changes shows the file as it is now (it may have been edited elsewhere);
                    // otherwise the draft, which holds the latest text
                    string text = null;
                    bool dirty = st.Dirty;
                    if (st.File != null && !st.Dirty) text = ReadText(st.File);
                    if (text == null) text = ReadText(Path.Combine(paths.Config, st.Draft));
                    if (text == null && st.File != null) { text = ReadText(st.File); dirty = false; }
                    var sheet = AddSheet(st.Id, st.Draft, st.Number, text ?? "", select: false);
                    sheet.FilePath = st.File;
                    sheet.IsDirty = st.File != null && dirty;
                    sheet.Caret = Math.Max(0, Math.Min(st.Caret, sheet.Document.TextLength));
                }
            if (Sheets.Count == 0)
            {
                // the first run, or an older LuminaDB: the one notebook.sql becomes Sheet 1
                var text = ReadText(Path.Combine(paths.Config, "notebook.sql"));
                AddSheet("1", "notebook.sql", 1, string.IsNullOrWhiteSpace(text) ? NotebookExamples.Welcome : text, select: false);
            }
            var selected = Sheets.FirstOrDefault(x => x.Id == state?.Selected) ?? Sheets[0];
            _sheet = selected;
            selected.IsSelected = true;
            _caret = selected.Caret;
            UpdateCanClose();

            NewSheetCommand = new RelayCommand(NewSheet, () => CanSwitchSheet);
            SelectSheetCommand = new RelayCommand(p => SelectSheet(p as NotebookSheet), _ => CanSwitchSheet);
            CloseSheetCommand = new AsyncCommand(p => CloseSheetAsync(p as NotebookSheet), _ => CanSwitchSheet);
            OpenFileCommand = new RelayCommand(OpenFile, () => CanSwitchSheet);
            SaveCommand = new RelayCommand(() => Save(_sheet, chooseFile: false), () => !_main.HasSlip);
            SaveAsCommand = new RelayCommand(() => Save(_sheet, chooseFile: true), () => !_main.HasSlip);
        }

        private NotebookSheet AddSheet(string id, string draft, int number, string text, bool select)
        {
            var sheet = new NotebookSheet(id, draft, number, text);
            sheet.Document.TextChanged += (s, e) =>
            {
                sheet.DraftStale = true;
                if (sheet.IsFile) sheet.IsDirty = true;
                _saveTimer?.Stop();
                _saveTimer?.Start();
                if (sheet == _sheet) { _analyseTimer?.Stop(); _analyseTimer?.Start(); }
            };
            Sheets.Add(sheet);
            UpdateCanClose();
            if (select) SelectSheet(sheet);
            return sheet;
        }

        private void UpdateCanClose()
        {
            foreach (var s in Sheets) s.CanClose = Sheets.Count > 1;
        }

        private void NewSheet()
        {
            int number = Sheets.Count == 0 ? 1 : Sheets.Max(x => x.Number) + 1;
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            AddSheet(id, Path.Combine("notebook-sheets", "sheet-" + id + ".sql"), number, "", select: true);
            SaveSheetsState();
        }

        public void SelectSheet(NotebookSheet sheet)
        {
            if (sheet == null || sheet == _sheet || !Sheets.Contains(sheet)) return;
            _sheet.Caret = _caret;
            _sheet.IsSelected = false;
            _sheet = sheet;
            sheet.IsSelected = true;
            _caret = Math.Min(sheet.Caret, sheet.Document.TextLength);
            _selStart = _selLength = 0;
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(CurrentSheet));
            Analyse();
            SaveSheetsState();
        }

        private async Task CloseSheetAsync(NotebookSheet sheet)
        {
            if (sheet == null || Sheets.Count < 2 || !Sheets.Contains(sheet)) return;
            if (sheet.IsFile && sheet.IsDirty)
            {
                var choice = await _main.AskAsync(new ChoiceSlip("CLOSE", "Save " + sheet.Title + " first?",
                    "It has changes that aren't in the file yet. Close without saving drops them.", false, "Save and close", "Close without saving", sheet.FilePath));
                if (choice == SlipChoice.Cancel) return;
                if (choice == SlipChoice.Primary && !Save(sheet, chooseFile: false)) return;
            }
            else if (!sheet.IsFile && sheet.Document.TextLength > 0 && sheet.Document.Text.Trim().Length > 0)
            {
                var choice = await _main.AskAsync(new ChoiceSlip("CLOSE", "Close " + sheet.Title + "?",
                    "Its SQL isn't saved as a file, so it goes with the tab. Save as keeps it as a .sql file first.", true, "Close it", "Save as a file first", Headline(sheet)));
                if (choice == SlipChoice.Cancel) return;
                if (choice == SlipChoice.Secondary && !Save(sheet, chooseFile: true)) return;
            }
            int index = Sheets.IndexOf(sheet);
            if (sheet == _sheet) SelectSheet(Sheets[index > 0 ? index - 1 : 1]);
            Sheets.Remove(sheet);
            UpdateCanClose();
            foreach (var r in Receipts.Where(r => r.Sheet == sheet)) r.Sheet = null;
            TryDelete(Path.Combine(_paths.Config, sheet.DraftFile));
            SaveSheetsState();
        }

        private static string Headline(NotebookSheet sheet)
        {
            var first = sheet.Document.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return first.Length > 100 ? first.Substring(0, 100) + "…" : first;
        }

        // ------------------------------------------------------------------ files

        private void OpenFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a .sql file in the Notebook",
                Filter = "SQL files (*.sql)|*.sql|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = Directory.Exists(_lastFolder ?? "") ? _lastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() != true) return;
            OpenFile(dlg.FileName);
        }

        /// <summary>Opens a .sql file in its own tab (or the tab it's already in).</summary>
        public void OpenFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var full = Path.GetFullPath(path);
            var open = Sheets.FirstOrDefault(x => x.FilePath != null && string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (open != null) { SelectSheet(open); _main.Toast(open.Title + " is already open."); return; }
            long size;
            try { size = new FileInfo(full).Length; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _main.Toast("Couldn't open " + Path.GetFileName(full) + ": " + ex.Message); return; }
            if (size > MaxOpenBytes)
            {
                _main.Toast(Path.GetFileName(full) + " is " + (size / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB: a dump that size runs best from Import (F6).");
                return;
            }
            var text = ReadText(full);
            if (text == null) { _main.Toast("Couldn't read " + Path.GetFileName(full) + "."); return; }
            _lastFolder = Path.GetDirectoryName(full);

            // an empty, unnamed tab is reused; otherwise the file gets a new tab
            NotebookSheet sheet;
            if (!_sheet.IsFile && _sheet.Document.Text.Trim().Length == 0) sheet = _sheet;
            else
            {
                int number = Sheets.Max(x => x.Number) + 1;
                string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                sheet = AddSheet(id, Path.Combine("notebook-sheets", "sheet-" + id + ".sql"), number, "", select: false);
            }
            sheet.Document.Text = text;
            sheet.Document.UndoStack.ClearAll();
            sheet.FilePath = full;
            sheet.IsDirty = false;
            sheet.Caret = 0;
            if (sheet == _sheet) { OnPropertyChanged(nameof(Document)); Analyse(); }
            else SelectSheet(sheet);
            SaveSheetsState();
            _main.Toast("Opened " + sheet.Title + ". Ctrl+S saves it back.");
        }

        /// <summary>Save (to its file, or ask where for a new one). Returns false when nothing was saved.</summary>
        private bool Save(NotebookSheet sheet, bool chooseFile)
        {
            if (sheet == null) return false;
            string path = sheet.FilePath;
            if (chooseFile || path == null)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save the SQL as a file",
                    Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
                    DefaultExt = ".sql",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = sheet.IsFile ? Path.GetFileName(sheet.FilePath) : "sheet-" + sheet.Number.ToString(CultureInfo.InvariantCulture) + ".sql",
                    InitialDirectory = sheet.IsFile ? Path.GetDirectoryName(sheet.FilePath)
                        : Directory.Exists(_lastFolder ?? "") ? _lastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                };
                if (dlg.ShowDialog() != true) return false;
                path = dlg.FileName;
            }
            if (!WriteFile(path, sheet.Document.Text)) return false;
            _lastFolder = Path.GetDirectoryName(path);
            // the same file open in another tab would now be stale: that tab lets go of the file
            foreach (var other in Sheets.Where(x => x != sheet && x.FilePath != null && string.Equals(Path.GetFullPath(x.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                other.FilePath = null;
            sheet.FilePath = Path.GetFullPath(path);
            sheet.IsDirty = false;
            SaveSheetsState();
            _main.Toast("Saved " + sheet.Title + ".");
            return true;
        }

        /// <summary>Writes UTF-8 (no BOM, as MariaDB and phpMyAdmin expect) through a temp file, so a failed save keeps the old file.</summary>
        private bool WriteFile(string path, string text)
        {
            var tmp = path + ".lumina-tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                _main.Toast("Couldn't save " + Path.GetFileName(path) + ": " + ex.Message);
                return false;
            }
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }   // UTF-8 unless the file has another BOM
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("reading " + path + " failed", ex); return null; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("deleting " + path + " failed", ex); }
        }

        // ------------------------------------------------------------------ drafts

        /// <summary>Writes the drafts of changed sheets and the list of tabs.</summary>
        public void SaveNow()
        {
            if (_sheet != null) _sheet.Caret = _caret;
            foreach (var sheet in Sheets.Where(s => s.DraftStale).ToList())
            {
                var file = Path.Combine(_paths.Config, sheet.DraftFile);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var tmp = file + ".tmp";
                    File.WriteAllText(tmp, sheet.Document.Text, new UTF8Encoding(false));
                    if (File.Exists(file)) File.Replace(tmp, file, null);
                    else File.Move(tmp, file);
                    sheet.DraftStale = false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("saving " + sheet.DraftFile + " failed", ex); }
            }
            SaveSheetsState();
        }

        private void SaveSheetsState()
        {
            if (_sheetsFile == null) return;
            var state = new NotebookSheetsState
            {
                Selected = _sheet?.Id,
                LastFolder = _lastFolder,
                Sheets = Sheets.Select(s => new NotebookSheetState
                {
                    Id = s.Id, Draft = s.DraftFile, Number = s.Number, File = s.FilePath, Dirty = s.IsDirty,
                    Caret = s == _sheet ? _caret : s.Caret,
                }).ToList(),
            };
            try { Directory.CreateDirectory(_paths.Config); JsonFile.Write(_sheetsFile, state); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("saving notebook tabs failed", ex); }
        }
    }
}
