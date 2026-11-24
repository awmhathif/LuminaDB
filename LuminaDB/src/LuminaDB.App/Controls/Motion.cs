using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LuminaDB.App.Controls
{
    /// <summary>
    /// One way to move things, so every slide in the app feels the same: about 200 ms, fast start, soft landing.
    /// Works on anything with a number (column widths, row heights, opacity). With Windows animations turned off
    /// (Settings › Accessibility › Visual effects) the value jumps straight to the end.
    /// </summary>
    public static class Motion
    {
        public const int Duration = 210;

        public static bool Enabled => SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

        private static readonly Dictionary<object, Tween> Running = new Dictionary<object, Tween>();

        /// <summary>
        /// Moves a value from <paramref name="from"/> to <paramref name="to"/>. A new tween on the same <paramref name="key"/>
        /// replaces the old one (no fighting when a button is pressed twice quickly).
        /// </summary>
        public static void Run(object key, double from, double to, Action<double> set, Action done = null, int ms = Duration)
        {
            if (Running.TryGetValue(key, out var old)) { old.Stop(); Running.Remove(key); }
            if (!Enabled || ms <= 0 || Math.Abs(from - to) < 0.5)
            {
                set(to);
                done?.Invoke();
                return;
            }
            var t = new Tween(from, to, ms, set, () => { Running.Remove(key); done?.Invoke(); });
            Running[key] = t;
            t.Start();
        }

        public static bool IsRunning(object key) => Running.ContainsKey(key);

        /// <summary>Ease-out cubic: quick to respond, gentle to stop.</summary>
        public static double Ease(double t) => 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, t)), 3);

        private sealed class Tween
        {
            private readonly double _from, _to, _ms;
            private readonly Action<double> _set;
            private readonly Action _done;
            private TimeSpan? _start;
            private bool _stopped;

            public Tween(double from, double to, double ms, Action<double> set, Action done) { _from = from; _to = to; _ms = ms; _set = set; _done = done; }

            public void Start() => CompositionTarget.Rendering += OnFrame;

            public void Stop()
            {
                _stopped = true;
                CompositionTarget.Rendering -= OnFrame;
            }

            private void OnFrame(object sender, EventArgs e)
            {
                if (_stopped) return;
                var now = ((RenderingEventArgs)e).RenderingTime;
                if (_start == null) _start = now;
                double t = (now - _start.Value).TotalMilliseconds / _ms;
                if (t >= 1)
                {
                    Stop();
                    _set(_to);
                    _done?.Invoke();
                    return;
                }
                _set(_from + (_to - _from) * Ease(t));
            }
        }
    }
}
