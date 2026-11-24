using System;
using System.ComponentModel;
using System.Windows;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using LuminaDB.App.ViewModels;

namespace LuminaDB.App
{
    public partial class MainWindow : Window
    {
        private bool _readyToClose;

        /// <summary>Space above the first binder tab (matches Margin in XAML).</summary>
        public const double TabsTop = 4;
        private const double TabPadding = 8 + 2 + 2;   // Border padding top+bottom, gap under each tab, rounding slack
        private const double MaxTabFont = 12.5, MinTabFont = 8;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) =>
            {
                // Start the server when the app opens (SPEC Settings default).
                if (DataContext is MainViewModel vm) await vm.StartAsync();
            };
        }

        private MainViewModel Vm => (MainViewModel)DataContext;

        /// <summary>
        /// All twelve tabs must fit the window height (a 1366×768 lab screen included) and look alike, so every
        /// tab shares one font size: the largest that fits the longest label.
        /// </summary>
        private void OnBookSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var sections = (DataContext as MainViewModel)?.Sections;
            if (sections == null || sections.Count == 0) return;
            // Labels are horizontal now, so the tab height is one line: only a really short window needs a smaller font.
            double perTab = (e.NewSize.Height - TabsTop) / sections.Count - TabPadding - 4;
            double size = Math.Max(MinTabFont, Math.Min(MaxTabFont, Math.Floor(perTab / 1.35 * 2) / 2));
            Resources["TabFontSize"] = size;

            // Narrow windows (like the prototype's media query): smaller status sentence, shorter note, then no note.
            double w = e.NewSize.Width;
            StatusText.FontSize = w < 1150 ? 24 : 30;
            MastNoteText.MaxWidth = w < 1250 ? 190 : 250;
            MastNoteText.Visibility = w < 1080 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F10 arrives as a "system" key in WPF.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Vm.HasSlip)
            {
                if (key == Key.Escape && !(Vm.Slip?.IsBusy ?? false)) { Vm.CloseSlip(); e.Handled = true; }
                return;
            }
            if (key >= Key.F1 && key <= Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
            {
                Vm.SelectByKey(key);
                e.Handled = true;
            }
            else if (key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var key2 = Vm.SelectedSection.Key;
                var cmd = key2 == "notebook" ? Vm.Notebook.RefreshSchemaCommand : key2 == "tables" ? Vm.Tables.RefreshCommand : Vm.RefreshCommand;
                if (cmd.CanExecute(null)) cmd.Execute(null);
                e.Handled = true;
            }
        }

        /// <summary>Closing always shuts the server down cleanly first, then closes.</summary>
        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (_readyToClose) return;
            e.Cancel = true;
            IsEnabled = false;
            try { await Vm.ShutdownAsync(); }
            catch (Exception ex) { AppLog.Error("shutdown", ex); }
            _readyToClose = true;
            AppLog.Info("closed");
            // When the server was already stopped, ShutdownAsync finishes at once and we are still inside Closing,
            // where Close() throws. Close on the next dispatcher turn instead.
            await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
