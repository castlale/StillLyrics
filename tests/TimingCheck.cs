using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace StillLyrics
{
    // Exercise the real playback and paint paths with a local fixture; no Spotify session is needed.
    internal sealed partial class Island
    {
        internal void MeasureAppearanceFrames()
        {
            nextRead = long.MaxValue;
            View.ShowTransport = View.DockTop = View.HoverHide = false;
            var samples = new[] { new List<double>(), new List<double>(), new List<double>() };
            int phase = -1;
            double previousValue = 0, previousTime = 0;
            EventHandler sample = (s, e) =>
            {
                if (phase < 0 || phase > 2) return;
                double value = phase == 0 ? BackColor.ToArgb() : Opacity;
                if (value == previousValue) return;
                double now = clock.Elapsed.TotalMilliseconds;
                if (previousTime > 0) samples[phase].Add(now - previousTime);
                previousTime = now;
                previousValue = value;
            };
            using (var step = new System.Windows.Forms.Timer { Interval = 480 })
            {
                step.Tick += (s, e) =>
                {
                    phase++;
                    previousTime = 0;
                    if (phase == 0) SetCoverPalette(Color.WhiteSmoke);
                    else if (phase == 1)
                    {
                        // Put the test form around the pointer without moving the user's mouse.
                        var cursor = Cursor.Position;
                        Location = new Point(cursor.X - Width / 2, cursor.Y - Height / 2);
                        View.HoverHide = true;
                        RefreshVisibility(cursor);
                    }
                    else if (phase == 2)
                    {
                        if (Visible) throw new InvalidOperationException("hover fade did not finish");
                        View.HoverHide = false;
                        RefreshVisibility(Cursor.Position);
                        SetCoverPalette(Color.Navy);
                    }
                    else
                    {
                        if (!Visible || Opacity < .99 || PaletteAnimating || BackColor != Color.Navy || animationClockRunning)
                            throw new InvalidOperationException("overlapping fade and palette did not settle");
                        step.Stop();
                        Close();
                    }
                };
                Application.Idle += sample;
                try { step.Start(); Application.Run(this); }
                finally { Application.Idle -= sample; }
            }
            ReportFrameIntervals("cover color", samples[0]);
            ReportFrameIntervals("fade out", samples[1]);
            ReportFrameIntervals("fade in + cover", samples[2]);
        }

        internal void MeasureLyricFrames()
        {
            nextRead = long.MaxValue;
            View.TwoLines = View.Translation = true;
            track = new Playback { Artist = "测试", Title = "双行换句", Duration = 15, Playing = true };
            var rows = Enumerable.Range(0, 7).Select(i => "[00:0" + i + ".00]第" + i + "句");
            var translations = Enumerable.Range(3, 4).Select(i => "[00:0" + i + ".00]译文" + i);
            lyrics = Lyrics.Parse(string.Join("\n", rows), string.Join("\n", translations), null);
            playbackClock.Observe(track, clock.ElapsedMilliseconds);
            var previewGaps = new List<double>();
            var translationGaps = new List<double>();
            Line previous = null;
            double lastPaint = 0;
            Paint += (s, e) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                var outgoing = lineTransition.Outgoing;
                if (!e.ClipRectangle.Contains(TextColumn.Left + TextColumn.Width / 2, Px(42))) return;
                if (outgoing == null || lineTransition.Progress(clock.ElapsedMilliseconds) >= 1) return;
                if (outgoing == previous)
                    (lineTransition.PromotePreview ? previewGaps : translationGaps).Add(now - lastPaint);
                previous = outgoing;
                lastPaint = now;
            };
            using (var deadline = new System.Windows.Forms.Timer { Interval = 6800 })
            {
                deadline.Tick += (s, e) => { deadline.Stop(); Close(); };
                deadline.Start();
                Application.Run(this);
            }
            ReportFrameIntervals("lyric preview", previewGaps);
            ReportFrameIntervals("lyric translation", translationGaps);
        }

        private static void ReportFrameIntervals(string mode, List<double> gaps)
        {
            if (gaps.Count < 3) throw new InvalidOperationException("Missing transition samples: " + mode);
            gaps.Sort();
            Console.WriteLine("{0}: {1} intervals, {2:F1} fps, p95 {3:F2} ms", mode, gaps.Count,
                1000 / gaps.Average(), gaps[(int)Math.Ceiling(gaps.Count * .95) - 1]);
        }

        internal void MeasureNoticeFrames()
        {
            if (!animationsAllowed || SystemInformation.HighContrast)
                throw new InvalidOperationException("Frame measurement needs Windows animations enabled.");
            var times = new List<double>();
            var exitTimes = new List<double>();
            var watch = new Stopwatch();
            Paint += (s, e) =>
            {
                if (watch.IsRunning && notice.Active && watch.ElapsedMilliseconds < NoticeMotionMs)
                    times.Add(watch.Elapsed.TotalMilliseconds);
                if (noticeExitStart.HasValue) exitTimes.Add(clock.Elapsed.TotalMilliseconds);
            };
            using (var step = new System.Windows.Forms.Timer { Interval = 350 })
            {
                int runs = 0;
                step.Tick += (s, e) =>
                {
                    if (exitTimes.Count > 0)
                    {
                        if (notice.Active) throw new InvalidOperationException("exit did not finish on the animation clock");
                        ReportFrameIntervals("notice exit", exitTimes.Skip(1).Select((t,i) => t - exitTimes[i]).ToList());
                        exitTimes.Clear();
                        if (runs == 3) { step.Stop(); Close(); return; }
                    }
                    if (watch.IsRunning && watch.ElapsedMilliseconds < NoticeMotionMs) return;
                    if (watch.IsRunning)
                    {
                        watch.Stop();
                        if (times.Count < 2) throw new InvalidOperationException("No active notice paint samples.");
                        var gaps = times.Skip(1).Select((t, i) => t - times[i]).OrderBy(t => t).ToArray();
                        Console.WriteLine("run {0}: {1} paints, {2:F1} fps, p95 interval {3:F2} ms", ++runs, times.Count,
                            1000 / gaps.Average(), gaps[(int)Math.Ceiling(gaps.Length * .95) - 1]);
                        BeginNoticeExit();
                        if (!noticeExitStart.HasValue) throw new InvalidOperationException("exit did not start");
                        exitTimes.Add(clock.Elapsed.TotalMilliseconds);
                        return;
                    }
                    times.Clear();
                    watch.Restart();
                    PresentNotice(null, null, true, true);
                };
                step.Start();
                Application.Run(this);
            }
        }

        internal void CheckLyricsDuringWeChat()
        {
            track = new Playback { Artist = "Artist", Title = "Song", Duration = 30, Playing = true, Position = 2 };
            lyrics = Lyrics.Parse("[00:01.00]Before\n[00:07.00]After");
            playbackClock.Observe(track, clock.ElapsedMilliseconds);
            UpdateText();
            if (lastLine != "Before") throw new InvalidOperationException("fixture starts on first lyric");
            PresentNotice(null, null, false, true);
            CheckNoticeFrames();
            BeginNoticeExit();
            track.Position = 10;
            playbackClock.Observe(track, clock.ElapsedMilliseconds);
            UpdateText();
            if (lastLine != "After") throw new InvalidOperationException("playback advances during exit");
            if (noticeExitStart.HasValue)
            {
                exitCardAge = NoticeMotionMs;
                if (NoticeExitEdge(0) != Width || NoticeExitEdge(110) != Width / 2f || NoticeExitEdge(220) != 0)
                    throw new InvalidOperationException("green retracts from right to left over 220 milliseconds");
                PresentNotice(null, null, false, true);
                if (noticeExitStart.HasValue || !notice.Active) throw new InvalidOperationException("new notice supersedes exit");
            }
            ClearNotice();
            if (notice.Active || lastLine != "After") throw new InvalidOperationException("dismissal reveals current lyric");
        }

        private void CheckNoticeFrames()
        {
            using (var frame = new Bitmap(Width, Height))
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Magenta);
                int x = Width * 3 / 4, y = Height / 2;
                PaintNotice(g, 0);
                if (frame.GetPixel(x, y).ToArgb() != Color.Magenta.ToArgb())
                    throw new InvalidOperationException("notice entrance starts on the left");
                PaintNotice(g, NoticeMotionMs);
                if (frame.GetPixel(x, y).ToArgb() == Color.Magenta.ToArgb())
                    throw new InvalidOperationException("resting card covers the island");
                bool saved = animationsAllowed;
                animationsAllowed = false;
                try
                {
                    UpdateAnimationClock();
                    if (NoticeAnimationAge() != NoticeMotionMs || animationClockRunning)
                        throw new InvalidOperationException("reduced motion shows a static card");
                }
                finally { animationsAllowed = saved; }
            }
        }
    }

    internal static class TimingCheck
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        private static Playback Track(int pos, bool playing)
        {
            return new Playback
            {
                Title = "Test",
                Artist = "Artist",
                LeadArtist = "Artist",
                Duration = 60,
                Position = pos,
                Playing = playing
            };
        }

        private static void Check(bool pass, string label)
        {
            if (!pass)
            {
                Console.Error.WriteLine("Failed: " + label);
                throw new InvalidOperationException(label);
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try { RunChecks(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Environment.ExitCode = 1;
            }
        }

        private static void RunChecks(string[] args)
        {
            if (args.Contains("--frames"))
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                var frameSettings = new Library();
                frameSettings.View.HoverHide = frameSettings.View.HideFullscreen = false;
                using (var island = new Island(frameSettings, Path.Combine(Path.GetTempPath(), "StillLyricsFrameCheck.json")))
                    island.MeasureNoticeFrames();
                using (var island = new Island(frameSettings, Path.Combine(Path.GetTempPath(), "StillLyricsFrameCheck.json")))
                    island.MeasureLyricFrames();
                using (var island = new Island(frameSettings, Path.Combine(Path.GetTempPath(), "StillLyricsFrameCheck.json")))
                    island.MeasureAppearanceFrames();
            }
            CheckAppearance();
            CheckWeChat();
            var metadata = Track(14, true);
            Check(SpotifyLiveLyrics.MatchesCredits(new[] { metadata.Title, metadata.LeadArtist }, metadata)
                && !SpotifyLiveLyrics.MatchesCredits(new[] { "previous song", metadata.LeadArtist }, metadata), "Spotify panel must belong to the current recording");
            Check(SpotifyLiveLyrics.IsCurrentRow("J4_kkGYm5hjbZlztykxr SOT5gG00YqnGQ4uR7PwQ")
                && !SpotifyLiveLyrics.IsCurrentRow("J4_kkGYm5hjbZlztykxr y45YX8smwP4lW13IQulz"), "Spotify active and inactive row distinction");
            var clock = new PlaybackClock();
            clock.Observe(Track(12, true), 1000);
            clock.Observe(Track(12, true), 6000);
            Check(clock.Position(7000) == 18, "stale Connect reading");
            clock.Observe(Track(21, false), 8000);
            Check(clock.Position(16000) == 21, "pause");
            clock.Observe(Track(6, true), 17000);
            Check(clock.Position(18000) == 7, "seek and resume");
            Check(clock.Position(100000) == 60, "track end clamp");
            var lyrics = Lyrics.Parse("[offset:100]\n[00:02.50][00:10.0]First\n[00:04.20]\n[00:06.000]Second");
            Check(lyrics.At(1) == "", "intro");
            Check(lyrics.At(2.45) == "First", "offset");
            Check(lyrics.At(5) == "", "instrumental gap");
            Check(lyrics.At(6) == "Second" && lyrics.At(11) == "First", "seek and repeated tag");
            var shifted = Track(12, true);
            shifted.Duration += 1;
            Check(shifted.SameRecording(Track(12, true)), "duration jitter");
            shifted.Duration += 40;
            Check(!shifted.SameRecording(Track(12, true)), "different recording length");
            var enhanced = Lyrics.Parse("[offset:100]\n[00:02.00]<00:02.00>你<00:02.60>好\n[00:03.00]再见", "[00:01.90]hello", null);
            string next;
            var line = enhanced.Current(2.3, out next);
            Check(next == "再见" && line.Translation == "hello" && line.Highlight(2.3) == 1 && line.Highlight(2.51) == 2, "word timing, translation, next line");
            var yrc = Lyrics.Parse("", null, "[1000,1000](1000,500,0)长(1500,500,0)句");
            Check(yrc.Current(1.2, out next).Highlight(1.2) == 1 && yrc.At(2) == "长句", "imported YRC");
            Check(!lyrics.HasWords, "line-only lyrics have no fabricated word timings");
            var sample = new Playback
            {
                Title = "一点",
                Artist = "Muyoi、Pezzi",
                LeadArtist = "Muyoi",
                Duration = 195
            };
            var editions = new[]
            {
                new Edition
                {
                    Artist = "Muyoi、Pezzi",
                    Title = "一点",
                    Duration = 200.655
                },
                new Edition
                {
                    Artist = "Muyoi & Pezzi",
                    Title = "一点",
                    Duration = 199
                },
                new Edition
                {
                    Artist = "Muyoi",
                    Title = "一点 (Live)",
                    Duration = 195
                }
            };
            var ranked = Catalog.Rank(editions, sample, sample.Title).ToList();
            Check(ranked.Count == 2 && ranked[0].Duration == 199, "both sources compared and live version excluded");
            var theme = new Playback { Title = "心湖 (《影之刃零》主题曲)", Artist = "張靚穎", LeadArtist = "張靚穎", Duration = 274 };
            var themeEdition = new Edition { Title = "心湖", Artist = "张靓颖", Aliases = new[] { "《影之刃零》主题曲" }, Duration = 279.05 };
            Check(Catalog.Rank(new[] { themeEdition }, theme, theme.Title).Any(), "catalog alias and simplified artist");
            Check(!Catalog.Rank(new[] { themeEdition }, theme, "心湖 (Live)").Any(), "unverified suffix still rejected");
            var duet = new Playback { Title = "說好不哭", Artist = "周杰倫、五月天 阿信", LeadArtist = "周杰倫", Duration = 216 };
            var duetEdition = new Edition { Title = "说好不哭 (with 五月天阿信)", Artist = "周杰伦", Duration = 217 };
            Check(Catalog.Rank(new[] { duetEdition }, duet, duet.Title).Any(), "guest credited in title");
            duetEdition.Title = "说好不哭 (with 其他歌手)";
            Check(!Catalog.Rank(new[] { duetEdition }, duet, duet.Title).Any(), "mismatched guest excluded");
            var cancelSong = new Playback { Title = "第57次取消发送", Artist = "菲菲公主", LeadArtist = "菲菲公主", Duration = 175 };
            var original = new Edition { Title = cancelSong.Title, Artist = "菲菲公主（陆绮菲）", Duration = 180.56 };
            var alternatives = new[] {
                new Edition { Title = cancelSong.Title + "（伴奏）", Artist = original.Artist, Duration = 176.5 },
                new Edition { Title = cancelSong.Title + "（加速版）", Artist = original.Artist, Duration = 155.62 },
                new Edition { Title = cancelSong.Title, Artist = "吃一口橙子", Duration = 175.306 },
                new Edition { Title = cancelSong.Title, Artist = "菲菲公主（另一位）", Duration = 175 },
                original
            };
            foreach (int reportedDuration in new[] { 175, 176 })
            {
                cancelSong.Duration = reportedDuration;
                Check(Catalog.Rank(alternatives, cancelSong, cancelSong.Title).SequenceEqual(new[] { original }), "verified artist credit selects the vocal original");
            }
            var lib = new Library();
            string path = Path.Combine(Path.GetTempPath(), "StillLyrics-check-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var saved = lib.Choice(sample, true);
                saved.OffsetMs = 250;
                saved.Pinned = new Edition
                {
                    Source = "本地",
                    Id = "sample.lrc",
                    Lrc = "[00:01]sample"
                };
                lib.View.Alpha = 60;
                lib.Save(path);
                lib.View.Alpha = 80;
                lib.View.CenterText = lib.View.ShowCover = lib.View.ShowTransport = false;
                lib.Save(path);
                var reopened = Library.Load(path);
                sample.Duration++;
                Check(reopened.View.Alpha == 80 && reopened.Choice(sample, false).Pinned.Decode().At(1) == "sample" && reopened.Choice(sample, false).OffsetMs == 250, "persistent pinned LRC, offset, atomic settings replace");
                Check(!reopened.View.CenterText && !reopened.View.ShowCover && !reopened.View.ShowTransport, "layout choices survive restart");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

            if (args.Contains("--window"))
            {
                using (var chat = new Form { TopMost = true, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(180, 240, 340, 260) })
                using (var cover = new Form { TopMost = true, StartPosition = FormStartPosition.Manual, Bounds = chat.Bounds })
                {
                    chat.Show();
                    Check(WeChatWindow.IsExposedOnDesktop(chat.Handle), "exposed chat area");
                    cover.Show();
                    Check(!WeChatWindow.IsExposedOnDesktop(chat.Handle), "fully obscured chat");
                    cover.Left += 120;
                    Check(WeChatWindow.IsExposedOnDesktop(chat.Handle), "visible chat beside another active window");
                    chat.WindowState = FormWindowState.Minimized;
                    Check(!WeChatWindow.IsExposedOnDesktop(chat.Handle), "minimized chat");
                    chat.Hide();
                    Check(!WeChatWindow.IsExposedOnDesktop(chat.Handle), "hidden chat");
                }
                var settings = new Library();
                settings.View.DockTop = false;
                settings.View.HideFullscreen = false;
                settings.View.X = 120;
                settings.View.Y = 120;
                using (var island = new Island(settings, path))
                {
                    island.Show();
                    var bounds = island.Bounds;
                    var hover = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    var outside = new Point(bounds.Right + 5, bounds.Top);
                    var buttons = island.Controls.OfType<Button>().OrderBy(b => b.Left).ToArray();
                    Check(buttons.Length == 3 && buttons[0].AccessibleName == "上一首" && buttons[2].AccessibleName == "下一首", "ordered previous, play and next controls");
                    var control = buttons[1];
                    var buttonPoint = island.PointToScreen(new Point(control.Left + control.Width / 2, control.Top + control.Height / 2));
                    var column = island.TextColumn;
                    Check(buttons[2].Right < column.Left && column.Left == island.Width - column.Right, "lyrics centered with separate transport and cover slots");
                    island.RefreshVisibility(outside, 500);
                    island.RefreshVisibility(outside, 800);
                    island.RefreshVisibility(hover, 900);
                    island.RefreshVisibility(hover, 1010);
                    Check(island.Visible && island.Opacity > 0 && island.Opacity < 1, "hover fades through intermediate frames");
                    Check(WindowFromPoint(hover) != island.Handle, "underlying window receives hit during fade");
                    double middle = island.Opacity;
                    island.RefreshVisibility(outside, 1010);
                    Check(Math.Abs(island.Opacity - middle) < 0.01, "reversal has no jump");
                    island.RefreshVisibility(outside, 1300);
                    Check(island.Opacity > 0.99, "quick leave restores full alpha");
                    island.RefreshVisibility(hover, 1350);
                    island.RefreshVisibility(hover, 1600);
                    Check(!island.Visible, "fade-out completes");
                    island.RefreshVisibility(hover, 1700);
                    Check(!island.Visible, "hover remains hidden");
                    island.RefreshVisibility(buttonPoint, 1800);
                    island.RefreshVisibility(buttonPoint, 1890);
                    Check(island.Visible && island.Opacity > 0 && island.Opacity < 1, "visible fade-in");
                    island.RefreshVisibility(buttonPoint, 2000);
                    Check(island.Visible && island.Opacity > 0.99 && island.Bounds == bounds, "transport stays reachable with hover hiding enabled");
                    var look = island.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "词岛外观");
                    var glass = look.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "毛玻璃 · 透出后方窗口");
                    using (var frosted = new Bitmap(island.Width, island.Height))
                    using (var plain = new Bitmap(island.Width, island.Height))
                    {
                        island.DrawToBitmap(frosted, island.ClientRectangle);
                        glass.PerformClick();
                        island.DrawToBitmap(plain, island.ClientRectangle);
                        int x = island.Width / 2;
                        Check(SystemInformation.HighContrast || frosted.GetPixel(x, 2) != plain.GetPixel(x, 2), "glass highlights differ from flat palette");
                        glass.PerformClick();
                    }
                    var startColor = island.BackColor;
                    island.SetCoverPalette(Color.WhiteSmoke);
                    if (island.PaletteAnimating)
                    {
                        Check(island.BackColor == startColor, "new cover begins at the previous palette");
                        island.AdvancePalette(175);
                        Check(island.BackColor != startColor && island.BackColor != Color.WhiteSmoke, "intermediate palette frame");
                        Check(island.Ink == CoverColors.Ink(island.BackColor), "ink follows the displayed color");
                        var intermediate = island.BackColor;
                        island.SetCoverPalette(Color.Navy);
                        Check(island.BackColor == intermediate, "rapid cover switch retains current frame");
                        island.AdvancePalette(350);
                        Check(island.BackColor == Color.Navy && !island.PaletteAnimating, "palette timer stops at destination");
                    }
                    else Check(island.BackColor == Color.WhiteSmoke, "reduced motion immediately applies cover color");
                    Check(island.Bounds == bounds, "palette changes preserve island geometry");
                    island.SetCoverPalette(null);
                    island.AdvancePalette(350);
                    foreach (var skip in new[] { buttons[0], buttons[2] })
                    {
                        var hot = island.PointToScreen(new Point(skip.Left + skip.Width / 2, skip.Top + skip.Height / 2));
                        island.RefreshVisibility(hot, 2010);
                        island.RefreshVisibility(hot, 2300);
                        Check(island.Visible && island.Opacity > 0.99, "skip controls stay reachable when hover hiding is on");
                    }
                    var art = look.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "右侧歌曲封面");
                    art.PerformClick();
                    Check(island.TextColumn.Left == island.Width - island.TextColumn.Right, "single control preserves optical center");
                    var display = island.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "歌词显示");
                    display.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "歌词居中显示").PerformClick();
                    Check(island.TextColumn.Left > island.Width - island.TextColumn.Right, "left aligned text reclaims empty cover slot");
                    var behavior = island.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "词岛行为");
                    behavior.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "恢复到主屏顶部").PerformClick();
                    var screen = Screen.PrimaryScreen;
                    Check(island.Top >= screen.WorkingArea.Top && (screen.WorkingArea.Top == screen.Bounds.Top || island.Top > screen.WorkingArea.Top), "reset clears top taskbar");
                    settings.View.HidePaused = true;
                    island.RefreshVisibility(Point.Empty);
                    Check(!island.Visible, "idle hiding");
                    settings.View.HoverHide = false;
                    using (var editor = new Form())
                    using (var input = new TextBox())
                    {
                        editor.Controls.Add(input);
                        editor.Show();
                        editor.Activate();
                        input.Focus();
                        IntPtr foreground = GetForegroundWindow();
                        Check(input.Focused, "editor owns the test thread's input focus before notification");
                        island.PresentNotice("private sender", "private text", false, true);
                        Check(island.Visible, "notification reveals idle island");
                        Check(GetForegroundWindow() == foreground && input.Focused,
                            "notification preserves the editor input focus");
                    }
                    Check(island.AccessibleDescription == "微信 · 微信有新消息" && buttons.All(b => !b.Visible), "private notice replaces the island controls");
                    settings.View.WeChatPreview = true;
                    island.PresentNotice("Amy", "Latest", false, true);
                    Check(island.AccessibleDescription == "微信 · Amy · Latest", "burst updates existing island");
                    island.ClearNotice();
                    island.RefreshVisibility(Point.Empty);
                    Check(!island.Visible && island.AccessibleDescription == null, "dismissal restores idle hiding and clears private text");
                    island.CheckLyricsDuringWeChat();
                    island.Close();
                }
                if (File.Exists(path)) File.Delete(path);
                using (var picker = new VersionPicker(sample, true, true))
                {
                    Check(picker.ActiveControl is TextBox, "keyboard starts in the song search field");
                    Check(picker.AcceptButton is Button && picker.CancelButton is Button, "Enter and Escape actions");
                    var list = picker.Controls[0].Controls.OfType<ListBox>().Single();
                    Check(list.TabStop && !string.IsNullOrEmpty(list.AccessibleName), "keyboard-accessible version list");
                }
            }

            if (args.Contains("--frost")) CheckFrostedWindow(path);

            if (args.Contains("--online"))
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                sample.Duration = 195;
                var found = Catalog.Find(sample, sample.Title, true, true, true, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var name in new[]
                {
                    "LRCLIB",
                    "网易云"
                }

                )
                {
                    var e = found.Items.FirstOrDefault(x => x.Source == name);
                    Check(e != null && e.Decode().Count > 0, name + " live timed lyrics");
                    Console.WriteLine(name + " id=" + e.Id + " seconds=" + e.Duration + " lines=" + e.Decode().Count);
                }

                Check(found.Items.Any(x => x.Source.Contains("AMLL") && x.Decode().HasWords), "live authored word timing");
                var cancelResult = Catalog.Find(cancelSong, cancelSong.Title, false, true, true, CancellationToken.None).GetAwaiter().GetResult();
                var vocal = cancelResult.Items.FirstOrDefault();
                Check(vocal != null && vocal.Id == "2692390754" && vocal.Decode().At(112).Length > 0, "live original has a synced line at reported playback position");
                Console.WriteLine("第57次取消发送 id=" + vocal.Id + " lines=" + vocal.Decode().Count + " source=" + vocal.Source);
                var themeResult = Catalog.Find(theme, theme.Title, false, true, true, CancellationToken.None).GetAwaiter().GetResult();
                var matched = themeResult.Items.FirstOrDefault(x => x.Id == "3455169579" && x.Decode().Count > 0);
                Check(matched != null, "live 心湖 soundtrack match");
                Console.WriteLine("心湖 timed lines=" + matched.Decode().Count + " source=" + matched.Source + " duration=" + matched.Duration);
                var duetResult = Catalog.Find(duet, duet.Title, false, true, true, CancellationToken.None).GetAwaiter().GetResult();
                var duetMatch = duetResult.Items.FirstOrDefault();
                Check(duetMatch != null && duetMatch.Decode().Count > 0 && duetMatch.Decode().At(60).Length > 0, "live Spotify duet lookup and timed line");
                Console.WriteLine("說好不哭 id=" + duetMatch.Id + " lines=" + duetMatch.Decode().Count + " source=" + duetMatch.Source + " seconds=" + duetMatch.Duration);
            }

            Console.WriteLine("All requested checks passed.");
        }

        private static void CheckWeChat()
        {
            var notice = new WeChatNotice();
            Check(new Preferences().WeChatAlerts && !new Preferences().WeChatPreview, "private defaults");
            Check(WeChatNotice.MatchesApp("微信", null) && WeChatNotice.MatchesApp("WEIXIN", null)
                && WeChatNotice.MatchesApp("", @"{folder}\Tencent\Weixin\Weixin.exe")
                && !WeChatNotice.MatchesApp("企业微信", null) && !WeChatNotice.MatchesApp("微信文件", null), "WeChat source filter");
            notice.Show(0, "private", "secret", false, false);
            Check(notice.Heading == "微信" && notice.Body == "微信有新消息", "preview off discards both fields");
            notice.Show(2000, "Sender", "One\nTwo", true, false);
            Check(notice.Heading == "微信 · Sender" && notice.Body == "One Two", "one-line preview");
            notice.Show(4000, "微信", "Newest", true, false);
            Check(notice.Heading == "微信" && notice.Body == "Newest" && !notice.ShouldDismiss(8999) && notice.ShouldDismiss(9000), "latest notification resets five-second deadline");
            notice.Show(9000, "", "", true, false);
            Check(notice.Body == "微信有新消息", "missing content falls back to private reminder");
            Check(!notice.Show(10000, null, null, true, true) && notice.Body == "微信有新消息"
                && notice.ShouldDismiss(14000), "late flash preserves system card and original deadline");
            notice.Show(14000, "private", "secret", true, true);
            Check(notice.Heading == "微信" && notice.Body == "微信请求关注", "flash never claims content");
            Check(notice.Show(14500, "Sender", "Detail", true, false) && notice.Body == "Detail"
                && !notice.Show(14600, null, null, false, true) && notice.Body == "Detail", "system notification upgrades flash in place");
            notice.Clear();
            Check(!notice.Active && notice.Heading == null && notice.Body == null, "clearing drops the card");
            notice.Show(20000, null, null, false, true);
            notice.Show(20600, null, null, false, true);
            Check(notice.StartedAt == 20000 && !notice.ShouldDismiss(25000), "repeated flashes extend the card without restarting motion");
            notice.Show(27000, null, null, false, true);
            Check(notice.StartedAt == 27000, "a new burst replays the entrance");
        }

        private static void CheckFrostedWindow(string path)
        {
            var lib = new Library();
            var area = Screen.PrimaryScreen.WorkingArea;
            lib.View.X = area.Left + 70;
            lib.View.Y = area.Bottom - 170;
            lib.View.DockTop = lib.View.HideFullscreen = lib.View.HidePaused = lib.View.HoverHide = false;
            lib.View.CoverColors = lib.View.ShowCover = lib.View.ShowTransport = false;
            lib.View.Background = Color.FromArgb(72, 72, 72).ToArgb();
            using (var island = new Island(lib, path))
            using (var backing = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false, TopMost = true, Bounds = island.Bounds, BackColor = Color.Black })
            using (var focus = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false, TopMost = true, Bounds = new Rectangle(area.Left + 50, island.Top, 5, 5) })
            {
                int mid = island.Width / 2;
                var half = new Panel { Bounds = new Rectangle(mid, 0, mid, island.Height), BackColor = Color.White };
                backing.Controls.Add(half);
                backing.Show();
                island.Show(backing);
                focus.Show();
                focus.Activate();
                for (int pass = 0; pass < 2; pass++)
                {
                    half.Left = pass == 0 ? mid : 0;
                    backing.Refresh();
                    for (int frame = 0; frame < 35; frame++) { Application.DoEvents(); Thread.Sleep(15); }
                    island.UpdateBackdrop(100000L * (pass + 1));
                    island.Refresh();
                    var pt = island.PointToScreen(new Point(mid, island.Height - 7));
                    Check(GetForegroundWindow() != island.Handle && WindowFromPoint(pt) == island.Handle, "unfocused glass is visible");
                    using (var strip = new Bitmap(121, 1))
                    using (var g = Graphics.FromImage(strip))
                    {
                        g.CopyFromScreen(pt.X - 60, pt.Y, 0, 0, strip.Size);
                        int[] samples = new[] { 0, 57, 63, 120 }.Select(i => (int)strip.GetPixel(i, 0).R).ToArray();
                        Console.WriteLine("Frost samples: " + string.Join(",", samples));
                        if (pass == 1) Array.Reverse(samples);
                        Check(samples[3] - samples[0] > 35 && samples[1] > samples[0] + 3 && samples[2] < samples[3] - 3 &&
                            samples[2] >= samples[1], "moving background is visible and its hard edge is blurred");
                    }
                }
                island.Close();
            }
        }

        private static void CheckAppearance()
        {
            var a = new Line { At = 0, Text = "相同的一句", Translation = "first" };
            var b = new Line { At = 2, Text = "相同的一句", Translation = "second" };
            var c = new Line { At = 4, Text = "下一句" };
            var slide = new LineTransition();
            slide.Observe(a, b, 1.95, 1000, true, false, b.Text, 1);
            slide.Observe(b, c, 2.05, 1100, true, false, c.Text, 0);
            Check(slide.Outgoing == a && slide.PromotePreview && slide.OldHighlight == 1, "promote adjacent preview, including repeated text");
            Check(slide.Progress(1100) == 0 && slide.Progress(1220) == 0.5f && slide.Progress(1340) == 1, "slide start, midpoint and settlement");
            slide.Observe(a, b, 1.95, 1350, true, true, a.Translation, 0);
            Check(slide.Outgoing == null, "seek backwards snaps");
            slide.Observe(b, c, 2.05, 1450, true, true, b.Translation, 0);
            Check(slide.Outgoing == a && !slide.PromotePreview && slide.OldSecondary == a.Translation, "translate both rows together");
            slide.Observe(c, null, 4.5, 1510, true, false, "", 0);
            Check(slide.Outgoing == null, "seek forwards snaps");
            slide.Observe(a, b, 1.95, 1600, false, false, b.Text, 0);
            slide.Observe(b, c, 2.05, 1700, true, false, c.Text, 0);
            Check(slide.Outgoing == null, "no queued animation after pause or reduced motion");

            using (var sample = new Bitmap(48, 48))
            using (var g = Graphics.FromImage(sample))
            {
                g.Clear(Color.Transparent);
                Check(!CoverColors.Dominant(sample).HasValue, "transparent cover fallback");
                g.Clear(Color.Navy);
                g.FillRectangle(Brushes.Gold, 0, 0, 8, 48);
                Check(CoverColors.Dominant(sample).Value.ToArgb() == Color.Navy.ToArgb(), "dominant color ignores minority accent");
            }
            foreach (var bg in new[] { Color.Navy, Color.White, Color.Black, Color.Gold, Color.Gray, Color.FromArgb(117,117,117) })
            {
                double l = CoverColors.Luminance(bg) + 0.05;
                var ink = CoverColors.Ink(bg);
                double contrast = ink == Color.White ? 1.05 / l : l / 0.05;
                Check(contrast >= 4.5 && Math.Abs(contrast - Math.Max(1.05 / l, l / 0.05)) < 0.00001, "choose more legible black or white");
            }
        }
    }
}
