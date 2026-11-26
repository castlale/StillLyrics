using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed partial class Island
    {
        private readonly Button transport = new Button();
        private readonly Button previous = new Button(), nextSong = new Button();
        private readonly ToolTip transportTip = new ToolTip();
        private ToolStripMenuItem transportMenu;
        private bool sendingControl;
        private bool? expectedPlaying;
        private long confirmUntil;

        private Rectangle CoverBounds
        {
            get { return new Rectangle(Width - Px(60), (Height - Px(44)) / 2, Px(44), Px(44)); }
        }

        internal Rectangle TextColumn
        {
            get
            {
                int left = View.ShowTransport ? Px(132) : Px(16);
                int right = View.ShowCover ? Px(68) : Px(16);
                if (View.CenterText) left = right = Math.Max(left, right);
                return new Rectangle(left, 0, Math.Max(1, Width - left - right), Height);
            }
        }

        private void InitializeTransport()
        {
            SetupButton(previous, -1, "上一首");
            SetupButton(transport, 0, "播放 Spotify");
            SetupButton(nextSong, 1, "下一首");
        }

        private void SetupButton(Button button, int direction, string label)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.BackColor = Color.Transparent;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.Cursor = Cursors.Hand;
            button.AccessibleRole = AccessibleRole.PushButton;
            button.AccessibleName = label;
            transportTip.SetToolTip(button, label);
            button.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int x = button.Width / 2, y = button.Height / 2;
                bool hot = button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
                if (View.Glass && hot && button.Capture && MouseButtons == MouseButtons.Left && button.Enabled)
                    y += Math.Max(1, Px(1));
                using (var ring = new SolidBrush(Color.FromArgb(hot && button.Enabled ? 45 : 22, Ink)))
                using (var glyph = new SolidBrush(Color.FromArgb(button.Enabled ? 255 : 110, Ink)))
                {
                    int radius = Px(direction == 0 ? 17 : 13);
                    if (direction == 0 || hot) g.FillEllipse(ring, x - radius, y - radius, radius * 2, radius * 2);
                    if (direction != 0)
                    {
                        int tip = x + direction * Px(4), tail = x - direction * Px(6);
                        g.FillPolygon(glyph, new[] { new Point(tail, y - Px(6)), new Point(tip, y), new Point(tail, y + Px(6)) });
                        g.FillRectangle(glyph, direction > 0 ? tip : tip - Px(2), y - Px(6), Px(2), Px(12));
                    }
                    else if (track != null && track.Playing)
                    {
                        g.FillRectangle(glyph, x - Px(6), y - Px(7), Px(4), Px(14));
                        g.FillRectangle(glyph, x + Px(2), y - Px(7), Px(4), Px(14));
                    }
                    else
                        g.FillPolygon(glyph, new[] { new Point(x - Px(4), y - Px(8)),
                            new Point(x + Px(8), y), new Point(x - Px(4), y + Px(8)) });
                }
            };
            button.Click += (s, e) => ChangePlayback(direction);
            Controls.Add(button);
        }

        private void RefreshTransport()
        {
            bool playing = track != null && track.Playing;
            if (expectedPlaying.HasValue && (track == null || expectedPlaying == playing ||
                clock.ElapsedMilliseconds >= confirmUntil)) expectedPlaying = null;
            bool enabled = track != null && !sendingControl && !expectedPlaying.HasValue;
            string label = playing ? "暂停 Spotify" : "播放 Spotify";
            if (transport.AccessibleName != label)
            {
                transport.AccessibleName = label;
                transportTip.SetToolTip(transport, label);
                transport.Invalidate();
            }
            previous.Enabled = transport.Enabled = nextSong.Enabled = enabled;
            transportMenu.Text = label;
            transportMenu.Enabled = enabled;
        }

        private async void ChangePlayback(int direction = 0)
        {
            if (track == null || sendingControl || expectedPlaying.HasValue) return;
            bool play = !track.Playing;
            sendingControl = true;
            RefreshTransport();
            bool accepted;
            try { accepted = await Task.Run(() => direction == 0 ? BackgroundPlayback.SetPlaying(play) : BackgroundPlayback.Skip(direction > 0)); }
            catch (Exception) { accepted = false; }
            if (closed) return;
            sendingControl = false;
            if (accepted && direction == 0)
            {
                expectedPlaying = play;
                confirmUntil = clock.ElapsedMilliseconds + 2500;
            }
            else if (!accepted) tray.ShowBalloonTip(2500, "Spotify 播放控制", "Spotify 未接受操作，请确认已打开播放器并选择了播放设备。", ToolTipIcon.Info);
            nextRead = 0;
            RefreshTransport();
        }

        private void RefreshCoverOptions()
        {
            if (!WantsCover) ResetCover();
            else if (recording != null && coverImage == null && coverLookup == null) FetchCover(recording);
            ApplyAppearance(false);
        }
    }
}
