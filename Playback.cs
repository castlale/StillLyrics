using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Automation;

namespace StillLyrics
{
    internal sealed class Playback
    {
        public string Title, Artist, LeadArtist;
        public int Position, Duration;
        public bool Playing;
        public string Key { get { return Title + "\n" + Artist; } }
        public bool SameRecording(Playback other)
        {
            return other != null && Key == other.Key && Math.Abs(Duration - other.Duration) <= 2;
        }
    }

    // Connect can leave the desktop time label unchanged while audio continues on the phone.
    // Anchor to actual changes reported by the player and use a monotonic clock in between.
    internal sealed class PlaybackClock
    {
        private long anchor;
        private Playback last;
        public void Observe(Playback item, long now)
        {
            if (!item.SameRecording(last) || item.Playing != last.Playing ||
                item.Position != last.Position) anchor = now;
            last = item;
        }
        public double Position(long now)
        {
            if (last == null) return 0;
            double delta = last.Playing ? Math.Max(0, now - anchor) / 1000.0 : 0;
            return Math.Min(last.Duration, last.Position + delta);
        }
        public void Clear() { last = null; }
    }

    // Only use Spotify's playback bar so other media sessions keep their own controls.
    internal sealed class SpotifyBar
    {
        private AutomationElement bar;
        private static readonly string[] Names = { "当前播放栏", "Now playing bar" };

        internal static Playback ReadFromWindow(AutomationElement window)
        {
            var element = window.FindFirst(TreeScope.Descendants, BarCondition());
            return element == null ? null : new SpotifyBar { bar = element }.Read();
        }

        internal static bool SetPlaying(bool play)
        {
            var root = FindBar();
            if (root == null) return false;
            foreach (AutomationElement button in root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                var name = button.Current.Name;
                bool pause = name == "暂停" || name == "暫停" || name == "Pause";
                if (!pause && name != "播放" && name != "Play") continue;
                if (pause == play) return true; // The requested state is already visible.
                object pattern;
                if (!button.Current.IsEnabled || !button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
                ((InvokePattern)pattern).Invoke();
                return true;
            }
            return false;
        }

        public Playback Read()
        {
            try
            {
                if (bar == null) bar = FindBar();
                if (bar == null) return null;
                var cache = new CacheRequest();
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.AutomationElementMode = AutomationElementMode.None;
                var links = new List<string>();
                var times = new List<int>();
                bool? playing = null;
                using (cache.Activate())
                {
                    foreach (AutomationElement el in bar.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        var name = (el.Cached.Name ?? "").Trim();
                        var type = el.Cached.ControlType;
                        if (type == ControlType.Hyperlink && times.Count == 0 && name.Length > 0)
                            links.Add(name);
                        if (type == ControlType.Button)
                        {
                            if (name == "暂停" || name == "暫停" || name == "Pause") playing = true;
                            if (name == "播放" || name == "Play") playing = false;
                        }
                        int seconds;
                        if (type == ControlType.Text && ParseTime(name, out seconds)) times.Add(seconds);
                    }
                }
                if (links.Count < 2 || times.Count < 2 || !playing.HasValue)
                {
                    bar = null;
                    return null;
                }
                int position = times[0], end = times[times.Count - 1];
                int duration = end < 0 ? position - end : end;
                if (position < 0 || duration <= 0 || position > duration + 1) return null;
                return new Playback { Title = links[0], LeadArtist = links[1],
                    Artist = string.Join("、", links.Skip(1)), Duration = duration,
                    Position = position, Playing = playing.Value };
            }
            catch (ElementNotAvailableException) { bar = null; return null; }
            catch (System.Runtime.InteropServices.COMException) { bar = null; return null; }
        }

        private static bool ParseTime(string text, out int seconds)
        {
            seconds = 0;
            bool negative = text.StartsWith("-") || text.StartsWith("−");
            string[] parts = (negative ? text.Substring(1) : text).Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            foreach (string part in parts)
            {
                int value;
                if (part.Length == 0 || part.Length > 3 ||
                    !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
                seconds = seconds * 60 + value;
            }
            if (negative) seconds = -seconds;
            return true;
        }

        private static Condition BarCondition()
        {
            return new OrCondition(Names.Select(s => (Condition)new PropertyCondition(
                AutomationElement.NameProperty, s, PropertyConditionFlags.IgnoreCase)).ToArray());
        }

        private static AutomationElement FindBar()
        {
            var condition = BarCondition();
            foreach (var process in Process.GetProcessesByName("Spotify"))
            {
                using (process)
                {
                    IntPtr handle;
                    try { handle = process.MainWindowHandle; }
                    catch (InvalidOperationException) { continue; }
                    if (handle == IntPtr.Zero) continue;
                    var root = AutomationElement.FromHandle(handle);
                    var found = root.FindFirst(TreeScope.Descendants, condition);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
