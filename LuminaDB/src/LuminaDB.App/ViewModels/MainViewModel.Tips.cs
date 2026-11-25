using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace LuminaDB.App.ViewModels
{
    /// <summary>The tip line at the bottom of each page (Tips.cs), and Settings › When I close the window.</summary>
    public sealed partial class MainViewModel
    {
        private string _tipText = "";
        private bool _tipPlaceChanged;

        public ICommand NextTipCommand { get; private set; }
        public ICommand HideTipsCommand { get; private set; }

        private void InitTips()
        {
            NextTipCommand = new RelayCommand(() => ShowTip(SelectedSection?.Key, advance: true));
            HideTipsCommand = new RelayCommand(() =>
            {
                ShowTips = false;
                Toast("Tips are off. Settings › Quick tips brings them back.");
            });
            ShowTip(SelectedSection?.Key, advance: false);
        }

        public string TipText { get => _tipText; private set { if (Set(ref _tipText, value)) OnPropertyChanged(nameof(HasTip)); } }
        public bool HasTip => ShowTips && !string.IsNullOrEmpty(_tipText);

        /// <summary>Settings › Quick tips (on by default).</summary>
        public bool ShowTips
        {
            get => !(_settings?.HideTips ?? false);
            set
            {
                if (_settings == null || ShowTips == value) return;
                _settings.HideTips = !value;
                SaveSettings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTip));
            }
        }

        /// <summary>Settings › When I close the window: false (the default) keeps LuminaDB running in the tray.</summary>
        public bool QuitOnClose
        {
            get => _settings?.QuitOnClose ?? false;
            set
            {
                if (_settings == null || QuitOnClose == value) return;
                _settings.QuitOnClose = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        /// <summary>A page was opened: its next tip (each visit moves on one, so the tips come round in turn).</summary>
        private void ShowTip(string page, bool advance)
        {
            if (page == null || !Tips.ByPage.TryGetValue(page, out var list) || list.Length == 0) { TipText = ""; return; }
            if (_settings != null && _settings.TipPlace == null) _settings.TipPlace = new Dictionary<string, int>();
            int at = -1;
            if (_settings != null && _settings.TipPlace.TryGetValue(page, out var saved)) at = saved;
            at = advance || at < 0 ? (at + 1) % list.Length : Math.Min(at, list.Length - 1);
            if (_settings != null && (!_settings.TipPlace.TryGetValue(page, out var old) || old != at))
            {
                _settings.TipPlace[page] = at;
                _tipPlaceChanged = true;
            }
            TipText = list[at];
        }

        private void OnTipPageShown(string page) => ShowTip(page, advance: true);

        /// <summary>Where the tips have got to is kept with the settings when LuminaDB closes.</summary>
        private void SaveTipPlace()
        {
            if (!_tipPlaceChanged || _settings == null) return;
            try { _settings.Save(_boot.Paths); _tipPlaceChanged = false; }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppLog.Error("saving tip place", ex); }
        }
    }
}
