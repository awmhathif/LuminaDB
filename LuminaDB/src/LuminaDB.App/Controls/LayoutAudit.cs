using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LuminaDB.App.Controls
{
    /// <summary>
    /// Development check (used with LUMINA_SNAP): lists every visible element that WPF had to cut because it didn't fit
    /// the space it was given (a layout clip narrower or shorter than the element). That is how a button loses its edge
    /// or a label loses its last letters without an ellipsis. Scrolling areas don't count: they scroll, not cut.
    /// </summary>
    public static class LayoutAudit
    {
        public static string Report(DependencyObject root)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            Walk(root, sb, seen);
            return sb.ToString();
        }

        private static void Walk(DependencyObject d, StringBuilder sb, HashSet<string> seen)
        {
            if (d is UIElement u && !u.IsVisible) return;
            if (d is FrameworkElement fe && !(d is Window) && !(d is ScrollContentPresenter) && !(d is Popup))
            {
                var clip = LayoutInformation.GetLayoutClip(fe);
                if (clip != null && !clip.Bounds.IsEmpty)
                {
                    double cutW = fe.ActualWidth - clip.Bounds.Width, cutH = fe.ActualHeight - clip.Bounds.Height;
                    if (cutW > 1.5 || cutH > 1.5)
                    {
                        var line = string.Format(CultureInfo.InvariantCulture, "{0} \"{1}\" {2:0}x{3:0} cut {4:0}w {5:0}h  in {6}",
                            fe.GetType().Name, Hint(fe), fe.ActualWidth, fe.ActualHeight, Math.Max(0, cutW), Math.Max(0, cutH), Where(fe));
                        if (seen.Add(line)) sb.AppendLine(line);
                    }
                }
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), sb, seen);
        }

        private static string Hint(DependencyObject d) => Hint(d, describePaths: true);

        /// <summary>A path describes itself plus a nearby label; that label search skips paths so it can't loop back.</summary>
        private static string Hint(DependencyObject d, bool describePaths)
        {
            var name = AutomationProperties.GetName(d);
            if (!string.IsNullOrEmpty(name)) return name;
            if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) return Short(tb.Text);
            if (d is System.Windows.Shapes.Path path)
            {
                if (!describePaths || path.Data == null) return "";
                string around = "";
                var p = VisualTreeHelper.GetParent(d);
                for (int up = 0; up < 3 && p != null && around.Length == 0; up++, p = VisualTreeHelper.GetParent(p))
                    around = Hint(p, describePaths: false);
                return "path " + Short(path.Data.ToString()) + (around.Length > 0 ? " near " + around : "");
            }
            if (d is ContentControl cc && cc.Content is string s) return Short(s);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var h = Hint(VisualTreeHelper.GetChild(d, i), describePaths);
                if (!string.IsNullOrEmpty(h)) return h;
            }
            return "";
        }

        private static string Short(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

        /// <summary>The nearest named view (UsersView, BackupsView…) so the report says which page.</summary>
        private static string Where(DependencyObject d)
        {
            for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
                if (p is UserControl || p is Window) return p.GetType().Name;
            return "?";
        }
    }
}
