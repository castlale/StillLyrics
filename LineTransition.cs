using System;

namespace StillLyrics
{
    internal sealed class LineTransition
    {
        internal const int DurationMs = 240;
        private Line last, expected;
        private double lastPosition;
        private long lastTick, started;
        private bool translated;
        private string secondary;
        private int highlighted;
        internal Line Outgoing { get; private set; }
        internal int OldHighlight { get; private set; }
        internal string OldSecondary { get; private set; }
        internal bool PromotePreview { get; private set; }

        internal void Observe(Line current, Line next, double position, long now, bool allowed,
            bool translation, string subtitle, int highlight)
        {
            long elapsed = now - lastTick;
            double movement = position - lastPosition;
            bool continuous = elapsed >= 0 && elapsed < 500 && movement >= 0 &&
                movement <= elapsed / 1000.0 + 0.35;
            if (!allowed || !continuous || current == null || current.Text.Length == 0)
                Outgoing = null;
            else if (last != current)
            {
                Outgoing = current == expected && last != null && last.Text.Length > 0 ? last : null;
                if (Outgoing != null)
                {
                    started = now;
                    PromotePreview = !translated && secondary == current.Text;
                    OldSecondary = secondary;
                    OldHighlight = highlighted;
                }
            }
            else if (translation != translated || subtitle != secondary) Outgoing = null;
            if (now - started >= DurationMs) Outgoing = null;
            last = allowed ? current : null;
            expected = allowed ? next : null;
            translated = translation;
            secondary = subtitle;
            highlighted = highlight;
            lastPosition = position;
            lastTick = now;
        }

        internal float Progress(double now)
        {
            float t = (float)Math.Min(1, Math.Max(0, (now - started) / DurationMs));
            return t * t * (3f - 2f * t);
        }
    }
}
