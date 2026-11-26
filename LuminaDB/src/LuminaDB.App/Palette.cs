using System;
using System.Linq;
using System.Windows;
using LuminaDB.App.Editor;
using LuminaDB.Core;

namespace LuminaDB.App
{
    /// <summary>
    /// Settings › Dark theme, switched while LuminaDB runs. The colours live in Theme/Palette.xaml (Ledger) and
    /// Theme/PaletteNight.xaml (Night); the XAML reads them with DynamicResource, so swapping the dictionary recolours every
    /// page at once. Code that drew with a colour (the SQL editors, the diagram lines) listens to <see cref="Changed"/>.
    /// </summary>
    public static class Palette
    {
        private const string Folder = "pack://application:,,,/LuminaDB;component/Theme/";

        /// <summary>The Night colours are in use.</summary>
        public static bool Night { get; private set; }

        /// <summary>Raised after the colours changed, for code that drew with the old ones.</summary>
        public static event Action Changed;

        public static void Apply(bool night)
        {
            var app = Application.Current;
            if (app == null || night == Night) return;
            var dicts = app.Resources.MergedDictionaries;
            int i = dicts.ToList().FindIndex(d => d.Source != null && d.Source.OriginalString.EndsWith(".xaml") &&
                                                  System.IO.Path.GetFileName(d.Source.OriginalString).StartsWith("Palette"));
            if (i < 0) return;
            dicts[i] = new ResourceDictionary { Source = new Uri(Folder + (night ? "PaletteNight.xaml" : "Palette.xaml"), UriKind.Absolute) };
            Night = night;
            AppLog.Info("theme: " + (night ? "night" : "ledger"));
            Ledger.Recolour();
            SqlHighlighting.Reset();
            Changed?.Invoke();
        }
    }
}
