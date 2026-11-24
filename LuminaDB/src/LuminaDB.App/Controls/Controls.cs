using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LuminaDB.App.ViewModels;
using LuminaDB.Core;

namespace LuminaDB.App.Controls
{
    /// <summary>The heartbeat line under the masthead: queries per second, drawn as one cheap polyline.</summary>
    public sealed class ActivityTrace : FrameworkElement
    {
        public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
            nameof(Samples), typeof(double[]), typeof(ActivityTrace), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(ActivityTrace), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public double[] Samples { get => (double[])GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            var s = Samples;
            double w = ActualWidth, h = ActualHeight;
            if (s == null || s.Length < 2 || w <= 0 || h <= 0) return;
            // Scale to the busiest moment on screen, but never below 10 q/s so idle noise stays flat.
            double max = Math.Max(10, s.Max());
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int i = 0; i < s.Length; i++)
                {
                    var p = new Point(i * w / (s.Length - 1), h - 1 - Math.Min(s[i], max) / max * (h - 3));
                    if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
                }
            }
            geo.Freeze();
            var pen = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }

    /// <summary>PasswordBox.Password can't be bound (by design); this pushes it into the view-model.</summary>
    public static class PasswordSink
    {
        public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
            "Target", typeof(PasswordSlip), typeof(PasswordSink), new PropertyMetadata(null, OnTarget));

        public static void SetTarget(DependencyObject d, PasswordSlip v) => d.SetValue(TargetProperty, v);
        public static PasswordSlip GetTarget(DependencyObject d) => (PasswordSlip)d.GetValue(TargetProperty);

        private static void OnTarget(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is PasswordBox box)) return;
            box.PasswordChanged -= Changed;
            box.PasswordChanged += Changed;
        }

        private static void Changed(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            var t = GetTarget(box);
            if (t != null) t.Password = box.Password;
        }
    }

    /// <summary>Focuses an element as soon as it appears (first field of a slip).</summary>
    public static class AutoFocus
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(AutoFocus), new PropertyMetadata(false, (d, e) =>
            {
                if (d is FrameworkElement fe && (bool)e.NewValue)
                    fe.Loaded += (s, _) => { fe.Focus(); if (fe is TextBox tb) tb.SelectAll(); };
            }));
        public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);
        public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    }

    // ------------------------------------------------------------------ converters

    /// <summary>Result cells: NULL (DBNull) is shown as a "NULL" chip, never as an empty string.</summary>
    public sealed class NullCell : IValueConverter
    {
        public static readonly NullCell IsNull = new NullCell();
        public object Convert(object value, Type t, object p, CultureInfo c) => value == null || value is DBNull;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class BoolToVisibility : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            bool b = value is bool v && v;
            if (value is string s) b = !string.IsNullOrEmpty(s);
            if (value is int i) b = i != 0;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class InvertBool : IValueConverter
    {
        public static readonly InvertBool Instance = new InvertBool();
        public object Convert(object value, Type t, object p, CultureInfo c) => !(value is bool b && b);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => !(value is bool b && b);
    }

    /// <summary>Picks a brush from the Ledger palette by tone / severity / slip kind / tab colour.</summary>
    public sealed class PaletteBrush : IValueConverter
    {
        /// <summary>"fg" (text colour) or "soft" (chip background).</summary>
        public string Mode { get; set; } = "fg";

        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            string key;
            switch (value)
            {
                case StatusTone tone:
                    key = tone == StatusTone.Ok ? "Green" : tone == StatusTone.Bad ? "Red" : tone == StatusTone.Busy ? "Amber" : "Ink3";
                    break;
                case CheckSeverity sev:
                    key = sev == CheckSeverity.Ok ? "Green" : sev == CheckSeverity.Warn ? "Amber" : "Red";
                    if (Mode == "soft") key += "Soft";
                    break;
                case int tab:
                    key = "Tab" + Math.Max(1, Math.Min(5, tab));
                    break;
                case string kind:
                    key = kind == "ok" ? "Green" : kind == "bad" ? "Red" : kind == "run" ? "Amber" : "Ink3";
                    break;
                default:
                    key = "Ink";
                    break;
            }
            return Application.Current.TryFindResource(key) ?? Brushes.Black;
        }

        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Stop square while running, play triangle otherwise.</summary>
    public sealed class RunStopIcon : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            Application.Current.TryFindResource(value is bool b && b ? "IconStop" : "IconPlay");
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class SeverityLabel : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value is CheckSeverity s ? (s == CheckSeverity.Ok ? "OK" : s == CheckSeverity.Warn ? "NOTE" : "FIX") : "";
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class NumberFormat : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value is long l ? l.ToString("N0", CultureInfo.InvariantCulture) : value is int i ? i.ToString("N0", CultureInfo.InvariantCulture) : value;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Chooses Today or the "planned section" page.</summary>
    public sealed class SectionTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Today { get; set; }
        public DataTemplate Planned { get; set; }
        public DataTemplate Notebook { get; set; }
        public DataTemplate Tables { get; set; }
        public DataTemplate Projects { get; set; }
        public DataTemplate Mail { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            switch ((item as Section)?.Key)
            {
                case "today": return Today;
                case "notebook": return Notebook;
                case "tables": return Tables;
                case "projects": return Projects ?? Planned;
                case "mail": return Mail ?? Planned;
                default: return Planned;
            }
        }
    }

    public sealed class SlipTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Message { get; set; }
        public DataTemplate NewDatabase { get; set; }
        public DataTemplate Password { get; set; }
        public DataTemplate Choice { get; set; }
        public DataTemplate NewProject { get; set; }
        public DataTemplate SaveSnippet { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is NewDatabaseSlip ? NewDatabase : item is PasswordSlip ? Password : item is MessageSlip ? Message : item is ChoiceSlip ? Choice
            : item is NewProjectSlip ? NewProject : item is SaveSnippetSlip ? SaveSnippet : null;
    }
}
