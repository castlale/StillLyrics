using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed partial class Island : Form
    {
        private readonly BackgroundPlayback reader = new BackgroundPlayback();
        private readonly SpotifyLiveLyrics spotifyLyrics = new SpotifyLiveLyrics();
        private SpotifyVerse liveVerse;
        private Line liveLine, liveNext;
        private bool probingSpotify, fallbackStarted;
        private long nextProbe, fallbackAfter, lastLiveRead;
        private double livePosition;
        private readonly List<KeyValuePair<Playback, CatalogResult>> cache = new List<KeyValuePair<Playback, CatalogResult>>();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PlaybackClock playbackClock = new PlaybackClock();
        private readonly LineTransition lineTransition = new LineTransition();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Library library;
        private readonly string settingsPath;
        private Preferences View
        {
            get
            {
                return library.View;
            }
        }

        private Font captionFont, lyricFont;
        private CancellationTokenSource lookup;
        private CancellationTokenSource coverLookup;
        private Color? coverColor;
        private Bitmap coverImage;
        private bool WantsCover { get { return View.ShowCover || View.CoverColors; } }
        private long nextCoverRead;
        private bool HasCover { get { return View.CoverColors && coverColor.HasValue; } }
        internal Color Ink { get { return HasCover || PaletteAnimating ? CoverColors.Ink(BackColor) : Color.FromArgb(View.Foreground); } }
        private Playback track, recording;
        private Lyrics lyrics;
        private Edition edition;
        private TrackChoice choice;
        private bool reading, closed, dragging, moving, dialogOpen;
        private int misses;
        private long nextRead;
        private Point dragOffset;
        private string message = "等待 Spotify 播放";
        private string lastLine, lastInfo, lastSecondary;
        private int lit;
        private bool previewTranslation;
        private bool animationsAllowed;
        private readonly float dpiScale;
        protected override bool ShowWithoutActivation
        {
            get
            {
                return true;
            }
        }

        internal Island() : this(Library.Load(Library.FilePath), Library.FilePath)
        {
        }

        internal Island(Library library, string path)
        {
            this.library = library;
            settingsPath = path;
            Text = "定格词岛";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            using (var g = CreateGraphics())
                dpiScale = g.DpiX / 96f;
            ContextMenuStrip = menu;
            InitializeTransport();
            BuildMenu();
            tray.Icon = MakeTrayIcon();
            tray.Text = "定格词岛 · 右键设置";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => menu.Show(Cursor.Position);
            ApplyAppearance(true);
            timer.Interval = 80;
            animationsAllowed = ClientAnimationsEnabled();
            timer.Tick += (s, e) => Tick();
            Shown += (s, e) =>
            {
                timer.Start();
                Tick();
            };
        }

        private void Save()
        {
            try
            {
                library.Save(settingsPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置本次已应用，保存失败。\n" + ex.Message, "定格词岛");
            }
        }

        private int Px(int n)
        {
            return (int)Math.Round(n * dpiScale * View.Zoom / 100.0);
        }

        private async void Tick()
        {
            TickWeChat();
            RefreshVisibility(Cursor.Position);
            // Reuse the current frosted image while animating; resampling a newly shown window can stall the fade.
            if (!notice.Active && fadeDuration == 0 && !PaletteAnimating) UpdateBackdrop(clock.ElapsedMilliseconds);
            UpdateText();
            if (reading || clock.ElapsedMilliseconds < nextRead)
                return;
            reading = true;
            try
            {
                var latest = await Task.Run(() => reader.Read());
                if (closed)
                    return;
                if (latest == null)
                {
                    if (++misses >= 2)
                        SetIdle();
                }
                else
                {
                    misses = 0;
                    playbackClock.Observe(latest, clock.ElapsedMilliseconds);
                    track = latest;
                    if (!latest.SameRecording(recording))
                    {
                        recording = latest;
                        choice = library.Choice(latest, false);
                        ResetCover(View.Glass && View.CoverColors);
                        CancelLookup();
                        ClearLive();
                        nextProbe = 0;
                        fallbackStarted = false;
                        fallbackAfter = clock.ElapsedMilliseconds + 900;
                        message = "正在读取 Spotify 歌词…";
                        Use(null);
                        if (choice != null && choice.Pinned != null) LoadTrack(false);
                    }
                    CheckLyricsSource();
                    if (WantsCover && coverImage == null && coverLookup == null &&
                        clock.ElapsedMilliseconds >= nextCoverRead) FetchCover(recording);
                }
            }
            catch (Exception)
            {
                if (!closed)
                    SetIdle();
            }
            finally
            {
                nextRead = clock.ElapsedMilliseconds + (track == null ? 1800 : 650);
                reading = false;
                if (!closed)
                    UpdateText();
            }
        }

        private void CancelLookup()
        {
            if (lookup != null)
            {
                lookup.Cancel();
                lookup = null;
            }
        }

        private void SetIdle()
        {
            ClearLive();
            if (recording != null) ResetCover();
            CancelLookup();
            track = recording = null;
            choice = null;
            lyrics = null;
            edition = null;
            playbackClock.Clear();
            message = "等待 Spotify 播放";
        }

        private void ResetCover(bool holdPalette = false)
        {
            if (coverLookup != null) { coverLookup.Cancel(); coverLookup = null; }
            nextCoverRead = 0;
            if (coverImage != null) { coverImage.Dispose(); coverImage = null; }
            if (!holdPalette) SetCoverPalette(null);
            Invalidate();
        }

        internal void SetCoverPalette(Color? color)
        {
            coverColor = color;
            SetPalette(true);
        }

        private async void FetchCover(Playback item)
        {
            var work = new CancellationTokenSource();
            coverLookup = work;
            try
            {
                var image = await Task.Run(() => BackgroundPlayback.ReadCover(item, work.Token));
                if (closed || work.IsCancellationRequested || recording != item || !WantsCover)
                {
                    if (image != null) image.Dispose();
                    return;
                }
                coverImage = image;
                SetCoverPalette(image == null ? (Color?)null : CoverColors.Dominant(image));
            }
            catch (Exception)
            {
                if (!closed && !work.IsCancellationRequested && recording == item) SetCoverPalette(null);
            }
            finally
            {
                if (coverLookup == work) { coverLookup = null; nextCoverRead = clock.ElapsedMilliseconds + 12000; }
                work.Dispose();
            }
        }

        private void Use(Edition version)
        {
            edition = version;
            lyrics = version == null ? null : version.Decode();
            UpdateText();
        }

        private void ClearLive()
        {
            liveVerse = null;
            liveLine = liveNext = null;
        }

        private void CheckLyricsSource()
        {
            if (choice != null && choice.Pinned != null) return;
            long now = clock.ElapsedMilliseconds;
            if (liveLine != null && now - lastLiveRead > 3500) liveLine = liveNext = null;
            if (!probingSpotify && now >= nextProbe) ProbeSpotify(recording, track);
            if (liveLine == null && !fallbackStarted && now >= fallbackAfter) LoadTrack(false);
        }

        private async void ProbeSpotify(Playback item, Playback latest)
        {
            probingSpotify = true;
            try
            {
                var verse = await Task.Run(() => spotifyLyrics.Read(latest));
                if (closed || recording != item || (choice != null && choice.Pinned != null)) return;
                long now = clock.ElapsedMilliseconds;
                double position = playbackClock.Position(now);
                bool changed = verse != null && (liveVerse == null || liveVerse.Index != verse.Index || liveVerse.Text != verse.Text);
                if (changed) livePosition = position;
                if (verse != null && Math.Abs(position - livePosition) < 20)
                {
                    if (liveLine == null) { CancelLookup(); fallbackStarted = false; }
                    if (changed || liveLine == null)
                    {
                        liveLine = liveNext != null && liveVerse.Index + 1 == verse.Index && liveNext.Text == verse.Text
                            ? liveNext : new Line { Text = verse.Text };
                        liveNext = string.IsNullOrWhiteSpace(verse.Next) ? null : new Line { Text = verse.Next };
                    }
                    liveVerse = verse;
                    lastLiveRead = now;
                    nextProbe = now + 250;
                }
                else
                {
                    // A minimized client can freeze its active marker while the phone keeps playing.
                    liveLine = liveNext = null;
                    nextProbe = now + 4000;
                    if (!fallbackStarted) LoadTrack(false);
                }
            }
            catch (Exception)
            {
                if (!closed && recording == item && (choice == null || choice.Pinned == null))
                {
                    ClearLive();
                    nextProbe = clock.ElapsedMilliseconds + 4000;
                    if (!fallbackStarted) LoadTrack(false);
                }
            }
            finally { probingSpotify = false; if (!closed) UpdateText(); }
        }

        private async void LoadTrack(bool refresh)
        {
            CancelLookup();
            if (recording == null)
                return;
            if (choice != null && choice.Pinned != null)
            {
                Use(choice.Pinned);
                return;
            }
            if (liveLine != null) return;
            fallbackStarted = true;

            var item = recording;
            var stored = cache.FirstOrDefault(x => item.SameRecording(x.Key));
            if (!refresh && stored.Value != null)
            {
                ApplyResult(stored.Value);
                return;
            }

            message = "正在匹配同步歌词…";
            Use(null);
            var work = new CancellationTokenSource();
            lookup = work;
            try
            {
                var found = await Catalog.Find(item, item.Title, false, View.Karaoke, View.Translation, work.Token);
                if (work.IsCancellationRequested || closed)
                    return;
                cache.RemoveAll(x => item.SameRecording(x.Key));
                if (found.Instrumental || found.Items.Count > 0)
                {
                    if (cache.Count >= 32)
                        cache.RemoveAt(0);
                    cache.Add(new KeyValuePair<Playback, CatalogResult>(item, found));
                }

                ApplyResult(found);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                if (!closed && !work.IsCancellationRequested)
                {
                    message = "词库暂不可用 · 托盘右键可重试";
                    Use(null);
                }
            }
            finally
            {
                if (lookup == work)
                    lookup = null;
                work.Dispose();
            }
        }

        private void ApplyResult(CatalogResult found)
        {
            message = found.Instrumental ? "纯音乐" : found.Failures > 0 ? "部分词库暂不可用 · 可重试" : "网易云和 LRCLIB 暂未匹配到同步歌词";
            Use(found.Items.FirstOrDefault());
        }

        private void UpdateText()
        {
            RefreshTransport();
            string line = message, sub = "", info = "打开电脑版 Spotify，从手机或电脑播放";
            int highlight = 0;
            Line current = null, next = null;
            double position = 0;
            bool translation = false;
            if (track != null)
            {
                info = (track.Playing ? "" : "已暂停  ·  ") + track.Artist + "   /   " + track.Title;
                if (liveLine == null && edition != null && edition.Duration > 0 && Math.Abs(edition.Duration - track.Duration) > 2)
                    info += "   ·   近似版本";
                if (View.InfoOnly)
                {
                    info = track.Playing ? "正在播放" : "已暂停";
                    line = track.Title;
                    sub = track.Artist;
                }
                else if (liveLine != null)
                {
                    position = playbackClock.Position(clock.ElapsedMilliseconds);
                    current = liveLine;
                    next = liveNext;
                    line = current.Text;
                    translation = View.Translation && !string.IsNullOrWhiteSpace(liveVerse.Translation);
                    sub = translation ? liveVerse.Translation : View.TwoLines && next != null ? next.Text : "";
                }
                else if (lyrics != null)
                {
                    position = playbackClock.Position(clock.ElapsedMilliseconds) + (choice == null ? 0 : choice.OffsetMs) / 1000.0;
                    current = lyrics.CurrentAndNext(position, out next);
                    line = current == null || current.Text.Length == 0 ? "♪" : current.Text;
                    if (current != null && View.Karaoke)
                        highlight = current.Highlight(position);
                    translation = View.Translation && current != null && !string.IsNullOrWhiteSpace(current.Translation);
                    if (translation)
                        sub = current.Translation;
                    else if (View.TwoLines)
                        sub = next == null ? "" : next.Text;
                }
            }

            bool hadOutgoingLine = lineTransition.Outgoing != null;
            lineTransition.Observe(current, next, position, clock.ElapsedMilliseconds,
                Visible && track != null && track.Playing && View.AnimateLines && animationsAllowed &&
                !View.InfoOnly && (View.TwoLines || translation), translation, sub, highlight);
            UpdateAnimationClock();
            bool modeChanged = previewTranslation != translation;
            previewTranslation = translation;
            if (!modeChanged && hadOutgoingLine == (lineTransition.Outgoing != null) && line == lastLine && info == lastInfo && sub == lastSecondary && highlight == lit)
                return;
            bool newTitle = line != lastLine || info != lastInfo;
            lastLine = line;
            lastInfo = info;
            lastSecondary = sub;
            lit = highlight;
            if (newTitle)
            {
                AccessibleName = line + " · " + info;
                Text = "定格词岛 · " + AccessibleName;
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (notice.Active && !noticeExitStart.HasValue)
            {
                PaintNotice(g, NoticeAnimationAge());
                return;
            }
            using (var accent = new SolidBrush(HasCover ? Ink : Color.FromArgb(View.Accent)))
            using (var fg = new SolidBrush(Ink))
            using (var format = (StringFormat)StringFormat.GenericDefault.Clone())
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.LineAlignment = StringAlignment.Center;
                var column = TextColumn;
                format.Alignment = View.CenterText ? StringAlignment.Center : StringAlignment.Near;
                var header = new RectangleF(column.Left, Px(4), column.Width, Px(25));
                if (!View.CenterText)
                {
                    g.FillEllipse(accent, column.Left + Px(2), Px(14), Px(6), Px(6));
                    header.X += Px(14);
                    header.Width -= Px(14);
                }
                g.DrawString(lastInfo ?? "定格词岛", captionFont, fg, header, format);
                if (View.ShowCover && coverImage != null)
                    g.DrawImage(coverImage, CoverBounds);
                bool two = View.InfoOnly || View.TwoLines || previewTranslation || (View.Translation && liveLine == null && lyrics != null && lyrics.HasTranslation);
                var box = new RectangleF(column.Left, Px(28), column.Width, two ? Px(30) : Height - Px(33));
                string text = lastLine ?? message;
                if (!two)
                    format.FormatFlags &= ~StringFormatFlags.NoWrap;
                var second = new RectangleF(column.Left, Px(57), column.Width, Px(24));
                var clip = g.Save();
                g.SetClip(new RectangleF(column.Left, box.Top, column.Width, Height - box.Top), CombineMode.Intersect);
                PaintRows(g, format, box, second, text, two, clock.Elapsed.TotalMilliseconds);
                g.Restore(clip);
            }
            if (noticeExitStart.HasValue) PaintNotice(g, exitCardAge);
        }

        private void PaintRows(Graphics g, StringFormat format, RectangleF main, RectangleF second, string text, bool two, double now)
        {
            float p = animationsAllowed && lineTransition.Outgoing != null ? lineTransition.Progress(now) : 1f;
            float step = second.Y + second.Height / 2 - main.Y - main.Height / 2;
            float secondaryAlpha = HasCover ? 1f : 0.76f;
            bool promote = lineTransition.PromotePreview;
            if (p < 1 && lineTransition.Outgoing != null)
            {
                var old = main;
                old.Y -= (promote ? step : Px(8)) * p;
                DrawRow(g, format, old, lineTransition.Outgoing.Text, lyricFont, 1 - p, lineTransition.OldHighlight);
                if (!promote)
                {
                    var oldSub = second;
                    oldSub.Y -= Px(8) * p;
                    DrawRow(g, format, oldSub, lineTransition.OldSecondary, captionFont, secondaryAlpha * (1 - p), 0);
                }
                main.Y += (promote ? step : Px(8)) * (1 - p);
                second.Y += (promote ? step : Px(8)) * (1 - p);
            }
            DrawRow(g, format, main, text, lyricFont, promote ? secondaryAlpha + (1f - secondaryAlpha) * p : p, lit);
            if (two)
                DrawRow(g, format, second, lastSecondary, previewTranslation || View.InfoOnly ? captionFont : lyricFont, secondaryAlpha * p, 0);
        }

        private void DrawRow(Graphics g, StringFormat format, RectangleF box, string text, Font font, float alpha, int highlight)
        {
            if (string.IsNullOrEmpty(text) || alpha <= 0) return;
            int opacity = (int)Math.Round(255 * alpha);
            using (var brush = new SolidBrush(Color.FromArgb(opacity, Ink)))
                g.DrawString(text, font, brush, box, format);
            if (highlight <= 0) return;
            format.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, Math.Min(highlight, text.Length)) });
            using (var range = g.MeasureCharacterRanges(text, font, box, format)[0])
            using (var brush = new SolidBrush(Color.FromArgb(opacity, HasCover ? Ink : Color.FromArgb(View.Accent))))
            {
                var state = g.Save();
                g.SetClip(range, CombineMode.Intersect);
                if (HasCover)
                {
                    using (var underlined = new Font(font, font.Style | FontStyle.Underline))
                        g.DrawString(text, underlined, brush, box, format);
                }
                else g.DrawString(text, font, brush, box, format);
                g.Restore(state);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (notice.Active)
                {
                    if (openingWeChat || !OverNotice(e.Location)) return;
                    noticePressed = true;
                    Capture = true;
                    return;
                }
                dragging = true;
                dragOffset = e.Location;
                Capture = true;
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) noticePressed = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
                Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y);
            else if (Visible && View.HoverHide)
                RefreshVisibility(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (noticePressed && e.Button == MouseButtons.Left)
            {
                noticePressed = false;
                bool open = Capture && OverNotice(e.Location);
                Capture = false;
                if (open) OpenWeChat();
                return;
            }
            if (!dragging)
                return;
            dragging = false;
            moving = false;
            Capture = false;
            View.X = Left;
            View.Y = Top;
            ApplyAppearance(false);
            Save();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closed = true;
            ReleaseBackdrop();
            StopWeChat();
            if (coverLookup != null) coverLookup.Cancel();
            if (coverImage != null) { coverImage.Dispose(); coverImage = null; }
            transportTip.Dispose();
            ClearGlass();
            if (glassOutline != null) { glassOutline.Dispose(); glassOutline = null; }
            timer.Stop();
            CancelLookup();
            tray.Visible = false;
            var icon = tray.Icon;
            tray.Dispose();
            icon.Dispose();
            timer.Dispose();
            if (noticeFont != null) noticeFont.Dispose();
            lyricFont.Dispose();
            captionFont.Dispose();
            menu.Dispose();
            base.OnFormClosed(e);
        }
    }
}
