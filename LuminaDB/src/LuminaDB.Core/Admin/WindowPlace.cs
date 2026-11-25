using System;
using System.IO;
using System.Runtime.Serialization;

namespace LuminaDB.Core.Admin
{
    /// <summary>
    /// config\window.json: where the main window was and how big, so it opens the way it was left.
    /// All numbers are device-independent pixels (WPF units). The geometry is here, apart from WPF, so it can be tested.
    /// </summary>
    [DataContract]
    public sealed class WindowPlace
    {
        /// <summary>The window's normal (not maximised) bounds.</summary>
        [DataMember] public double Left { get; set; }
        [DataMember] public double Top { get; set; }
        [DataMember] public double Width { get; set; }
        [DataMember] public double Height { get; set; }
        [DataMember] public bool Maximized { get; set; }

        /// <summary>The size the design is drawn for; bigger screens get this, smaller ones get the whole screen.</summary>
        public const double DesignWidth = 1280, DesignHeight = 800;
        /// <summary>The smallest window every page is laid out for (smaller screens lower it to fit).</summary>
        public const double MinWidth = 980, MinHeight = 600;

        public static string FileFor(AppPaths p) => Path.Combine(p.Config, "window.json");

        public static WindowPlace Load(AppPaths p)
        {
            var w = JsonFile.Read<WindowPlace>(FileFor(p));
            if (w == null || !IsNumber(w.Left) || !IsNumber(w.Top) || !(w.Width >= 200) || !(w.Height >= 150) || w.Width > 20000 || w.Height > 20000) return null;
            return w;
        }

        public void Save(AppPaths p) => JsonFile.Write(FileFor(p), this);

        private static bool IsNumber(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 100000;

        /// <summary>
        /// The first time on a screen: a screen that can't hold the design size comfortably (a 1366×768 laptop, or any
        /// screen at 125-150% scaling) opens maximised; a bigger one gets the design size, centred.
        /// </summary>
        public static WindowPlace FirstRun(Area work)
        {
            bool small = work.Width < DesignWidth + 80 || work.Height < DesignHeight + 60;
            double w = Math.Min(DesignWidth, work.Width), h = Math.Min(DesignHeight, work.Height);
            return new WindowPlace
            {
                Width = w, Height = h,
                Left = work.Left + Math.Round((work.Width - w) / 2),
                Top = work.Top + Math.Round((work.Height - h) / 2),
                Maximized = small,
            };
        }

        /// <summary>
        /// Puts saved bounds fully on <paramref name="work"/> (the work area of the screen they were on, or the main one
        /// if that screen is gone): no larger than it, and the title bar always reachable. A position already inside is kept.
        /// </summary>
        public WindowPlace FitTo(Area work)
        {
            double w = Math.Max(Math.Min(MinWidth, work.Width), Math.Min(Width, work.Width));
            double h = Math.Max(Math.Min(MinHeight, work.Height), Math.Min(Height, work.Height));
            double left = Math.Min(Math.Max(Left, work.Left), work.Right - w);
            double top = Math.Min(Math.Max(Top, work.Top), work.Bottom - h);
            return new WindowPlace { Left = Math.Round(left), Top = Math.Round(top), Width = Math.Round(w), Height = Math.Round(h), Maximized = Maximized };
        }

        /// <summary>A rectangle in device-independent pixels.</summary>
        public struct Area
        {
            public Area(double left, double top, double width, double height) { Left = left; Top = top; Width = width; Height = height; }
            public double Left, Top, Width, Height;
            public double Right => Left + Width;
            public double Bottom => Top + Height;
        }
    }
}
