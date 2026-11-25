using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LuminaDB.Core;

namespace LuminaDB.App
{
    /// <summary>
    /// The "something went wrong" box: what happened in a sentence, the report to read, and Copy report, Save report and
    /// Where to send it. Nothing is ever sent by itself.
    /// </summary>
    public sealed class ErrorReportWindow : Window
    {
        /// <summary>Where people send reports (the issue tracker; changes with the release site).</summary>
        public const string ReportUrl = "https://github.com/awmhathif/LuminaDB/issues";

        private readonly string _report;
        private readonly AppPaths _paths;
        private readonly TextBlock _status;

        public ErrorReportWindow(string message, string report, AppPaths paths, string heading = null)
        {
            _report = report;
            _paths = paths;
            Title = "LuminaDB";
            Width = 640;
            Height = 520;
            MinWidth = 460;
            MinHeight = 360;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush("Paper", Color.FromRgb(0xF3, 0xEE, 0xE1));
            if (Environment.GetEnvironmentVariable("LUMINA_OFFSCREEN") == "1")
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000; Top = 0;
                ShowActivated = false;
            }

            var root = new DockPanel { Margin = new Thickness(24, 20, 24, 18) };

            var head = new StackPanel();
            head.Children.Add(new TextBlock
            {
                Text = heading ?? "Something went wrong, but your data is safe.",
                FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brush("Ink", Colors.Black), TextWrapping = TextWrapping.Wrap,
            });
            head.Children.Add(new TextBlock
            {
                Text = message, Margin = new Thickness(0, 6, 0, 0), FontSize = 13.5, Foreground = Brush("Ink", Colors.Black), TextWrapping = TextWrapping.Wrap,
            });
            head.Children.Add(new TextBlock
            {
                Text = (heading == null ? "LuminaDB keeps running. If you'd like this fixed, copy" : "Copy") + " or save the report below and send it to the developer. " +
                       "It holds no databases, passwords or SQL, and your name and PC name are replaced. Nothing is sent by itself.",
                Margin = new Thickness(0, 10, 0, 10), FontSize = 12.5, Foreground = Brush("Ink2", Colors.DimGray), TextWrapping = TextWrapping.Wrap,
            });
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
            var close = MakeButton("Close", "ToolPrimary", (s, e) => Close());
            close.IsCancel = true;
            close.IsDefault = true;
            DockPanel.SetDock(close, Dock.Right);
            buttons.Children.Add(close);
            buttons.Children.Add(MakeButton("Copy report", "ToolButton", (s, e) => CopyReport()));
            buttons.Children.Add(MakeButton("Save report", "ToolButton", (s, e) => SaveReport()));
            buttons.Children.Add(MakeButton("Where to send it", "ToolButton", (s, e) => Open(ReportUrl)));
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 12, Foreground = Brush("Ink2", Colors.DimGray), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            var box = new TextBox
            {
                Text = report, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontSize = 11.5,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brush("Paper3", Colors.White), BorderBrush = Brush("Rule", Colors.Gray), Padding = new Thickness(8),
            };
            if (Application.Current?.TryFindResource("FontMono") is FontFamily mono) box.FontFamily = mono;
            System.Windows.Automation.AutomationProperties.SetName(box, "Error report");
            root.Children.Add(box);

            Content = root;
        }

        private static Brush Brush(string key, Color fallback) => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        private Button MakeButton(string text, string style, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            if (Application.Current?.TryFindResource(style) is Style st) b.Style = st;
            System.Windows.Automation.AutomationProperties.SetName(b, text);
            b.Click += click;
            return b;
        }

        private void CopyReport()
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(_report); _status.Text = "Copied. Paste it into an e-mail or the issue page."; return; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }
            }
            _status.Text = "The clipboard is busy. Try again.";
        }

        private void SaveReport()
        {
            try
            {
                var file = ErrorReport.Save(_paths, _report);
                _status.Text = "Saved as " + file;
                if (Environment.GetEnvironmentVariable("LUMINA_OFFSCREEN") != "1")
                    Process.Start("explorer.exe", "/select,\"" + file + "\"");
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                _status.Text = "Couldn't save it: " + ex.Message;
            }
        }

        private void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { _status.Text = "Couldn't open the browser: " + ex.Message; }
        }
    }
}
