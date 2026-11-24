using System;
using System.ComponentModel;
using System.Windows;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using LuminaDB.App.Controls;
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
            // Dev/test only: LUMINA_OFFSCREEN=1 opens the window far off every screen and never activates it,
            // so automated checks can run while someone is using the PC.
            if (Environment.GetEnvironmentVariable("LUMINA_OFFSCREEN") == "1")
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                WindowState = WindowState.Normal;
                Left = -20000;
                Top = 0;
                Width = 1366;
                Height = 768;
                ShowActivated = false;
                ShowInTaskbar = false;
                // Off every monitor WPF stops drawing, so screen captures come out black. Instead the window paints
                // itself to a PNG on request: create <LUMINA_SNAP>.req and <LUMINA_SNAP> appears.
                var snap = Environment.GetEnvironmentVariable("LUMINA_SNAP");
                if (!string.IsNullOrEmpty(snap))
                {
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    timer.Tick += (s, e) =>
                    {
                        if (!System.IO.File.Exists(snap + ".req")) return;
                        try
                        {
                            System.IO.File.Delete(snap + ".req");
                            var el = (FrameworkElement)Content;
                            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                Math.Max(1, (int)el.ActualWidth), Math.Max(1, (int)el.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bmp.Render(el);
                            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                            using (var f = System.IO.File.Create(snap)) png.Save(f);
                        }
                        catch (Exception ex) { AppLog.Error("snap", ex); }
                    };
                    timer.Start();
                }
            }
            DataContextChanged += (s, e) =>
            {
                if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmChanged;
                if (e.NewValue is MainViewModel vm2) vm2.PropertyChanged += OnVmChanged;
            };
            Loaded += async (s, e) =>
            {
                ApplyChrome(animate: false);
                // Start the server when the app opens (SPEC Settings default).
                if (DataContext is MainViewModel vm)
                {
                    await vm.StartAsync();
                    vm.OpenStartFile();
                }
            };
        }

        private MainViewModel Vm => (MainViewModel)DataContext;

        // ------------------------------------------------------------------ focus mode: slim header, tab rail

        private bool? _compact;

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsCompactChrome)) ApplyChrome(animate: true);
        }

        /// <summary>Folds the masthead and the activity line away (and back), narrows the tabs to a rail, fades the slim status in.</summary>
        private void ApplyChrome(bool animate)
        {
            bool compact = (DataContext as MainViewModel)?.IsCompactChrome ?? false;
            if (_compact == compact) return;
            _compact = compact;
            int ms = animate ? Motion.Duration : 0;

            foreach (var part in new FrameworkElement[] { Masthead, Heartbeat })
            {
                var el = part;
                if (compact)
                {
                    double from = el.ActualHeight;
                    Motion.Run(el, from, 0, v => { el.MaxHeight = v; el.Opacity = from <= 0 ? 0 : v / from; }, () => el.Visibility = Visibility.Collapsed, ms);
                }
                else
                {
                    double start = el.Visibility == Visibility.Visible ? el.ActualHeight : 0;   // reversing mid-fold starts where it is
                    el.Visibility = Visibility.Visible;
                    el.MaxHeight = double.PositiveInfinity;
                    el.Measure(new Size(Math.Max(1, ((FrameworkElement)el.Parent).ActualWidth), double.PositiveInfinity));
                    double to = el.DesiredSize.Height;
                    el.MaxHeight = start;
                    Motion.Run(el, start, to, v => { el.MaxHeight = v; el.Opacity = to <= 0 ? 1 : v / to; }, () => { el.MaxHeight = double.PositiveInfinity; el.Opacity = 1; }, ms);
                }
            }

            double tabsFrom = TabsCol.ActualWidth > 0 ? TabsCol.ActualWidth : TabsCol.Width.Value;
            Motion.Run(TabsCol, tabsFrom, compact ? 34 : 104, v => TabsCol.Width = new GridLength(v), null, ms);

            if (compact) SlimStatus.Visibility = Visibility.Visible;
            Motion.Run(SlimStatus, SlimStatus.Opacity, compact ? 1 : 0, v => SlimStatus.Opacity = v,
                       () => { if (!compact) SlimStatus.Visibility = Visibility.Collapsed; }, ms);
        }

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
            else if (key == Key.M && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && Vm.SelectedSection?.Key == "notebook")
            {
                Vm.Notebook.IsFocus = !Vm.Notebook.IsFocus;
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
