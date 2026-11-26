using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed partial class Island
    {
        private readonly List<Action> refreshChecks = new List<Action>();
        private ToolStripMenuItem songMenu, offsetMenu;
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
        private static bool ClientAnimationsEnabled()
        {
            int enabled;
            return !SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled != 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (shellMessage != 0 && m.Msg == shellMessage && m.WParam.ToInt64() == 0x8006)
                OnWindowFlash(m.LParam);
            if (m.Msg == 0x001A) animationsAllowed = ClientAnimationsEnabled();
            base.WndProc(ref m);
            if ((m.Msg == 0x001A || m.Msg == 0x007E) && lyricFont != null && !closed && !dragging)
                ApplyAppearance(false);
        }
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
        private static Icon MakeTrayIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Color.FromArgb(116, 221, 174)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(brush, 1, 6, 20, 20);
                g.FillEllipse(brush, 11, 6, 20, 20);
                g.FillRectangle(brush, 10, 6, 12, 20);
                using (var pen = new Pen(Color.FromArgb(26, 43, 39), 3f))
                {
                    g.DrawLine(pen, 10, 13, 10, 19);
                    g.DrawLine(pen, 16, 10, 16, 22);
                    g.DrawLine(pen, 22, 13, 22, 19);
                }

                var handle = bitmap.GetHicon();
                try
                {
                    using (var wrapper = Icon.FromHandle(handle))
                        return (Icon)wrapper.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        private bool FullScreenWindow()
        {
            var hwnd = GetForegroundWindow();
            NativeRect rc;
            if (hwnd == IntPtr.Zero || hwnd == Handle || !GetWindowRect(hwnd, out rc))
                return false;
            var screen = Screen.FromControl(this).Bounds;
            if (rc.Left > screen.Left || rc.Top > screen.Top || rc.Right < screen.Right || rc.Bottom < screen.Bottom)
                return false;
            var cls = new StringBuilder(128);
            GetClassName(hwnd, cls, cls.Capacity);
            string name = cls.ToString();
            return name != "Progman" && name != "WorkerW" && !name.StartsWith("Shell_", StringComparison.Ordinal);
        }

        private double fadeLevel = 1, fadeStart = 1, fadeTarget = 1;
        private double fadeStarted;
        private int fadeDuration;
        private bool passMouse;

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                if (notice != null && notice.Active) p.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                // Layered + transparent lets Windows route clicks to the window underneath during fade-out.
                if (passMouse) p.ExStyle |= 0x80000 | 0x20;
                return p;
            }
        }

        internal void RefreshVisibility(Point cursor)
        {
            RefreshVisibility(cursor, clock.Elapsed.TotalMilliseconds);
        }

        internal void RefreshVisibility(Point cursor, double now, bool schedule = true)
        {
            if (closed || dragging || menu.Visible || dialogOpen)
                return;
            var local = new Point(cursor.X - Left, cursor.Y - Top);
            bool overControl = !notice.Active && View.ShowTransport && Rectangle.Union(previous.Bounds, nextSong.Bounds).Contains(local);
            UpdateReflection(overControl ? local.X : -1);
            bool hover = !notice.Active && View.HoverHide && !overControl && Bounds.Contains(cursor) && (Region == null || Region.IsVisible(local));
            bool immediate = !moving && ((!notice.Active && View.HidePaused && (track == null || !track.Playing)) ||
                (View.HideFullscreen && FullScreenWindow()));
            double target = !moving && (hover || immediate) ? 0 : 1;
            if (fadeDuration > 0) AdvanceFade(now);
            if (target != fadeTarget || ((immediate || !animationsAllowed) && fadeDuration > 0))
            {
                fadeStart = fadeLevel;
                fadeTarget = target;
                fadeStarted = now;
                fadeDuration = immediate || !animationsAllowed ? 0 : (int)Math.Ceiling(Math.Abs(target - fadeStart) *
                    (target == 0 ? 220 : 180));
                if (fadeDuration == 0) fadeLevel = target;
            }
            bool transparent = hover && !moving;
            if (passMouse != transparent) { passMouse = transparent; UpdateStyles(); }
            ApplyFadeOpacity();
            if (fadeLevel == 0 && target == 0) { if (Visible) Hide(); }
            else if (!Visible)
            {
                if (notice.Active) ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
                if (!Visible) Show();
            }
            if (schedule) UpdateAnimationClock();
        }

        private void AdvanceFade(double now)
        {
            double t = Math.Max(0, Math.Min(1, (now - fadeStarted) / (double)fadeDuration));
            double eased = t * t * (3 - 2 * t);
            fadeLevel = fadeStart + (fadeTarget - fadeStart) * eased;
            if (t >= 1) { fadeLevel = fadeTarget; fadeDuration = 0; }
        }

        private void ApplyFadeOpacity()
        {
            double value = fadeLevel * (notice.Active ? 1 : View.Alpha / 100.0);
            if (Math.Abs(Opacity - value) > 0.0001) Opacity = value;
        }

        private void ApplyAppearance(bool restore)
        {
            if (lyricFont != null)
                lyricFont.Dispose();
            if (captionFont != null)
                captionFont.Dispose();
            lyricFont = new Font(View.Family, View.FontSize * View.Zoom / 100f, View.Bold ? FontStyle.Bold : FontStyle.Regular);
            if (noticeFont != null) noticeFont.Dispose();
            noticeFont = new Font(View.Family, Px(28), FontStyle.Bold, GraphicsUnit.Pixel);
            captionFont = new Font(View.Family, 8f * View.Zoom / 100f);
            int leftExtra = View.ShowTransport ? 116 : 0, rightExtra = View.ShowCover ? 52 : 0;
            if (View.CenterText) leftExtra = rightExtra = Math.Max(leftExtra, rightExtra);
            SetPalette(false);
            ApplyFadeOpacity();
            var center = View.X.HasValue && View.Y.HasValue ? new Point(View.X.Value + Px(224), View.Y.Value + Px(42)) : Screen.PrimaryScreen.WorkingArea.Location;
            var screen = restore ? Screen.FromPoint(center) : Screen.FromControl(this);
            var area = screen.WorkingArea;
            Size = new Size(Math.Min(area.Width, Px(448 + leftExtra + rightExtra)), Px(84));
            int buttonTop = (Height - Px(44)) / 2;
            previous.Bounds = new Rectangle(Px(16), buttonTop, Px(32), Px(44));
            transport.Bounds = new Rectangle(Px(48), buttonTop, Px(44), Px(44));
            nextSong.Bounds = new Rectangle(Px(92), buttonTop, Px(32), Px(44));
            previous.Visible = transport.Visible = nextSong.Visible = View.ShowTransport && !notice.Active;
            // A taskbar at the top needs breathing room, independent of the user's lyric zoom.
            int top = area.Top + (area.Top > screen.Bounds.Top ? (int)Math.Ceiling(12 * dpiScale) : 0);
            int x = restore ? View.X ?? (area.Left + (area.Width - Width) / 2) : Left;
            int y = restore ? View.Y ?? (area.Top + Px(12)) : Top;
            if (View.DockTop)
            {
                x = area.Left + (area.Width - Width) / 2;
                y = top;
            }

            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(top, Math.Min(y, area.Bottom - Height)));
            int d = Px(40);
            using (var path = new GraphicsPath())
            {
                if (View.DockTop && top == screen.Bounds.Top)
                {
                    path.AddLine(0, 0, Width, 0);
                }
                else
                {
                    path.AddArc(0, 0, d, d, 180, 90);
                    path.AddArc(Width - d, 0, d, d, 270, 90);
                }

                path.AddArc(Width - d, Height - d, d, d, 0, 90);
                path.AddArc(0, Height - d, d, d, 90, 90);
                path.CloseFigure();
                var old = Region;
                Region = new Region(path);
                if (glassOutline != null) glassOutline.Dispose();
                glassOutline = (GraphicsPath)path.Clone();
                ClearGlass();
                if (old != null)
                    old.Dispose();
            }

            Invalidate();
        }

        private ToolStripMenuItem Group(ToolStripItemCollection items, string title)
        {
            var group = new ToolStripMenuItem(title);
            items.Add(group);
            return group;
        }

        private void Option(ToolStripItemCollection items, string title, Func<bool> selected, Action change)
        {
            var item = new ToolStripMenuItem(title);
            items.Add(item);
            refreshChecks.Add(() => item.Checked = selected());
            item.Click += (s, e) =>
            {
                change();
                Save();
                UpdateText();
                Invalidate();
            };
        }

        private void BuildMenu()
        {
            transportMenu = new ToolStripMenuItem("播放 Spotify", null, (s, e) => ChangePlayback());
            menu.Items.Add(transportMenu);
            RefreshTransport();
            BuildWeChatMenu();
            var behavior = Group(menu.Items, "词岛行为").DropDownItems;
            Option(behavior, "鼠标移入时隐藏", () => View.HoverHide, () => View.HoverHide = !View.HoverHide);
            Option(behavior, "灵动大陆 · 顶部居中贴边", () => View.DockTop, () =>
            {
                View.DockTop = !View.DockTop;
                ApplyAppearance(true);
            });
            Option(behavior, "暂停或停止时隐藏", () => View.HidePaused, () => View.HidePaused = !View.HidePaused);
            Option(behavior, "当前屏幕全屏时隐藏", () => View.HideFullscreen, () => View.HideFullscreen = !View.HideFullscreen);
            behavior.Add("移动词岛…", null, (s, e) =>
            {
                moving = true;
                View.DockTop = false;
                ApplyAppearance(false);
                Show();
                tray.ShowBalloonTip(2500, "移动词岛", "拖动歌词条并松手，即可保存位置。", ToolTipIcon.Info);
            });
            behavior.Add("恢复到主屏顶部", null, (s, e) =>
            {
                moving = false;
                View.X = null;
                View.Y = null;
                View.DockTop = true;
                ApplyAppearance(true);
                Save();
            });
            var display = Group(menu.Items, "歌词显示").DropDownItems;
            Option(display, "歌词居中显示", () => View.CenterText, () =>
            {
                View.CenterText = !View.CenterText;
                ApplyAppearance(false);
            });
            Option(display, "双行 · 当前句与下一句", () => View.TwoLines, () => View.TwoLines = !View.TwoLines);
            Option(display, "双行换句动效", () => View.AnimateLines, () => View.AnimateLines = !View.AnimateLines);
            Option(display, "优先在第二行显示译文", () => View.Translation, () =>
            {
                View.Translation = !View.Translation;
                RefreshLayers();
            });
            Option(display, "按词源时间逐字高亮", () => View.Karaoke, () =>
            {
                View.Karaoke = !View.Karaoke;
                RefreshLayers();
            });
            Option(display, "仅显示歌曲信息", () => View.InfoOnly, () => View.InfoOnly = !View.InfoOnly);
            var look = Group(menu.Items, "词岛外观").DropDownItems;
            Option(look, "毛玻璃 · 透出后方窗口", () => View.Glass, () =>
            {
                View.Glass = !View.Glass;
                ReleaseBackdrop();
                nextCapture = 0;
                SetPalette(false);
                ClearGlass();
                Invalidate(true);
            });
            Option(look, "左侧播放与切歌按钮", () => View.ShowTransport, () =>
            {
                View.ShowTransport = !View.ShowTransport;
                ApplyAppearance(false);
            });
            Option(look, "右侧歌曲封面", () => View.ShowCover, () =>
            {
                View.ShowCover = !View.ShowCover;
                RefreshCoverOptions();
            });
            Option(look, "跟随 Spotify 封面主色 · 自动黑白字", () => View.CoverColors, () =>
            {
                View.CoverColors = !View.CoverColors;
                RefreshCoverOptions();
            });
            Option(look, "粗体歌词", () => View.Bold, () =>
            {
                View.Bold = !View.Bold;
                ApplyAppearance(false);
            });
            look.Add("字体和字号…", null, (s, e) =>
            {
                dialogOpen = true;
                try
                {
                    using (var initial = new Font(View.Family, View.FontSize, View.Bold ? FontStyle.Bold : FontStyle.Regular))
                    using (var f = new FontDialog
                    {
                        Font = initial,
                        ShowEffects = false,
                        MinSize = 8,
                        MaxSize = 22
                    }

                    )
                    {
                        if (f.ShowDialog() == DialogResult.OK)
                        {
                            View.Family = f.Font.FontFamily.Name;
                            View.Bold = f.Font.Bold;
                            View.FontSize = f.Font.SizeInPoints;
                            ApplyAppearance(false);
                            Save();
                        }
                    }
                }
                finally
                {
                    dialogOpen = false;
                }
            });
            var zoom = Group(look, "词岛缩放").DropDownItems;
            foreach (int v in new[]
            {
                75,
                90,
                100,
                115,
                130,
                150
            }

            )
                Option(zoom, v + "%", () => View.Zoom == v, () =>
                {
                    View.Zoom = v;
                    ApplyAppearance(false);
                });
            var opacity = Group(look, "整体不透明度").DropDownItems;
            foreach (int v in new[]
            {
                40,
                60,
                80,
                90,
                100
            }

            )
                Option(opacity, v + "%", () => View.Alpha == v, () =>
                {
                    View.Alpha = v;
                    ApplyFadeOpacity();
                });
            PickColor(look, "歌词颜色…", () => View.Foreground, c => View.Foreground = c);
            PickColor(look, "高亮颜色…", () => View.Accent, c => View.Accent = c);
            PickColor(look, "底色…", () => View.Background, c => View.Background = c);
            menu.Items.Add(new ToolStripSeparator());
            songMenu = Group(menu.Items, "这首歌");
            var sourceNote = songMenu.DropDownItems.Add("当前歌词");
            sourceNote.Enabled = false;
            var pinCurrent = songMenu.DropDownItems.Add("记住当前版本", null, (s, e) =>
            {
                if (edition != null && recording != null)
                    Pin(recording, edition);
            });
            songMenu.DropDownItems.Add("选择并记住歌词版本…", null, (s, e) => ChooseVersion());
            songMenu.DropDownItems.Add("导入并记住 LRC 或逐字歌词…", null, (s, e) => ImportFile());
            songMenu.DropDownItems.Add("恢复自动匹配", null, (s, e) =>
            {
                if (recording == null)
                    return;
                if (choice != null)
                {
                    choice.Pinned = null;
                    Save();
                }

                LoadTrack(true);
            });
            offsetMenu = Group(songMenu.DropDownItems, "歌词时间");
            offsetMenu.DropDownItems.Add("歌词提前 0.25 秒", null, (s, e) => Offset(250));
            offsetMenu.DropDownItems.Add("歌词延后 0.25 秒", null, (s, e) => Offset(-250));
            offsetMenu.DropDownItems.Add("恢复原时间", null, (s, e) => Offset(0));
            menu.Items.Add("退出定格词岛", null, (s, e) => Close());
            menu.Opening += (s, e) =>
            {
                foreach (var refresh in refreshChecks)
                    refresh();
                songMenu.Enabled = recording != null;
                sourceNote.Text = liveLine != null ? "Spotify 桌面歌词 · 实时" : edition == null ? "当前没有同步歌词" : edition.Source + (lyrics.HasWords ? " · 逐字" : " · 逐行") + (choice != null && choice.Pinned != null ? " · 已记住" : "");
                offsetMenu.Enabled = liveLine == null;
                pinCurrent.Enabled = liveLine == null && edition != null;
                offsetMenu.Text = "歌词时间  " + ((choice == null ? 0 : choice.OffsetMs) / 1000.0).ToString("+0.00;-0.00;0.00") + " 秒";
            };
        }

        private void RefreshLayers()
        {
            if (recording != null && (choice == null || choice.Pinned == null))
                LoadTrack(true);
        }

        private void PickColor(ToolStripItemCollection items, string title, Func<int> get, Action<int> set)
        {
            items.Add(title, null, (s, e) =>
            {
                dialogOpen = true;
                try
                {
                    using (var d = new ColorDialog
                    {
                        Color = Color.FromArgb(get()),
                        FullOpen = true
                    }

                    )
                    {
                        if (d.ShowDialog() == DialogResult.OK)
                        {
                            set(d.Color.ToArgb());
                            ApplyAppearance(false);
                            Save();
                        }
                    }
                }
                finally
                {
                    dialogOpen = false;
                }
            });
        }

        private void Offset(int delta)
        {
            if (recording == null)
                return;
            choice = library.Choice(recording, true);
            choice.OffsetMs = delta == 0 ? 0 : Math.Max(-30000, Math.Min(30000, choice.OffsetMs + delta));
            Save();
            UpdateText();
        }

        private void Pin(Playback item, Edition version)
        {
            if (!item.SameRecording(recording))
            {
                MessageBox.Show("播放曲目已改变，请为当前歌曲重新选择。", "定格词岛");
                return;
            }

            CancelLookup();
            choice = library.Choice(recording, true);
            choice.Pinned = version;
            ClearLive();
            Save();
            Use(version);
        }

        private void ChooseVersion()
        {
            var item = recording;
            if (item == null)
                return;
            dialogOpen = true;
            try
            {
                using (var picker = new VersionPicker(item, View.Karaoke, View.Translation))
                {
                    if (picker.ShowDialog() == DialogResult.OK && picker.Selection != null)
                        Pin(item, picker.Selection);
                }
            }
            finally
            {
                dialogOpen = false;
            }
        }

        private void ImportFile()
        {
            var item = recording;
            if (item == null)
                return;
            dialogOpen = true;
            try
            {
                using (var d = new OpenFileDialog
                {
                    Filter = "同步歌词 (*.lrc;*.yrc)|*.lrc;*.yrc",
                    Title = "为「" + item.Title + "」选择歌词"
                }

                )
                {
                    if (d.ShowDialog() != DialogResult.OK)
                        return;
                    var f = new System.IO.FileInfo(d.FileName);
                    if (f.Length > 1024 * 1024)
                        throw new System.IO.InvalidDataException("歌词文件超过 1 MB。");
                    string raw = System.IO.File.ReadAllText(d.FileName);
                    var version = new Edition
                    {
                        Source = "本地",
                        Id = f.Name,
                        Title = item.Title,
                        Artist = item.Artist,
                        Duration = item.Duration
                    };
                    if (f.Extension.Equals(".yrc", StringComparison.OrdinalIgnoreCase))
                        version.WordTiming = raw;
                    else
                        version.Lrc = raw;
                    if (version.Decode().Count == 0)
                        throw new System.IO.InvalidDataException("没有识别到有效时间标签。");
                    Pin(item, version);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "导入歌词");
            }
            finally
            {
                dialogOpen = false;
            }
        }
    }
}
