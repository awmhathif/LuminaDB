using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LuminaDB.App.ViewModels;
using WF = System.Windows.Forms;

namespace LuminaDB.App
{
    /// <summary>
    /// LuminaDB's icon in the notification area, like XAMPP's: a dot shows whether the database runs (green), is starting
    /// or stopping (amber), stopped by itself (red) or is stopped (grey). Double-click opens the window; right-click has
    /// the everyday actions and Quit.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly MainViewModel _vm;
        private readonly Action _open, _quit;
        private readonly WF.NotifyIcon _icon;
        private readonly WF.ContextMenuStrip _menu;
        private readonly WF.ToolStripMenuItem _openItem, _serverItem, _webItem, _pmaItem, _folderItem, _quitItem;
        private Icon _current;
        private IntPtr _currentHandle;
        private string _drawn;
        private bool _quitting;

        public TrayIcon(MainViewModel vm, Action open, Action quit)
        {
            _vm = vm;
            _open = open;
            _quit = quit;

            _menu = new WF.ContextMenuStrip { ShowImageMargin = false, Renderer = new PaperRenderer(), Font = new Font("Segoe UI", 9f) };
            _openItem = Item("Open LuminaDB", (s, e) => _open());
            _openItem.Font = new Font(_menu.Font, FontStyle.Bold);
            _serverItem = Item("Start the database", (s, e) => Run(_vm.ToggleServerCommand));
            _webItem = Item("Start the web server", (s, e) => Run(_vm.ToggleWebCommand));
            _pmaItem = Item("Open phpMyAdmin", (s, e) => _vm.OpenLink(_vm.Web.PhpMyAdminUrl));
            _folderItem = Item("Open the projects folder", (s, e) => OpenFolder(_vm.Paths.Projects));
            _quitItem = Item("Quit LuminaDB", (s, e) => _quit());
            _menu.Items.AddRange(new WF.ToolStripItem[]
            {
                _openItem, new WF.ToolStripSeparator(), _serverItem, _webItem, _pmaItem, _folderItem, new WF.ToolStripSeparator(), _quitItem,
            });
            _menu.Opening += (s, e) => UpdateMenu();

            _icon = new WF.NotifyIcon { ContextMenuStrip = _menu };
            _icon.MouseDoubleClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) _open(); };
            _icon.BalloonTipClicked += (s, e) => _open();
            Update();
            _icon.Visible = true;
            _vm.PropertyChanged += OnVmChanged;
        }

        private static WF.ToolStripMenuItem Item(string text, EventHandler click)
        {
            var item = new WF.ToolStripMenuItem(text) { Padding = new WF.Padding(2, 3, 2, 3) };
            item.Click += click;
            return item;
        }

        private static void Run(System.Windows.Input.ICommand cmd)
        {
            if (cmd != null && cmd.CanExecute(null)) cmd.Execute(null);
        }

        private void OpenFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
            {
                AppLog.Error("tray: open folder", ex);
            }
        }

        private void UpdateMenu()
        {
            bool busy = _vm.IsBusy || _quitting;
            _serverItem.Text = _vm.IsRunning ? "Stop the database" : "Start the database";
            _serverItem.Enabled = !busy && _vm.ToggleServerCommand.CanExecute(null);
            _webItem.Visible = _vm.CanServeWeb;
            _webItem.Text = _vm.WebRunning ? "Stop the web server" : "Start the web server";
            _webItem.Enabled = !busy && !_vm.WebBusy && _vm.ToggleWebCommand.CanExecute(null);
            _pmaItem.Visible = _vm.Stack != null && _vm.Stack.HasPhpMyAdmin;
            _pmaItem.Enabled = _vm.WebRunning && _vm.Web != null && _vm.Web.PhpMyAdminOn;
            _quitItem.Enabled = !_quitting;
        }

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.Tone):
                case nameof(MainViewModel.IsRunning):
                case nameof(MainViewModel.PortText):
                case nameof(MainViewModel.WebRunning):
                case nameof(MainViewModel.StatusWord):
                    Update();
                    break;
            }
        }

        /// <summary>Quit was chosen (here or by an installer): the icon says so while the server stops.</summary>
        public void ShowQuitting()
        {
            _quitting = true;
            _icon.Text = "LuminaDB · closing, stopping the server safely…";
        }

        /// <summary>The first time the window is closed into the tray: one balloon saying where LuminaDB went.</summary>
        public void ShowStillRunningHint()
        {
            _icon.ShowBalloonTip(6000, "LuminaDB is still running",
                "It's here by the clock. Double-click to open it; right-click › Quit LuminaDB to close it. Settings › When I close the window can change this.",
                WF.ToolTipIcon.None);
        }

        private void Update()
        {
            if (_quitting) return;
            var tone = _vm.Tone;
            var text = "LuminaDB · database " + (_vm.IsRunning ? "on " + _vm.Server.Port : (_vm.StatusWord ?? "stopped").Trim());
            if (_vm.WebRunning && _vm.Web != null) text += " · web on " + _vm.Web.WebPort;
            _icon.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;

            var size = WF.SystemInformation.SmallIconSize;
            var key = tone + "@" + size.Width;
            if (key == _drawn) return;
            _drawn = key;
            var old = _current;
            var oldHandle = _currentHandle;
            _current = Draw(tone, size.Width, out _currentHandle);
            _icon.Icon = _current;
            old?.Dispose();
            if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
        }

        private static Color DotColor(StatusTone tone)
        {
            switch (tone)
            {
                case StatusTone.Ok: return Color.FromArgb(0x22, 0xA0, 0x55);
                case StatusTone.Busy: return Color.FromArgb(0xE0, 0x9A, 0x12);
                case StatusTone.Bad: return Color.FromArgb(0xD6, 0x3F, 0x21);
                default: return Color.FromArgb(0x8A, 0x86, 0x7C);
            }
        }

        /// <summary>LuminaDB's icon at the tray's size, with the status dot in the lower right corner.</summary>
        private static Icon Draw(StatusTone tone, int size, out IntPtr handle)
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/LuminaDB.ico"));
            using (var stream = res.Stream)
            using (var app = new Icon(stream, size, size))
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawIcon(app, new Rectangle(0, 0, size, size));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float d = Math.Max(7f, size * 0.5f);
                    float ring = Math.Max(1.5f, size / 12f);
                    var dot = new RectangleF(size - d, size - d, d, d);
                    using (var paper = new SolidBrush(Color.FromArgb(0xFA, 0xF6, 0xEC)))
                        g.FillEllipse(paper, dot);
                    using (var fill = new SolidBrush(DotColor(tone)))
                        g.FillEllipse(fill, RectangleF.Inflate(dot, -ring, -ring));
                }
                handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        public void Dispose()
        {
            _vm.PropertyChanged -= OnVmChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _current?.Dispose();
            if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
        }

        /// <summary>The menu in LuminaDB's paper colours instead of the grey Office look.</summary>
        private sealed class PaperRenderer : WF.ToolStripProfessionalRenderer
        {
            public PaperRenderer() : base(new PaperColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.FromArgb(0x1C, 0x1A, 0x16) : Color.FromArgb(0x9A, 0x95, 0x88);
                base.OnRenderItemText(e);
            }
        }

        private sealed class PaperColors : WF.ProfessionalColorTable
        {
            private static readonly Color Paper = Color.FromArgb(0xFA, 0xF6, 0xEC);
            private static readonly Color Hover = Color.FromArgb(0xEC, 0xE4, 0xD0);
            private static readonly Color Line = Color.FromArgb(0xC9, 0xC0, 0xAC);
            public override Color ToolStripDropDownBackground => Paper;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Paper;
            public override Color ImageMarginGradientBegin => Paper;
            public override Color ImageMarginGradientMiddle => Paper;
            public override Color ImageMarginGradientEnd => Paper;
        }
    }
}
