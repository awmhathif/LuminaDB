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
            WindowStartupLocation = WindowStartupLocation.Manual;   // placed in OnSourceInitialized: where it was left, or to fit this screen
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
                // LUMINA_OFFSCREEN_SIZE=980x600: check the layout at another window size
                var size = (Environment.GetEnvironmentVariable("LUMINA_OFFSCREEN_SIZE") ?? "").Split('x');
                if (size.Length == 2 && double.TryParse(size[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var sw) && double.TryParse(size[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var sh))
                {
                    MinWidth = Math.Min(MinWidth, sw); MinHeight = Math.Min(MinHeight, sh);
                    Width = sw; Height = sh;
                }
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
                            double k = el.LayoutTransform?.Value.M11 ?? 1.0;   // text size: the page is drawn scaled
                            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                Math.Max(1, (int)(el.ActualWidth * k)), Math.Max(1, (int)(el.ActualHeight * k)), 96, 96, PixelFormats.Pbgra32);
                            bmp.Render(el);
                            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                            System.IO.File.WriteAllText(snap + ".clip.txt", LayoutAudit.Report(el));
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
            Activated += (s, e) => TellShown();
            Deactivated += (s, e) => TellShown();
            IsVisibleChanged += (s, e) => TellShown();
            Loaded += async (s, e) =>
            {
                ApplyChrome(animate: false);
                ApplyTextSize();
                await StartOnceAsync();
            };
        }

        private bool _started;

        /// <summary>Starts the server when the app opens (SPEC Settings default): on the first show, or at once when LuminaDB starts in the tray.</summary>
        public async System.Threading.Tasks.Task StartOnceAsync()
        {
            if (_started || !(DataContext is MainViewModel vm)) return;
            _started = true;
            await vm.StartAsync();
            vm.OpenStartFile();
        }

        /// <summary>Back from the tray (or from the taskbar): visible, restored the way it was, and in front.</summary>
        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (WindowState == WindowState.Minimized && hwnd != IntPtr.Zero) ShowWindow(hwnd, 9 /* SW_RESTORE: maximised again if it was */);
            if (Offscreen) return;
            Activate();
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private MainViewModel Vm => (MainViewModel)DataContext;

        // ------------------------------------------------------------------ focus mode: slim header, tab rail

        private bool? _folded, _rail;
        /// <summary>A short window (a laptop at 125%, or one made small): the masthead folds into the title bar on every page.</summary>
        private bool _short;
        private const double ShortWindow = 660;

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsCompactChrome)) ApplyChrome(animate: true);
            else if (e.PropertyName == nameof(MainViewModel.TextSize)) ApplyTextSize();
        }

        /// <summary>Settings → Text size: the whole page is drawn larger (layout scales with it, so nothing is clipped).</summary>
        /// <summary>
        /// Settings › Dark theme: the new colours spread like ink from the switch. A picture of the window in the old colours
        /// covers it, the colours change underneath, and a soft-edged hole in the picture grows from the switch until the
        /// picture is gone. Nothing is reloaded, so typing, results and running commands are untouched. With software
        /// drawing (no graphics card) the colours just change.
        /// </summary>
        public void SwitchTheme(bool night, FrameworkElement from)
        {
            if (night == Palette.Night) return;
            EndInk();
            double w = Shell.ActualWidth, h = Shell.ActualHeight;
            // The ink plays even with Windows animations off: it is one short change the person asked for, not motion
            // that comes by itself. Only software drawing (no graphics card) skips it, as it would stutter.
            bool canDraw = System.Windows.Media.RenderCapability.Tier > 0;
            if (!canDraw || !IsVisible || WindowState == WindowState.Minimized || w < 1 || h < 1)
            {
                AppLog.Info($"theme switch without ink (graphics card {(canDraw ? "yes" : "no")}, visible {IsVisible})");
                Palette.Apply(night);
                return;
            }

            var origin = new Point(w / 2, h / 2);
            if (from != null && from.IsVisible)
            {
                try { origin = from.TranslatePoint(new Point(Math.Min(16, from.ActualWidth / 2), from.ActualHeight / 2), Shell); }
                catch (InvalidOperationException) { }
            }

            System.Windows.Media.Imaging.RenderTargetBitmap picture;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(Shell);
                picture = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                picture.Render(Shell);
                picture.Freeze();
            }
            catch (Exception ex) when (ex is OutOfMemoryException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            {
                Palette.Apply(night);
                return;
            }

            // transparent inside the ink, a narrow soft edge, the old picture outside it
            var hole = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = origin, GradientOrigin = origin, RadiusX = 1, RadiusY = 1 };
            hole.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            hole.GradientStops.Add(new GradientStop(Colors.Transparent, 0.9));
            hole.GradientStops.Add(new GradientStop(Colors.Black, 1));
            InkCover.Source = picture;
            InkCover.OpacityMask = hole;
            InkCover.Visibility = Visibility.Visible;

            Palette.Apply(night);

            double reach = new[] { new Point(0, 0), new Point(w, 0), new Point(0, h), new Point(w, h) }.Max(c => (c - origin).Length) / 0.9 + 2;
            var grow = new System.Windows.Media.Animation.DoubleAnimation(1, reach, TimeSpan.FromMilliseconds(620))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            grow.Completed += (s, e) => { if (InkCover.OpacityMask == hole) EndInk(); };
            hole.BeginAnimation(RadialGradientBrush.RadiusXProperty, grow);
            hole.BeginAnimation(RadialGradientBrush.RadiusYProperty, grow);
        }

        /// <summary>Takes the old-colours picture away (the ink has spread, or another switch came first).</summary>
        private void EndInk()
        {
            if (InkCover.OpacityMask is RadialGradientBrush old)
            {
                old.BeginAnimation(RadialGradientBrush.RadiusXProperty, null);
                old.BeginAnimation(RadialGradientBrush.RadiusYProperty, null);
            }
            InkCover.Visibility = Visibility.Collapsed;
            InkCover.OpacityMask = null;
            InkCover.Source = null;
        }

        private void ApplyTextSize()
        {
            double s = (DataContext as MainViewModel)?.TextSize ?? 1.0;
            if (s < 1.0 || s > 1.5) s = 1.0;
            Root.LayoutTransform = s == 1.0 ? Transform.Identity : new ScaleTransform(s, s);
        }

        /// <summary>
        /// Folds the masthead and the activity line away (and back) and fades the slim status in: in Notebook focus, and in
        /// any short window. Notebook focus also narrows the tabs to a rail.
        /// </summary>
        private void ApplyChrome(bool animate)
        {
            bool compact = (DataContext as MainViewModel)?.IsCompactChrome ?? false;
            bool fold = compact || _short;
            int ms = animate ? Motion.Duration : 0;

            if (_rail != compact)
            {
                _rail = compact;
                double tabsFrom = TabsCol.ActualWidth > 0 ? TabsCol.ActualWidth : TabsCol.Width.Value;
                Motion.Run(TabsCol, tabsFrom, compact ? 34 : 114, v => TabsCol.Width = new GridLength(v), null, ms);
            }
            if (_folded == fold) return;
            _folded = fold;
            compact = fold;

            foreach (var part in new FrameworkElement[] { Masthead, Heartbeat, TipLine })
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
            StatusText.FontSize = w < 1150 ? 21 : 25;
            MastNoteText.MaxWidth = w < 1250 ? 240 : 320;
            MastNoteText.Visibility = w < 1080 ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            if (!info.HeightChanged) return;
            // A little hysteresis, so a window dragged across the line doesn't flicker between the two.
            bool s = _short ? ActualHeight < ShortWindow + 20 : ActualHeight < ShortWindow;
            if (s == _short) return;
            _short = s;
            if (IsLoaded) ApplyChrome(animate: true);
        }

        // ------------------------------------------------------------------ title bar

        // ------------------------------------------------------------------ where the window opens

        private static bool Offscreen => Environment.GetEnvironmentVariable("LUMINA_OFFSCREEN") == "1";
        private bool _wasMaximized, _maximizeOnRestore;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (Offscreen) return;
            PlaceWindow();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        protected override void OnClosed(EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            base.OnClosed(e);
        }

        /// <summary>
        /// Opens the way it was left (size, place, maximised), kept fully on a screen that still exists. The first time,
        /// a laptop-sized screen gets it maximised and a big one gets the design size, centred; never taller than the screen.
        /// </summary>
        private void PlaceWindow()
        {
            var paths = (DataContext as MainViewModel)?.Paths;
            LuminaDB.Core.Admin.WindowPlace saved = null;
            try { if (paths != null) saved = LuminaDB.Core.Admin.WindowPlace.Load(paths); }
            catch (Exception ex) { AppLog.Error("window place", ex); }

            var work = saved == null ? PrimaryWorkArea() : WorkAreaFor(saved.Left, saved.Top, saved.Width, saved.Height);
            var place = saved == null ? LuminaDB.Core.Admin.WindowPlace.FirstRun(work) : saved.FitTo(work);
            FitMinimum(work);
            Left = place.Left; Top = place.Top; Width = place.Width; Height = place.Height;
            if (place.Maximized)
            {
                // Started minimised by Windows: maximise when first opened from the taskbar.
                if (WindowState == WindowState.Minimized) _maximizeOnRestore = true;
                else WindowState = WindowState.Maximized;
            }
        }

        /// <summary>A screen smaller than the minimum (1366×768 at 150%) lowers the minimum, so the window always fits.</summary>
        private void FitMinimum(LuminaDB.Core.Admin.WindowPlace.Area work)
        {
            MinWidth = Math.Min(LuminaDB.Core.Admin.WindowPlace.MinWidth, work.Width);
            MinHeight = Math.Min(LuminaDB.Core.Admin.WindowPlace.MinHeight, work.Height);
        }

        /// <summary>Resolution changed or a screen was unplugged: pull a normal window back fully onto a screen.</summary>
        private void OnDisplayChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WindowState != WindowState.Normal) return;
            var work = WorkAreaFor(Left, Top, ActualWidth, ActualHeight);
            FitMinimum(work);
            var fit = new LuminaDB.Core.Admin.WindowPlace { Left = Left, Top = Top, Width = ActualWidth, Height = ActualHeight }.FitTo(work);
            Left = fit.Left; Top = fit.Top; Width = fit.Width; Height = fit.Height;
        }));

        private void SavePlace()
        {
            if (Offscreen) return;
            var paths = (DataContext as MainViewModel)?.Paths;
            if (paths == null) return;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (b.IsEmpty || b.Width < 200 || b.Height < 150) return;
            try
            {
                new LuminaDB.Core.Admin.WindowPlace
                {
                    Left = Math.Round(b.Left), Top = Math.Round(b.Top), Width = Math.Round(b.Width), Height = Math.Round(b.Height),
                    Maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && (_wasMaximized || _maximizeOnRestore)),
                }.Save(paths);
            }
            catch (Exception ex) { AppLog.Error("window place", ex); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;

        /// <summary>The work area (screen minus taskbar), in WPF units, of the screen holding most of this rectangle; the main screen if none does.</summary>
        private LuminaDB.Core.Admin.WindowPlace.Area WorkAreaFor(double left, double top, double width, double height)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var r = new RECT
            {
                Left = (int)(left * dpi.DpiScaleX), Top = (int)(top * dpi.DpiScaleY),
                Right = (int)((left + width) * dpi.DpiScaleX), Bottom = (int)((top + height) * dpi.DpiScaleY),
            };
            var mon = MonitorFromRect(ref r, MONITOR_DEFAULTTONULL);
            if (mon == IntPtr.Zero) return PrimaryWorkArea();
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref info)) return PrimaryWorkArea();
            return new LuminaDB.Core.Admin.WindowPlace.Area(info.rcWork.Left / dpi.DpiScaleX, info.rcWork.Top / dpi.DpiScaleY,
                (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX, (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY);
        }

        private static LuminaDB.Core.Admin.WindowPlace.Area PrimaryWorkArea()
        {
            var w = SystemParameters.WorkArea;
            return new LuminaDB.Core.Admin.WindowPlace.Area(w.Left, w.Top, w.Width, w.Height);
        }

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
        private void TellShown() => (DataContext as MainViewModel)?.SetWindowShown(WindowState == WindowState.Minimized || !IsVisible, IsActive);

        private void OnStateChanged(object sender, EventArgs e)
        {
            TellShown();
            if (WindowState == WindowState.Maximized) _wasMaximized = true;
            else if (WindowState == WindowState.Normal)
            {
                _wasMaximized = false;
                if (_maximizeOnRestore) { _maximizeOnRestore = false; WindowState = WindowState.Maximized; return; }
            }
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

        /// <summary>
        /// Closing the window keeps LuminaDB running in the tray (Settings › When I close the window), like XAMPP. Quitting
        /// (that setting, the tray's Quit, an installer, Windows shutting down) shuts the server down cleanly first, then closes.
        /// </summary>
        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (_readyToClose) return;
            SavePlace();
            var app = Application.Current as App;
            if (app != null && !app.Quitting && app.CanHide && !Vm.AppSettings.QuitOnClose)
            {
                e.Cancel = true;
                Hide();
                app.OnHiddenToTray();
                return;
            }
            app?.MarkQuitting("window closed");
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
