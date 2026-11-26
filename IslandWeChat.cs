using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace StillLyrics
{
    internal sealed partial class Island
    {
        private readonly WeChatNotice notice = new WeChatNotice();
        private HashSet<Tuple<uint, DateTimeOffset>> seenNotifications;
        private long nextNotificationPoll, nextQuietCheck;
        private bool polling, requesting, shellRegistered;
        private int sourceGeneration, shellMessage;
        private string listenerStatus = "等待检查授权";
        private IntPtr flashingWeChatWindow;
        private bool noticePressed, openingWeChat, demoNotice;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        private static extern bool RegisterShellHookWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool DeregisterShellHookWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            shellMessage = RegisterWindowMessage("SHELLHOOK");
            shellRegistered = shellMessage != 0 && RegisterShellHookWindow(Handle);
        }

        private void UnregisterWeChatShell()
        {
            if (shellRegistered) DeregisterShellHookWindow(Handle);
            shellRegistered = false;
        }

        private void OnWindowFlash(IntPtr window)
        {
            if (closed || library == null || !View.WeChatAlerts) return;
            int pid;
            if (GetWindowThreadProcessId(window, out pid) == 0) return;
            try
            {
                using (var owner = Process.GetProcessById(pid))
                    if (WeChatNotice.MatchesApp(owner.ProcessName, null) && CanShowNotice())
                    {
                        flashingWeChatWindow = window;
                        PresentNotice(null, null, true);
                    }
            }
            catch (ArgumentException) { } // The flashing window may have closed.
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static bool CanShowNotice()
        {
            int state;
            try
            {
                return SHQueryUserNotificationState(out state) == 0 && state == 5 &&
                    ToastNotificationManager.GetDefault().NotificationMode == ToastNotificationMode.Unrestricted;
            }
            catch (Exception) { return false; } // Keep quiet when Windows cannot report its mode.
        }

        internal void PresentNotice(string title, string body, bool flash, bool demo = false)
        {
            if (!demo && (openingWeChat || WeChatWindow.IsOnDesktop()))
            {
                BeginNoticeExit();
                return;
            }
            bool first = !notice.Active;
            if (!notice.Show(clock.ElapsedMilliseconds, title, body, View.WeChatPreview, flash, noticeExitStart.HasValue)) return;
            demoNotice = demo;
            noticeExitStart = null;
            if (first) { passMouse = false; UpdateStyles(); }
            Cursor = Cursors.Hand;
            previous.Visible = transport.Visible = nextSong.Visible = false;
            AccessibleDescription = notice.Heading + " · " + notice.Body;
            RefreshVisibility(Cursor.Position);
            UpdateAnimationClock();
            Invalidate();
        }

        internal void ClearNotice()
        {
            if (!notice.Active) return;
            noticeExitStart = null;
            notice.Clear();
            if (noticePressed) { noticePressed = false; Capture = false; }
            Cursor = Cursors.Default;
            UpdateAnimationClock();
            if (!closed) UpdateStyles();
            AccessibleDescription = null;
            previous.Visible = transport.Visible = nextSong.Visible = View.ShowTransport;
            Invalidate();
        }

        private async void OpenWeChat()
        {
            if (openingWeChat) return;
            openingWeChat = true;
            var target = flashingWeChatWindow;
            BeginNoticeExit();
            try
            {
                bool opened = await System.Threading.Tasks.Task.Run(() => WeChatWindow.Activate(target));
                if (!opened && !closed)
                    tray.ShowBalloonTip(3000, "定格词岛", "未能切换到微信，请从微信托盘图标打开。", ToolTipIcon.Info);
            }
            finally { openingWeChat = false; }
        }

        private void StopWeChat()
        {
            sourceGeneration++;
            seenNotifications = null;
            nextNotificationPoll = 0;
            ClearNotice();
        }

        private void TickWeChat()
        {
            long now = clock.ElapsedMilliseconds;
            if (notice.ShouldDismiss(now)) BeginNoticeExit();
            if (noticeExitStart.HasValue && NoticeExitFinished()) ClearNotice();
            if (notice.Active && now >= nextQuietCheck)
            {
                nextQuietCheck = now + 300;
                if (!CanShowNotice()) ClearNotice();
                else if (!demoNotice && !noticeExitStart.HasValue && WeChatWindow.IsOnDesktop()) BeginNoticeExit();
            }
            if (View.WeChatAlerts && View.WeChatSystemNotifications && !polling && now >= nextNotificationPoll)
                PollNotifications();
        }

        private async void PollNotifications()
        {
            polling = true;
            int generation = sourceGeneration;
            int delay = 1000;
            try
            {
                var listener = UserNotificationListener.Current;
                if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
                {
                    seenNotifications = null;
                    if (notice.FromSystem) ClearNotice();
                    listenerStatus = "未获通知授权";
                    delay = 5000;
                    return;
                }
                IReadOnlyList<UserNotification> items;
                using (var timeout = new CancellationTokenSource(5000))
                    items = await BackgroundPlayback.Complete(listener.GetNotificationsAsync(NotificationKinds.Toast), timeout.Token);
                if (closed || generation != sourceGeneration) return;
                var current = new HashSet<Tuple<uint, DateTimeOffset>>();
                UserNotification latest = null;
                foreach (var item in items)
                {
                    try
                    {
                        var app = item.AppInfo;
                        if (app == null || !WeChatNotice.MatchesApp(app.DisplayInfo.DisplayName, app.AppUserModelId)) continue;
                        var key = Tuple.Create(item.Id, item.CreationTime);
                        current.Add(key);
                        if (seenNotifications == null || seenNotifications.Contains(key) ||
                            DateTimeOffset.UtcNow - item.CreationTime > TimeSpan.FromSeconds(5)) continue;
                        if (latest == null || item.CreationTime >= latest.CreationTime) latest = item;
                    }
                    catch (System.Runtime.InteropServices.COMException) { } // Source app may have been removed.
                }
                seenNotifications = current;
                if (latest == null)
                {
                    if (!listenerStatus.StartsWith("已收到")) listenerStatus = "已授权，尚未收到新的微信系统通知";
                    return;
                }
                listenerStatus = "已收到微信通知 · " + DateTime.Now.ToString("HH:mm:ss");
                if (!CanShowNotice()) return;
                string[] text = null;
                if (View.WeChatPreview)
                {
                    var binding = latest.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding != null)
                        text = binding.GetTextElements().Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                }
                PresentNotice(text == null ? null : text.FirstOrDefault(),
                    text == null ? null : string.Join(" ", text.Skip(1)), false);
            }
            catch (Exception ex)
            {
                if (!closed && generation == sourceGeneration)
                {
                    seenNotifications = null;
                    listenerStatus = "读取失败 · " + ex.HResult.ToString("X8");
                    delay = 5000;
                }
            }
            finally
            {
                polling = false;
                if (generation == sourceGeneration) nextNotificationPoll = clock.ElapsedMilliseconds + delay;
            }
        }

        private async void EnableSystemNotifications()
        {
            if (requesting) return;
            int generation = sourceGeneration;
            requesting = true;
            try
            {
                if (MessageBox.Show(this, "开启后仍响应微信任务栏闪烁。只有微信向 Windows 发送系统通知，词岛才能读取该通知的摘要。是否继续检查通知访问权限？", "微信系统通知",
                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                var listener = UserNotificationListener.Current;
                var access = listener.GetAccessStatus();
                if (access != UserNotificationListenerAccessStatus.Allowed)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                        access = await BackgroundPlayback.Complete(listener.RequestAccessAsync(), timeout.Token);
                }
                if (closed || sourceGeneration != generation) return;
                if (access != UserNotificationListenerAccessStatus.Allowed)
                {
                    MessageBox.Show(this, "Windows 尚未授予词岛通知访问权限。可在 Windows 设置中搜索“通知访问”。如仅需来信提醒，选择“仅任务栏闪烁”即可。", "微信提醒");
                    return;
                }
                StopWeChat();
                View.WeChatAlerts = View.WeChatSystemNotifications = true;
                listenerStatus = "已授权，等待微信系统通知";
                Save();
                TickWeChat();
                MessageBox.Show(this, "系统通知访问已开启。微信只闪烁任务栏时，词岛会显示“微信请求关注”。如果希望展示系统通知提供的正文，可勾选“预览联系人和摘要”。", "微信提醒");
            }
            catch (OperationCanceledException)
            {
                if (!closed && generation == sourceGeneration)
                    MessageBox.Show(this, "Windows 未及时返回授权结果。可重新检查授权，或使用任务栏闪烁提醒。", "微信提醒");
            }
            catch (Exception ex)
            {
                if (!closed) MessageBox.Show(this, "授权失败（" + ex.HResult.ToString("X8") + "）。请使用“启动微信通知版.cmd”启动注册版。", "微信提醒");
            }
            finally { requesting = false; }
        }

        private void BuildWeChatMenu()
        {
            var items = Group(menu.Items, "微信提醒").DropDownItems;
            var status = items.Add("状态");
            status.Enabled = false;
            var flashes = items.Add("仅任务栏闪烁", null, (s, e) =>
            {
                StopWeChat();
                View.WeChatAlerts = true;
                View.WeChatSystemNotifications = false;
                Save();
            }) as ToolStripMenuItem;
            var system = items.Add("任务栏闪烁 + 系统通知…", null, (s, e) => EnableSystemNotifications()) as ToolStripMenuItem;
            var off = items.Add("关闭微信提醒", null, (s, e) =>
            {
                StopWeChat();
                View.WeChatAlerts = false;
                Save();
            }) as ToolStripMenuItem;
            Option(items, "预览联系人和摘要", () => View.WeChatPreview, () =>
            {
                View.WeChatPreview = !View.WeChatPreview;
                ClearNotice();
            });
            items.Add("试播一条提醒", null, (s, e) =>
            {
                if (CanShowNotice()) PresentNotice("示例联系人", "这是一条预览消息", false, true);
                else MessageBox.Show(this, "Windows 当前处于静默状态，或无法确认勿扰状态。词岛已暂停弹窗。", "微信提醒");
            });
            refreshChecks.Add(() =>
            {
                off.Checked = !View.WeChatAlerts;
                system.Checked = View.WeChatAlerts && View.WeChatSystemNotifications;
                flashes.Checked = View.WeChatAlerts && !View.WeChatSystemNotifications;
                system.Enabled = !requesting;
                status.Text = requesting ? "正在检查 Windows 授权…" : off.Checked ? "已关闭" : system.Checked ?
                    (shellRegistered ? "闪烁提醒已开启 · " : "闪烁监听不可用 · ") + listenerStatus :
                    shellRegistered ? "等待微信任务栏闪烁" : "任务栏监听不可用";
            });
        }
    }
}
