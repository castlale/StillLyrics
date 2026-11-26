using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace StillLyrics
{
    internal static class WeChatWindow
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        private delegate bool WindowVisitor(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RectangleBounds bounds);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [StructLayout(LayoutKind.Sequential)]
        private struct RectangleBounds { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);

        private static bool IsWeChat(IntPtr window)
        {
            int pid;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out pid) == 0) return false;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return WeChatNotice.MatchesApp(process.ProcessName, null);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        internal static bool IsExposedOnDesktop(IntPtr window)
        {
            const int toolOrNoActivate = 0x80 | 0x08000000;
            int cloaked;
            RectangleBounds client;
            var origin = Point.Empty;
            if (!IsWindowVisible(window) || IsIconic(window) ||
                (GetWindowStyle(window, -20) & toolOrNoActivate) != 0 ||
                (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) ||
                !GetClientRect(window, out client) || !ClientToScreen(window, ref origin)) return false;
            var bounds = new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
            foreach (var screen in Screen.AllScreens)
            {
                var area = Rectangle.Intersect(bounds, screen.Bounds);
                if (area.Width < 3 || area.Height < 3) continue;
                // Hit-test the actual desktop so an obscured window does not silence alerts.
                for (int y = 0; y <= 4; y++)
                    for (int x = 0; x <= 4; x++)
                    {
                        var point = new Point(area.Left + 1 + (area.Width - 3) * x / 4,
                            area.Top + 1 + (area.Height - 3) * y / 4);
                        if (GetAncestor(WindowFromPoint(point), 2) == window) return true;
                    }
            }
            return false;
        }

        internal static bool IsOnDesktop()
        {
            bool found = false;
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                found = IsWindowVisible(window) && !IsIconic(window) && IsWeChat(window) && IsExposedOnDesktop(window);
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        private static bool AwaitForeground()
        {
            for (int i = 0; i < 16; i++)
            {
                var window = GetForegroundWindow();
                if (IsWeChat(window) && IsWindowVisible(window) && !IsIconic(window)) return true;
                Thread.Sleep(40);
            }
            return false;
        }

        private static bool Restore(IntPtr window)
        {
            if (!IsWeChat(window)) return false;
            if (IsIconic(window) || !IsWindowVisible(window)) ShowWindowAsync(window, 9); // SW_RESTORE
            SetForegroundWindow(window);
            return AwaitForeground();
        }

        internal static bool Activate(IntPtr flashed)
        {
            try
            {
                if (IsWeChat(flashed) && IsWindowVisible(flashed) && Restore(flashed)) return true;
                if (!IsWeChat(flashed))
                {
                    foreach (var name in new[] { "Weixin", "WeChat" })
                        foreach (var process in Process.GetProcessesByName(name))
                            using (process)
                                if (Restore(process.MainWindowHandle)) return true;
                }
                // The client's own tray action restores its hidden chat UI without starting another instance.
                if (InvokeTrayIcon() && AwaitForeground()) return true;
                return Restore(flashed);
            }
            catch (ElementNotAvailableException) { return Restore(flashed); }
            catch (InvalidOperationException) { return Restore(flashed); }
            catch (UnauthorizedAccessException) { return false; }
            catch (COMException) { return Restore(flashed); }
            catch (Win32Exception) { return false; }
        }

        private static bool InvokeTrayIcon()
        {
            var notificationArea = FindWindow("Shell_TrayWnd", null);
            if (notificationArea == IntPtr.Zero) return false;
            var trayRoot = AutomationElement.FromHandle(notificationArea);
            InvokePattern overflow;
            if (InvokeIconIn(trayRoot, out overflow)) return true;
            IntPtr popup = VisibleOverflow();
            bool expanded = popup == IntPtr.Zero && overflow != null;
            try
            {
                if (expanded)
                {
                    overflow.Invoke();
                    for (int i = 0; i < 10 && popup == IntPtr.Zero; i++)
                    {
                        Thread.Sleep(35);
                        popup = VisibleOverflow();
                    }
                }
                InvokePattern unused;
                return popup != IntPtr.Zero && InvokeIconIn(AutomationElement.FromHandle(popup), out unused);
            }
            finally
            {
                // On success Explorer dismisses the flyout when WeChat takes focus.
                if (expanded && popup != IntPtr.Zero && !AwaitForeground() && IsWindowVisible(popup))
                    overflow.Invoke();
            }
        }

        private static IntPtr VisibleOverflow()
        {
            foreach (var name in new[] { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" })
            {
                var window = FindWindow(name, null);
                if (window != IntPtr.Zero && IsWindowVisible(window)) return window;
            }
            return IntPtr.Zero;
        }

        private static bool InvokeIconIn(AutomationElement root, out InvokePattern overflow)
        {
            overflow = null;
            var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var id = button.Current.AutomationId;
                if (id != "NotifyItemIcon" && id != "SystemTrayIcon") continue;
                var label = (button.Current.Name ?? "").Trim();
                object action;
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out action)) continue;
                var brand = label.Split(new[] { ' ', '\r', '\n', ':', '：' }, 2)[0];
                if (id == "NotifyItemIcon" && WeChatNotice.MatchesApp(brand, null))
                {
                    ((InvokePattern)action).Invoke();
                    return true;
                }
                if (id == "SystemTrayIcon" && (label.Contains("隐藏") ||
                    label.IndexOf("hidden icons", StringComparison.OrdinalIgnoreCase) >= 0))
                    overflow = (InvokePattern)action;
            }
            return false;
        }
    }
}
