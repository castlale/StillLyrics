using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace StillLyrics
{
    // Read the current line rendered by Spotify. The UI publishes no portable timestamp file.
    internal sealed class SpotifyVerse
    {
        internal int Index;
        internal string Text, Next, Translation;
    }

    internal sealed class SpotifyLiveLyrics
    {
        // In the installed Spotify stylesheet this pair selects --lyrics-color-active.
        // Unknown markup deliberately falls through to the timed catalogs.
        private const string Row = "J4_kkGYm5hjbZlztykxr", Active = "SOT5gG00YqnGQ4uR7PwQ";
        private AutomationElement panel, window;
        private Playback lastOpened;
        private delegate bool WindowCallback(IntPtr hwnd, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int size);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

        private static bool Token(string classes, string value)
        {
            return (" " + classes + " ").Contains(" " + value + " ");
        }

        internal static bool IsCurrentRow(string classes)
        {
            return Token(classes, Row) && Token(classes, Active);
        }

        internal static bool MatchesCredits(string[] links, Playback song)
        {
            return links.Contains(song.Title, StringComparer.OrdinalIgnoreCase) &&
                links.Contains(song.LeadArtist, StringComparer.OrdinalIgnoreCase);
        }

        internal SpotifyVerse Read(Playback song)
        {
            try
            {
                var cache = new CacheRequest();
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ControlTypeProperty);
                using (cache.Activate())
                {
                    if (panel != null)
                    {
                        var result = ReadPanel(panel, window, song);
                        if (result != null) return result;
                        panel = null;
                    }
                    var ids = new HashSet<int>();
                    foreach (var p in Process.GetProcessesByName("Spotify")) using (p) ids.Add(p.Id);
                    if (ids.Count == 0) return null;
                    var windows = new List<IntPtr>();
                    EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                    {
                        int pid;
                        GetWindowThreadProcessId(hwnd, out pid);
                        if (ids.Contains(pid))
                        {
                            var cls = new StringBuilder(64);
                            GetClassName(hwnd, cls, cls.Capacity);
                            if (cls.ToString() == "Chrome_WidgetWin_1") windows.Add(hwnd);
                        }
                        return true;
                    }, IntPtr.Zero);
                    foreach (var hwnd in windows)
                    {
                        var root = AutomationElement.FromHandle(hwnd);
                        var groups = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
                        foreach (AutomationElement group in groups)
                        {
                            if (!IsCurrentRow(group.Cached.ClassName)) continue;
                            var parent = TreeWalker.ControlViewWalker.GetParent(group);
                            var result = ReadPanel(parent, root, song);
                            if (result != null) { panel = parent; window = root; return result; }
                        }
                        if (!song.SameRecording(lastOpened) && (!IsWindowVisible(hwnd) || IsIconic(hwnd)))
                        {
                            var names = new[] { "歌词", "歌詞", "Lyrics" };
                            var button = root.FindFirst(TreeScope.Descendants, new AndCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                new OrCondition(names.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray())));
                            object pattern;
                            if (button != null && button.Current.IsEnabled && button.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                            {
                                lastOpened = song;
                                var toggle = (TogglePattern)pattern;
                                if (toggle.Current.ToggleState == ToggleState.Off && (!IsWindowVisible(hwnd) || IsIconic(hwnd))) toggle.Toggle();
                            }
                        }
                    }
                }
            }
            catch (ElementNotAvailableException) { panel = null; }
            catch (InvalidOperationException) { panel = null; }
            catch (COMException) { panel = null; }
            return null;
        }

        private static SpotifyVerse ReadPanel(AutomationElement parent, AutomationElement root, Playback song)
        {
            if (parent == null) return null;
            var children = parent.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            var links = children.Where(n => n.Cached.ControlType == ControlType.Hyperlink).Select(n => n.Cached.Name).ToArray();
            // A hidden Connect window may still contain the previous track's lyrics.
            if (links.Length != 0)
            {
                if (!MatchesCredits(links, song)) return null;
            }
            else
            {
                // Full-page lyrics put the track identity in the playback bar of the same window.
                var bar = SpotifyBar.ReadFromWindow(root);
                if (bar == null || !string.Equals(bar.Title, song.Title, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(bar.LeadArtist, song.LeadArtist, StringComparison.OrdinalIgnoreCase) ||
                    Math.Abs(bar.Duration - song.Duration) > 2 || Math.Abs(bar.Position - song.Position) > 4) return null;
            }
            var rows = children.Where(n => Token(n.Cached.ClassName, Row)).ToArray();
            int index = Array.FindIndex(rows, n => IsCurrentRow(n.Cached.ClassName));
            if (index < 0) return null;
            var text = Parts(rows[index]);
            if (text.Length == 0) return null;
            return new SpotifyVerse { Index = index, Text = text[0], Translation = text.ElementAtOrDefault(1),
                Next = index + 1 == rows.Length ? null : Parts(rows[index + 1]).FirstOrDefault() };
        }

        private static string[] Parts(AutomationElement row)
        {
            return row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(n => n.Cached.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        }
    }
}
