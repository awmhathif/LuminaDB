using System;
using System.Windows.Input;

namespace LuminaDB.App.ViewModels
{
    /// <summary>
    /// Focus mode for the Notebook: the editor and results get the window, the side panels and the window's
    /// header step aside, and the toolbar keeps only what writing SQL needs. Layout choices are remembered.
    /// </summary>
    public sealed partial class NotebookViewModel
    {
        private UiState _uiState;
        private ICommand _toggleFocus, _setLayout, _toggleIndex, _toggleReceipt;

        private UiState Ui => _uiState ?? (_uiState = UiState.Load(_paths));

        public bool IsFocus
        {
            get => Ui.NotebookFocus;
            set
            {
                if (Ui.NotebookFocus == value) return;
                Ui.NotebookFocus = value;
                Ui.Save(_paths);
                OnPropertyChanged();
                OnPropertyChanged(nameof(NotFocus));
                OnPropertyChanged(nameof(ShowIndex));
                OnPropertyChanged(nameof(ShowReceipt));
                OnPropertyChanged(nameof(FocusTip));
            }
        }
        public bool NotFocus => !IsFocus;
        public string FocusTip => IsFocus ? "Leave focus (Esc or Ctrl+Shift+M)" : "Focus: give the editor the whole window (Ctrl+Shift+M)";

        /// <summary>"split", "editor" or "results".</summary>
        public string Layout
        {
            get => Ui.NotebookLayout;
            set
            {
                var v = value == "editor" || value == "results" ? value : "split";
                if (Ui.NotebookLayout == v) return;
                Ui.NotebookLayout = v;
                Ui.Save(_paths);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSplitLayout));
                OnPropertyChanged(nameof(IsEditorLayout));
                OnPropertyChanged(nameof(IsResultsLayout));
            }
        }
        public bool IsSplitLayout => Layout == "split";
        public bool IsEditorLayout => Layout == "editor";
        public bool IsResultsLayout => Layout == "results";

        /// <summary>The editor's share of the height in split view; set by dragging the divider.</summary>
        public double SplitRatio
        {
            get => Ui.NotebookSplit;
            set
            {
                var v = Math.Max(0.2, Math.Min(0.85, value));
                if (Math.Abs(Ui.NotebookSplit - v) < 0.005) return;
                Ui.NotebookSplit = v;
                Ui.Save(_paths);
                OnPropertyChanged();
            }
        }

        public bool IsIndexPinned
        {
            get => Ui.NotebookIndexPinned;
            set { if (Ui.NotebookIndexPinned == value) return; Ui.NotebookIndexPinned = value; Ui.Save(_paths); OnPropertyChanged(); OnPropertyChanged(nameof(ShowIndex)); }
        }
        public bool IsReceiptPinned
        {
            get => Ui.NotebookReceiptPinned;
            set { if (Ui.NotebookReceiptPinned == value) return; Ui.NotebookReceiptPinned = value; Ui.Save(_paths); OnPropertyChanged(); OnPropertyChanged(nameof(ShowReceipt)); }
        }

        /// <summary>Whether the table list and the receipt tape are wanted (the window may still be too narrow for the tape).</summary>
        public bool ShowIndex => !IsFocus || IsIndexPinned;
        public bool ShowReceipt => !IsFocus || IsReceiptPinned;

        public ICommand ToggleFocusCommand => _toggleFocus ?? (_toggleFocus = new RelayCommand(() => IsFocus = !IsFocus));
        public ICommand SetLayoutCommand => _setLayout ?? (_setLayout = new RelayCommand(p => Layout = p as string ?? "split"));
        public ICommand ToggleIndexCommand => _toggleIndex ?? (_toggleIndex = new RelayCommand(() => IsIndexPinned = !IsIndexPinned));
        public ICommand ToggleReceiptCommand => _toggleReceipt ?? (_toggleReceipt = new RelayCommand(() => IsReceiptPinned = !IsReceiptPinned));

        /// <summary>Editor only → split → results only → editor only (double-click on the divider).</summary>
        public void CycleLayout() => Layout = Layout == "editor" ? "split" : Layout == "split" ? "results" : "editor";
    }
}
