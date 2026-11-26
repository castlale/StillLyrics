using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed partial class Island
    {
        private GraphicsPath glassOutline;
        private Bitmap glass;
        private FrostedBackdrop backdrop;
        private Rectangle sampledArea;
        private long nextCapture;
        private const int TintAlpha = 168;

        private void ReleaseBackdrop()
        {
            if (backdrop != null)
            {
                var old = backdrop;
                backdrop = null;
                old.Dispose();
            }
            sampledArea = Rectangle.Empty;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterWeChatShell();
            ReleaseBackdrop();
            base.OnHandleDestroyed(e);
        }

        internal void UpdateBackdrop(long now)
        {
            if (closed || !Visible || !View.Glass || SystemInformation.HighContrast || !IsHandleCreated)
            {
                if (backdrop != null) { ReleaseBackdrop(); Invalidate(true); }
                return;
            }
            var area = RectangleToScreen(ClientRectangle);
            if (menu.Visible || dialogOpen) return;
            if (area.Width <= 0 || area.Height <= 0) return;
            if (now < nextCapture && (sampledArea == area || sampledArea.IsEmpty)) return;
            nextCapture = now + 150;
            if (backdrop == null || sampledArea.Size != area.Size)
            {
                ReleaseBackdrop();
                backdrop = new FrostedBackdrop(Handle, area.Size, Math.Max(5, Px(10)));
            }
            bool changed;
            if (!backdrop.Capture(area, out changed))
            {
                ReleaseBackdrop();
                nextCapture = now + 5000;
                Invalidate(true);
                return;
            }
            if (changed || sampledArea != area) Invalidate(true);
            sampledArea = area;
        }

        // Keep automatic black/white ink readable at normal opacity, including over a black or white window.
        private Color FrostTint()
        {
            if (!HasCover && !PaletteAnimating) return BackColor;
            bool blackInk = Ink.ToArgb() == Color.Black.ToArgb();
            int end = blackInk ? 255 : 0;
            for (int step = 0; step < 16; step++)
            {
                int r = (BackColor.R * (16 - step) + end * step) / 16;
                int g = (BackColor.G * (16 - step) + end * step) / 16;
                int b = (BackColor.B * (16 - step) + end * step) / 16;
                int under = blackInk ? 0 : 255 * (255 - TintAlpha);
                var worst = Color.FromArgb((r * TintAlpha + under) / 255, (g * TintAlpha + under) / 255, (b * TintAlpha + under) / 255);
                double l = CoverColors.Luminance(worst) + .05;
                if ((blackInk ? l / .05 : 1.05 / l) >= 4.55) return Color.FromArgb(r, g, b);
            }
            return Color.FromArgb(end, end, end);
        }
        private int reflectionX = -1;
        private Color paletteFrom, paletteTo;
        private double paletteStarted;
        internal bool PaletteAnimating { get; private set; }

        private void ClearGlass()
        {
            if (glass != null) { glass.Dispose(); glass = null; }
        }

        private void UpdateReflection(int x)
        {
            if (!View.Glass || !animationsAllowed || SystemInformation.HighContrast) x = -1;
            if (x >= 0) x = x / 4 * 4;
            if (reflectionX == x) return;
            reflectionX = x;
            ClearGlass();
            Invalidate(true);
        }

        private void SetPalette(bool animate)
        {
            Color target = HasCover ? coverColor.Value : Color.FromArgb(View.Background);
            if (animate && View.Glass && animationsAllowed && Visible && !SystemInformation.HighContrast &&
                BackColor.ToArgb() != target.ToArgb())
            {
                if (PaletteAnimating && paletteTo == target) return;
                paletteFrom = BackColor;
                paletteTo = target;
                paletteStarted = clock.Elapsed.TotalMilliseconds;
                PaletteAnimating = true;
            }
            else
            {
                PaletteAnimating = false;
                BackColor = target;
            }
            UpdateAnimationClock();
            Invalidate(true);
        }

        internal void AdvancePalette(double elapsed)
        {
            if (!PaletteAnimating) return;
            double t = Math.Max(0, Math.Min(1, elapsed / 350.0));
            if (t >= 1 || !Visible || !animationsAllowed || SystemInformation.HighContrast || !View.Glass)
            {
                PaletteAnimating = false;
                BackColor = paletteTo;
            }
            else
            {
                t = t * t * (3 - 2 * t);
                BackColor = Color.FromArgb(
                    (int)Math.Round(paletteFrom.R + (paletteTo.R - paletteFrom.R) * t),
                    (int)Math.Round(paletteFrom.G + (paletteTo.G - paletteFrom.G) * t),
                    (int)Math.Round(paletteFrom.B + (paletteTo.B - paletteFrom.B) * t));
            }
            Invalidate(true);
        }

        private Bitmap RenderRim()
        {
            var rim = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(rim))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bevel = new LinearGradientBrush(ClientRectangle, Color.Transparent, Color.Transparent, 90f))
                {
                    bevel.InterpolationColors = new ColorBlend {
                        Positions = new[] { 0f, .23f, .60f, 1f },
                        Colors = new[] { Color.FromArgb(65, Color.White), Color.Transparent, Color.Transparent, Color.FromArgb(48, Color.Black) }
                    };
                    using (var lip = new Pen(bevel, Math.Max(2, Px(8)))) g.DrawPath(lip, glassOutline);
                }
                float peak = reflectionX < 0 ? .13f : Math.Max(.035f, Math.Min(.30f, reflectionX / (float)Width));
                using (var glint = new LinearGradientBrush(ClientRectangle, Color.Transparent, Color.Transparent, 0f))
                {
                    glint.InterpolationColors = new ColorBlend {
                        Positions = new[] { 0f, peak, peak + .18f, .62f, .88f, 1f },
                        Colors = new[] { Color.FromArgb(65, Color.White), Color.FromArgb(203, Color.White), Color.Transparent,
                            Color.Transparent, Color.FromArgb(86, Color.Black), Color.FromArgb(38, Color.White) }
                    };
                    using (var edge = new Pen(glint, Math.Max(2, Px(3)))) g.DrawPath(edge, glassOutline);
                }
            }
            return rim;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (notice.Active && !noticeExitStart.HasValue && NoticeAnimationAge() >= 340) return;
            base.OnPaintBackground(e);
            if (library == null || !View.Glass || glassOutline == null || SystemInformation.HighContrast) return;
            if (backdrop != null && backdrop.Surface != null && sampledArea == RectangleToScreen(ClientRectangle))
            {
                var surface = backdrop.Surface;
                var state = e.Graphics.Save();
                using (var edges = new ImageAttributes())
                using (var tint = new SolidBrush(Color.FromArgb(TintAlpha, FrostTint())))
                {
                    edges.SetWrapMode(WrapMode.TileFlipXY);
                    e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
                    e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    e.Graphics.DrawImage(surface, ClientRectangle, 0, 0, surface.Width, surface.Height, GraphicsUnit.Pixel, edges);
                    e.Graphics.FillRectangle(tint, ClientRectangle);
                }
                e.Graphics.Restore(state);
            }
            var flank = Rectangle.FromLTRB(TextColumn.Right, 0, Width, Height);
            if (flank.Width > 0)
            {
                Color tone = CoverColors.Luminance(Ink) > CoverColors.Luminance(BackColor) ? Color.Black : Color.White;
                using (var wash = new LinearGradientBrush(flank, Color.FromArgb(0, tone), Color.FromArgb(23, tone), 0f))
                    e.Graphics.FillRectangle(wash, flank);
            }
            if (glass == null) glass = RenderRim();
            e.Graphics.DrawImageUnscaled(glass, 0, 0);
        }
    }
}
