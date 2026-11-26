using System;
using System.Linq;

namespace StillLyrics
{
    internal sealed class WeChatNotice
    {
        internal string Heading { get; private set; }
        internal string Body { get; private set; }
        internal bool Active { get { return Body != null; } }
        internal bool FromSystem { get; private set; }
        internal long StartedAt { get; private set; }
        private long until;

        internal static bool MatchesApp(string name, string appId)
        {
            return string.Equals(name, "微信", StringComparison.Ordinal) ||
                string.Equals(name, "WeChat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Weixin", StringComparison.OrdinalIgnoreCase) ||
                (appId ?? "").EndsWith(@"\Tencent\Weixin\Weixin.exe", StringComparison.OrdinalIgnoreCase) ||
                (appId ?? "").EndsWith(@"\Tencent\WeChat\WeChat.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal bool Show(long now, string title, string summary, bool preview, bool flash, bool restart = false)
        {
            // A delayed taskbar flash carries less information than the current notification.
            if (flash && FromSystem && !ShouldDismiss(now)) return false;
            if (restart || !Active || ShouldDismiss(now)) StartedAt = now;
            FromSystem = !flash;
            Heading = "微信";
            Body = flash ? "微信请求关注" : "微信有新消息";
            if (preview && !flash)
            {
                title = SingleLine(title);
                summary = SingleLine(summary);
                if (summary.Length > 0)
                {
                    if (title.Length > 0 && !MatchesApp(title, null)) Heading += " · " + title;
                    Body = summary;
                }
                else if (title.Length > 0 && !MatchesApp(title, null)) Body = title;
            }
            until = now + 5000;
            return true;
        }

        private static string SingleLine(string value)
        {
            var s = new string((value ?? "").Take(240).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            return s.Trim();
        }

        internal bool ShouldDismiss(long now) { return Active && now >= until; }
        internal void Clear() { Heading = Body = null; FromSystem = false; }
    }
}
