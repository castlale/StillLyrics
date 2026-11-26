using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed partial class Island
    {
        private const int NoticeMotionMs = 1680;
        private const int NoticeExitMs = 220;
        private double? noticeExitStart;
        private double exitCardAge;
        private Font noticeFont;

        private void BeginNoticeExit()
        {
            if (!notice.Active || noticeExitStart.HasValue) return;
            if (!animationsAllowed || SystemInformation.HighContrast || !Visible) { ClearNotice(); return; }
            exitCardAge = NoticeAnimationAge();
            noticeExitStart = clock.Elapsed.TotalMilliseconds;
            UpdateAnimationClock();
            Invalidate();
        }

        private bool NoticeExitFinished()
        {
            return !animationsAllowed || SystemInformation.HighContrast || !Visible ||
                clock.Elapsed.TotalMilliseconds - noticeExitStart.Value >= NoticeExitMs;
        }

        private float NoticeEntryEdge(double age)
        {
            float origin = 53 * Height / 84f;
            double remaining = 1 - Math.Min(1, age / 340.0);
            return (float)(Width - (Width - origin) * remaining * remaining * remaining);
        }

        private float NoticeExitEdge(double elapsed)
        {
            double p = Math.Min(1, Math.Max(0, elapsed / NoticeExitMs));
            return (float)(NoticeEntryEdge(exitCardAge) * (1 - p * p * (3 - 2 * p)));
        }

        private bool OverNotice(Point point)
        {
            return notice.Active && ClientRectangle.Contains(point) && (Region == null || Region.IsVisible(point)) &&
                (!noticeExitStart.HasValue || point.X < NoticeExitEdge(clock.Elapsed.TotalMilliseconds - noticeExitStart.Value));
        }

        private double NoticeAnimationAge()
        {
            return animationsAllowed && !SystemInformation.HighContrast
                ? Math.Min(NoticeMotionMs, clock.Elapsed.TotalMilliseconds - notice.StartedAt) : NoticeMotionMs;
        }

        private void PaintNotice(Graphics g, double age)
        {
            bool contrast = SystemInformation.HighContrast;
            Color ink = contrast ? SystemColors.HighlightText : Color.FromArgb(7, 46, 36);
            Color green = contrast ? SystemColors.Highlight : Color.FromArgb(50, 240, 148);
            Color lime = contrast ? green : Color.FromArgb(185, 255, 123);
            float unit = Height / 84f;
            float center = Height / 2f, origin = 53 * unit;
            var state = g.Save();
            float leadingEdge = NoticeEntryEdge(age);
            if (noticeExitStart.HasValue)
                leadingEdge = NoticeExitEdge(clock.Elapsed.TotalMilliseconds - noticeExitStart.Value);
            g.SetClip(new RectangleF(0, 0, leadingEdge, Height), CombineMode.Intersect);
            using (var fill = new LinearGradientBrush(ClientRectangle, green, lime, 0f))
                g.FillRectangle(fill, ClientRectangle);

            if (!contrast)
            {
                // Fixed arcs give the resting card a distinct silhouette. Moving waves pass behind the text.
                using (var arc = new Pen(Color.FromArgb(38, ink), 2 * unit))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float radius = (45 + 15 * i) * unit;
                        g.DrawEllipse(arc, Width + 16 * unit - radius, center - radius, 2 * radius, 2 * radius);
                    }
                }
                for (int wave = 0; wave < 2; wave++)
                {
                    double p = (age - 140 - wave * 400) / 1140.0;
                    if (p < 0 || p >= 1) continue;
                    float radius = 25 * unit + (Width - origin) * (float)p;
                    using (var pen = new Pen(Color.FromArgb((int)(92 * (1 - p)), ink), (float)(12 - 8 * p) * unit))
                        g.DrawEllipse(pen, origin - radius, center - radius, 2 * radius, 2 * radius);
                }
            }

            var badge = g.Save();
            double pop = Math.Min(1, age / 470.0);
            float scale = (float)(1 + 0.6 * Math.Pow(1 - pop, 2) * (2.9 * pop - 1));
            g.TranslateTransform(origin, center);
            g.ScaleTransform(unit * scale, unit * scale);
            using (var dark = new SolidBrush(ink))
            using (var light = new SolidBrush(contrast ? green : Color.FromArgb(207, 255, 198)))
            {
                g.FillEllipse(dark, -28, -28, 56, 56);
                g.FillEllipse(light, -17, -13, 28, 21);
                g.FillPolygon(light, new[] { new Point(-13, 3), new Point(-13, 11), new Point(-5, 6) });
                g.FillEllipse(dark, -10, -6, 3, 3);
                g.FillEllipse(dark, -2, -6, 3, 3);
                g.FillEllipse(dark, -2, -3, 22, 18);
                g.FillEllipse(light, 0, -1, 20, 15);
                g.FillPolygon(light, new[] { new Point(11, 11), new Point(17, 16), new Point(16, 9) });
            }
            g.Restore(badge);

            float arrowX = Width - 33 * unit;
            using (var arrow = new Pen(ink, 2 * unit))
            {
                g.DrawLine(arrow, arrowX - 9 * unit, center, arrowX + 6 * unit, center);
                g.DrawLine(arrow, arrowX, center - 6 * unit, arrowX + 6 * unit, center);
                g.DrawLine(arrow, arrowX, center + 6 * unit, arrowX + 6 * unit, center);
            }
            float left = 96 * unit, width = Math.Max(1, Width - left - 55 * unit);
            bool hasSender = notice.Heading != "微信";
            using (var brush = new SolidBrush(ink))
            using (var format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.LineAlignment = StringAlignment.Center;
                if (hasSender)
                    g.DrawString(notice.Heading, captionFont, brush, new RectangleF(left, 5 * unit, width, 23 * unit), format);
                float top = hasSender ? 23 * unit : 0;
                g.DrawString(notice.Body, noticeFont, brush, new RectangleF(left, top, width, Height - top), format);
            }
            g.Restore(state);
        }
    }
}
