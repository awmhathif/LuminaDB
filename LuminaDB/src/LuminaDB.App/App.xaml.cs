using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LuminaDB.App.ViewModels;
using LuminaDB.Core;
using LuminaDB.Core.Web;

namespace LuminaDB.App
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private MainViewModel _vm;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => AppLog.Error("fatal", a.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => { AppLog.Error("background task", a.Exception); a.SetObserved(); };

            // Dev/test overrides: LUMINA_ROOT (app folder), LUMINA_ENGINE_DIR (a MariaDB folder).
            var root = Environment.GetEnvironmentVariable("LUMINA_ROOT");
            var paths = string.IsNullOrWhiteSpace(root) ? AppPaths.FromExecutable() : new AppPaths(root);

            // "LuminaDB.exe --quit": the installer and uninstaller ask the copy in this folder to close cleanly (server
            // stopped, window place saved), and wait for it. Exit code 0 when nothing is running any more.
            if (e.Args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase))) { Shutdown(QuitRunningCopy(paths.Root)); return; }

            if (!AcquireSingleInstance(paths.Root)) { Shutdown(0); return; }
            ListenForQuit(paths.Root);

            try { paths.EnsureCreated(); }
            catch (LuminaException ex)
            {
                MessageBox.Show(ex.Message, "LuminaDB", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }
            AppLog.Init(paths.Logs);

            var windows = WindowsInfo.Current();
            EngineSetKind kind;
            try
            {
                // Each installer carries only the sets for its PCs, so use the best set that is installed.
                kind = EngineSetSelector.Parse(Environment.GetEnvironmentVariable("LUMINA_ENGINE_SET"))
                    ?? EngineSetSelector.ChooseInstalled(windows,
                        k => Directory.Exists(Path.Combine(paths.Engines, EngineSet.FolderName(k), "mariadb")),
                        k => Directory.Exists(paths.MariaDbData(k)));
            }
            catch (LuminaException ex)
            {
                MessageBox.Show(ex.Message, "LuminaDB", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(3);
                return;
            }

            // Settings › Database port (3306 unless the user chose another).
            var boot = new Bootstrap { Paths = paths, Windows = windows, Kind = kind, PreferredPort = LuminaDB.Core.Admin.AppSettings.Load(paths).DatabasePort };
            try
            {
                var dir = Environment.GetEnvironmentVariable("LUMINA_ENGINE_DIR");
                boot.Engine = string.IsNullOrWhiteSpace(dir)
                    ? EngineSet.FromAppRoot(paths, kind)
                    : EngineSet.FromMariaDbFolder(kind, dir);
            }
            catch (LuminaException ex) { boot.EngineError = ex; AppLog.Error("engine: " + ex.Message); }

            // LUMINA_WEB_DIR: a folder holding php85\, caddy\, apache\ and phpmyadmin\ (developer setups).
            var webDir = Environment.GetEnvironmentVariable("LUMINA_WEB_DIR");
            boot.Web = string.IsNullOrWhiteSpace(webDir) ? WebStack.FromAppRoot(paths, kind) : WebStack.FromFolder(webDir);

            AppLog.Info($"start: {windows.FriendlyName}, set {EngineSet.FolderName(kind)}, root {paths.Root}, engine {(boot.Engine?.MariaDbDir ?? "missing")}, web {(boot.Web.CanServe ? boot.Web.PhpDir : "missing")}");

            _vm = new MainViewModel(boot, Dispatcher);
            // "Open with LuminaDB" on a .sql, .csv or .xlsx file: it opens on the Import page (F6)
            _vm.OpenOnStart = e.Args.FirstOrDefault(a => System.IO.File.Exists(a) && LuminaDB.App.ViewModels.ImportViewModel.CanOpen(a));
            var window = new MainWindow { DataContext = _vm };
            // Started by Windows (Settings › Start with Windows): open on the taskbar, not over what the person is doing.
            if (e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) && Environment.GetEnvironmentVariable("LUMINA_OFFSCREEN") != "1")
                window.WindowState = WindowState.Minimized;
            MainWindow = window;
            window.Show();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            // Windows is logging off or shutting down: stop the database cleanly before we're killed.
            AppLog.Info("Windows session ending: stopping the server");
            _vm?.EmergencyStop();
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _quitWait?.Unregister(null);
            _quitSignal?.Dispose();
            try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance?.Dispose();
            base.OnExit(e);
        }

        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("unhandled UI exception", e.Exception);
            e.Handled = true;
            MessageBox.Show("Something unexpected happened, but your data is safe.\n\n" + e.Exception.Message +
                            "\n\nDetails were written to " + (AppLog.LogFile ?? "logs\\app.log") + ".",
                "LuminaDB", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ------------------------------------------------------------------ one window per LuminaDB folder

        private static string FolderId(string root)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant())).Take(8).Select(b => b.ToString("x2")));
        }

        private EventWaitHandle _quitSignal;
        private RegisteredWaitHandle _quitWait;

        /// <summary>The running copy closes the normal way (server stopped cleanly) when an installer signals it.</summary>
        private void ListenForQuit(string root)
        {
            try
            {
                _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LuminaDB-quit-" + FolderId(root));
                _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (s, timedOut) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    AppLog.Info("asked to close by the installer");
                    if (MainWindow != null) MainWindow.Close(); else Shutdown(0);
                })), null, Timeout.Infinite, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is WaitHandleCannotBeOpenedException)
            {
                AppLog.Error("quit signal", ex);
            }
        }

        /// <summary>Signals the copy running from this folder to close and waits up to 90 s for it. 0 = closed (or none).</summary>
        private static int QuitRunningCopy(string root)
        {
            var id = FolderId(root);
            Mutex running;
            try { running = Mutex.OpenExisting(@"Local\LuminaDB-" + id); }
            catch (WaitHandleCannotBeOpenedException) { return 0; }
            catch (UnauthorizedAccessException) { return 1; }
            using (running)
            {
                try
                {
                    using (var signal = EventWaitHandle.OpenExisting(@"Local\LuminaDB-quit-" + id)) signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { return 1; }
                try
                {
                    if (!running.WaitOne(TimeSpan.FromSeconds(90))) return 1;
                    running.ReleaseMutex();
                    return 0;
                }
                catch (AbandonedMutexException) { return 0; }       // it exited without releasing: it's gone either way
            }
        }

        private bool AcquireSingleInstance(string root)
        {
            var id = FolderId(root);
            _singleInstance = new Mutex(true, @"Local\LuminaDB-" + id, out bool created);
            if (created) return true;

            // Already open: bring that window to the front instead of starting a second server.
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id))
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9 /* SW_RESTORE */);
                    SetForegroundWindow(h);
                    break;
                }
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
