using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace StillLyrics
{
    internal sealed class Preferences
    {
        public bool HoverHide { get; set; }
        public bool WeChatAlerts { get; set; }
        public bool WeChatSystemNotifications { get; set; }
        public bool WeChatPreview { get; set; }
        public bool DockTop { get; set; }
        public bool HidePaused { get; set; }
        public bool HideFullscreen { get; set; }
        public bool TwoLines { get; set; }
        public bool AnimateLines { get; set; }
        public bool CoverColors { get; set; }
        public bool CenterText { get; set; }
        public bool ShowTransport { get; set; }
        public bool Glass { get; set; }
        public bool ShowCover { get; set; }
        public bool Translation { get; set; }
        public bool Karaoke { get; set; }
        public bool InfoOnly { get; set; }
        public bool Bold { get; set; }
        public string Family { get; set; }
        public float FontSize { get; set; }
        public int Zoom { get; set; }
        public int Alpha { get; set; }
        public int Foreground { get; set; }
        public int Background { get; set; }
        public int Accent { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }

        public Preferences()
        {
            HoverHide = DockTop = HideFullscreen = Karaoke = Translation = Bold = true;
            AnimateLines = true;
            WeChatAlerts = true;
            CoverColors = true;
            CenterText = ShowTransport = ShowCover = true;
            Glass = true;
            Zoom = Alpha = 100;
            Family = "Microsoft YaHei UI";
            FontSize = 11f;
            Foreground = Color.FromArgb(246, 246, 239).ToArgb();
            Background = Color.FromArgb(24, 26, 29).ToArgb();
            Accent = Color.FromArgb(158, 222, 192).ToArgb();
        }
    }

    internal sealed class TrackChoice
    {
        public string Key { get; set; }
        public int Duration { get; set; }
        public int OffsetMs { get; set; }
        public Edition Pinned { get; set; }
    }

    internal sealed class Library
    {
        public Preferences View { get; set; }
        public List<TrackChoice> Songs { get; set; }

        public Library()
        {
            View = new Preferences();
            Songs = new List<TrackChoice>();
        }

        internal static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StillLyrics", "settings.json");
        internal static Library Load(string path)
        {
            if (!File.Exists(path))
                return new Library();
            var data = Serializer().Deserialize<Library>(File.ReadAllText(path));
            if (data == null || data.View == null || data.Songs == null)
                throw new InvalidDataException("设置文件格式无效。");
            data.View.Zoom = Math.Max(60, Math.Min(180, data.View.Zoom));
            data.View.Alpha = Math.Max(30, Math.Min(100, data.View.Alpha));
            data.View.FontSize = Math.Max(8, Math.Min(22, data.View.FontSize));
            return data;
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, Serializer().Serialize(this));
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = 32 * 1024 * 1024
            };
        }

        internal TrackChoice Choice(Playback song, bool create)
        {
            var entry = Songs.Where(s => s.Key == song.Key && Math.Abs(s.Duration - song.Duration) <= 2).OrderBy(s => Math.Abs(s.Duration - song.Duration)).FirstOrDefault();
            if (entry == null && create)
            {
                entry = new TrackChoice
                {
                    Key = song.Key,
                    Duration = song.Duration
                };
                Songs.Add(entry);
            }

            return entry;
        }
    }
}
