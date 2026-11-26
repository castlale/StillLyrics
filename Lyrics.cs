using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StillLyrics
{
    internal sealed class Word
    {
        public double Start;
        public int EndIndex;
    }

    internal sealed class Line
    {
        public double At;
        public string Text, Translation;
        public List<Word> Words = new List<Word>();
        public int Highlight(double time)
        {
            int count = 0;
            foreach (var word in Words)
            {
                if (word.Start > time)
                    break;
                count = word.EndIndex;
            }

            return count;
        }
    }

    internal sealed class Lyrics
    {
        private static readonly Regex Stamp = new Regex(@"\[(\d{1,3}):([0-5]\d)(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex WordStamp = new Regex(@"<(\d{1,3}):([0-5]\d)(?:[\.:](\d{1,3}))?>", RegexOptions.Compiled);
        private static readonly Regex YrcLine = new Regex(@"^\[(\d+),\d+\]", RegexOptions.Compiled);
        private static readonly Regex YrcWord = new Regex(@"\((\d+),\d+,\d+\)([^(\r\n]*)", RegexOptions.Compiled);
        private readonly List<Line> lines = new List<Line>();
        public int Count
        {
            get
            {
                return lines.Count;
            }
        }

        public bool HasWords { get; private set; }
        public bool HasTranslation { get; private set; }

        private static double Time(Match m)
        {
            return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value.PadRight(3, '0')) / 1000.0;
        }

        internal static Lyrics Parse(string raw)
        {
            return Parse(raw, null, null);
        }

        internal static Lyrics Parse(string raw, string translated, string yrc)
        {
            var result = new Lyrics();
            var ordered = new SortedDictionary<double, Line>();
            raw = raw ?? "";
            var offset = Regex.Match(raw, @"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase);
            int ms = 0;
            if (offset.Success)
                int.TryParse(offset.Groups[1].Value, out ms);
            foreach (string row in raw.Split('\n'))
            {
                var stamps = Stamp.Matches(row);
                if (stamps.Count == 0)
                    continue;
                string body = Stamp.Replace(row, "").Trim();
                var markers = WordStamp.Matches(body);
                foreach (Match stamp in stamps)
                {
                    double at = Time(stamp) - ms / 1000.0;
                    var line = new Line
                    {
                        At = at,
                        Text = WordStamp.Replace(body, "")
                    };
                    int prefix = markers.Count == 0 ? 0 : markers[0].Index;
                    for (int i = 0; i < markers.Count; i++)
                    {
                        var mark = markers[i];
                        int next = i + 1 < markers.Count ? markers[i + 1].Index : body.Length;
                        int length = next - mark.Index - mark.Length;
                        prefix += length;
                        if (length > 0)
                            line.Words.Add(new Word { Start = Time(mark) - ms / 1000.0 + Time(stamp) - Time(stamps[0]), EndIndex = prefix });
                    }

                    Line prior;
                    if (ordered.TryGetValue(at, out prior) && prior.Text.Length > 0)
                    {
                        if (line.Text != prior.Text && line.Text.Length > 0)
                            prior.Translation = line.Text;
                    }
                    else
                        ordered[at] = line;
                }
            }

            if (!string.IsNullOrWhiteSpace(yrc))
            {
                foreach (string row in yrc.Split('\n'))
                {
                    var start = YrcLine.Match(row);
                    if (!start.Success)
                        continue;
                    var line = new Line
                    {
                        At = double.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture) / 1000
                    };
                    var body = new StringBuilder();
                    foreach (Match m in YrcWord.Matches(row))
                    {
                        body.Append(m.Groups[2].Value);
                        double startAt = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000;
                        // Some YRC tracks encode untimed spaces with (0,0,0).
                        if (startAt >= line.At && (line.Words.Count == 0 || startAt >= line.Words[line.Words.Count - 1].Start))
                            line.Words.Add(new Word { Start = startAt, EndIndex = body.Length });
                    }

                    line.Text = body.ToString();
                    if (line.Text.Length == 0)
                        continue;
                    // Pair the line by text as well as time: line and word tracks can have slightly different stamps.
                    var prior = ordered.Values.Where(x => Math.Abs(x.At - line.At) < 1.5 && Compact(x.Text) == Compact(line.Text)).OrderBy(x => Math.Abs(x.At - line.At)).FirstOrDefault();
                    if (prior != null)
                    {
                        prior.Text = line.Text;
                        prior.Words = line.Words;
                    }
                    else if (string.IsNullOrWhiteSpace(raw))
                        ordered[line.At] = line;
                }
            }

            result.lines.AddRange(ordered.Values);
            if (!string.IsNullOrWhiteSpace(translated))
                foreach (var line in Parse(translated).lines)
                {
                    var original = result.lines.Where(x => Math.Abs(x.At - line.At) < 0.25).OrderBy(x => Math.Abs(x.At - line.At)).FirstOrDefault();
                    if (original != null && original.Text != line.Text && line.Text.Length > 0)
                        original.Translation = line.Text;
                }

            result.HasWords = result.lines.Any(x => x.Words.Count > 0);
            result.HasTranslation = result.lines.Any(x => !string.IsNullOrWhiteSpace(x.Translation));
            return result;
        }

        private static string Compact(string s)
        {
            return Regex.Replace(s, @"[\s\p{P}]", "");
        }

        public Line Current(double time, out string next)
        {
            Line following;
            var current = CurrentAndNext(time, out following);
            next = following == null ? "" : following.Text;
            return current;
        }

        internal Line CurrentAndNext(double time, out Line next)
        {
            int lo = 0, hi = lines.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (lines[mid].At <= time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            next = lo < lines.Count ? lines[lo] : null;
            return lo == 0 ? null : lines[lo - 1];
        }

        public string At(double time)
        {
            string next;
            var line = Current(time, out next);
            return line == null ? "" : line.Text;
        }
    }
}
