using System;
using System.ComponentModel;
using System.Windows;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
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
        private const double TabGap = 3;                       // Margin under each tab
        private const double MaxTabHeight = 32, MinTabHeight = 22;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) =>
            {
                // Start the server when the app opens (SPEC Settings default).
                if (DataContext is MainViewModel vm)
                {
                    await vm.StartAsync();
                    vm.OpenStartFile();
                }
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
            // One fixed height for every tab (so nothing moves on click), as tall as the window allows; the font follows.
            double perTab = Math.Floor((e.NewSize.Height - TabsTop) / sections.Count - TabGap);
            double height = Math.Max(MinTabHeight, Math.Min(MaxTabHeight, perTab));
            Resources["TabHeight"] = height;
            Resources["TabFontSize"] = Math.Min(12.5, Math.Max(9, Math.Floor(height * 0.42 * 2) / 2));

            // Narrow windows (like the prototype's media query): smaller status sentence, shorter note, then no note.
            double w = e.NewSize.Width;
            StatusText.FontSize = w < 1150 ? 24 : 30;
            MastNoteText.MaxWidth = w < 1250 ? 190 : 250;
            MastNoteText.Visibility = w < 1080 ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ title bar

        private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void OnMaximize(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        private const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;

        /// <summary>
        /// A maximised window without the Windows frame hangs over the screen edge by the frame's width; pad it back in
        /// (in device-independent pixels, so it is right at 125% and 150% scaling too). The max glyph becomes "restore".
        /// </summary>
        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                double x = (GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) / dpi.DpiScaleX;
                double y = (GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) / dpi.DpiScaleY;
                Root.Margin = new Thickness(x, y, x, y);
                MaxGlyph.Data = Geometry.Parse("M0.5,2.5 H7.5 V9.5 H0.5 Z M2.5,2.5 V0.5 H9.5 V7.5 H7.5");
            }
            else
            {
                Root.Margin = new Thickness(0);
                MaxGlyph.Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
            }
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
