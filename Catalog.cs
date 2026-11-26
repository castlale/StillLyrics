using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StillLyrics
{
    internal sealed class Edition
    {
        public string Source { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string[] Aliases { get; set; }
        public double Duration { get; set; }
        public string Lrc { get; set; }
        public string Translation { get; set; }
        public string WordTiming { get; set; }
        public bool Instrumental { get; set; }

        private Lyrics decoded;
        internal Lyrics Decode()
        {
            return decoded ?? (decoded = Lyrics.Parse(Lrc, Translation, WordTiming));
        }
    }

    internal sealed class CatalogResult
    {
        internal readonly List<Edition> Items = new List<Edition>();
        internal int Failures;
        internal bool Instrumental;
    }

    internal static class Catalog
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int LCMapStringEx(string locale, uint flags, string source, int length,
            StringBuilder destination, int capacity, IntPtr version, IntPtr reserved, IntPtr sortHandle);

        private static string Simplify(string s)
        {
            string value = (s ?? "").Normalize(NormalizationForm.FormKC);
            if (Regex.IsMatch(value, @"[\u3400-\u9fff]"))
            {
                var mapped = new StringBuilder(value.Length * 2);
                const uint simplifiedChinese = 0x02000000;
                if (LCMapStringEx("zh-CN", simplifiedChinese, value, -1, mapped,
                    mapped.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0) value = mapped.ToString();
            }
            return value;
        }

        private static string Canon(string s)
        {
            return Regex.Replace(Simplify(s), @"[\s\p{P}]", "").ToLowerInvariant();
        }

        private static string ArtistKey(string name)
        {
            var key = Canon(name);
            // NetEase artist 37044972 includes the real name in its display credit.
            return key == "菲菲公主陆绮菲" ? "菲菲公主" : key;
        }

        private static bool Full(string credit, Playback track)
        {
            if (ArtistKey(credit) == ArtistKey(track.Artist)) return true;
            const string separators = @"\s*[,;&/、，]\s*";
            return Regex.Split(credit ?? "", separators).Select(ArtistKey)
                .SequenceEqual(Regex.Split(track.Artist ?? "", separators).Select(ArtistKey));
        }

        private static string SearchTitle(string s)
        {
            return Canon(Regex.Replace(s ?? "", @"\s*[\(\[]feat(?:uring)?\.?\s+[^\)\]]+[\)\]]", "", RegexOptions.IgnoreCase));
        }

        // Some catalogs move a featured artist from the credits into the title.
        private static readonly Regex WithCredit = new Regex(@"\s*[\(\[]with\s+([^\)\]]+)[\)\]]\s*$", RegexOptions.IgnoreCase);
        private static bool HasMatchingGuest(Edition edition, Playback track, string target)
        {
            var credit = WithCredit.Match(edition.Title ?? "");
            if (!credit.Success || SearchTitle(edition.Title.Substring(0, credit.Index)) != target ||
                ArtistKey(edition.Artist) != ArtistKey(track.LeadArtist)) return false;
            if (ArtistKey(track.Artist) == ArtistKey(track.LeadArtist))
                return Math.Abs(edition.Duration - track.Duration) <= 2;
            return Full(edition.Artist + "、" + credit.Groups[1].Value, track);
        }

        internal static IEnumerable<Edition> Rank(IEnumerable<Edition> editions, Playback track, string title)
        {
            string target = SearchTitle(title);
            return editions.Where(e => SearchTitle(e.Title) == target || HasMatchingGuest(e, track, target) || (e.Aliases != null &&
                    e.Aliases.Any(alias => SearchTitle(e.Title + " (" + alias + ")") == target)))
                .Where(e => Full(e.Artist, track) || ArtistKey(e.Artist) == ArtistKey(track.LeadArtist))
                .Where(e => Math.Abs(e.Duration - track.Duration) <= (Full(e.Artist, track) ? 6 : 2))
                .OrderBy(e => Math.Abs(e.Duration - track.Duration) > 2)
                .ThenByDescending(e => Full(e.Artist, track))
                .ThenBy(e => Math.Abs(e.Duration - track.Duration))
                .ThenByDescending(e => !string.IsNullOrWhiteSpace(e.WordTiming));
        }

        private static async Task<T> Json<T>(string url, CancellationToken token, bool netease)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.UserAgent.ParseAdd("StillLyrics/2.0");
                if (netease)
                    request.Headers.Referrer = new Uri("https://music.163.com/");
                using (var response = await Http.SendAsync(request, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var data = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new JavaScriptSerializer
                    {
                        MaxJsonLength = 8 * 1024 * 1024
                    }.Deserialize<T>(data);
                }
            }
        }

        private sealed class LrRecord
        {
            public string id { get; set; }
            public string trackName { get; set; }
            public string artistName { get; set; }
            public string albumName { get; set; }
            public string syncedLyrics { get; set; }
            public double? duration { get; set; }
            public bool instrumental { get; set; }
        }

        private sealed class SearchReply
        {
            public SearchBody result { get; set; }
            public int code { get; set; }
        }

        private sealed class SearchBody
        {
            public Song[] songs { get; set; }
        }

        private sealed class Song
        {
            public string[] alias { get; set; }
            public long id { get; set; }
            public string name { get; set; }
            public Person[] artists { get; set; }
            public Person album { get; set; }
            public double duration { get; set; }
        }

        private sealed class Person
        {
            public string name { get; set; }
        }

        private sealed class TextBody
        {
            public string lyric { get; set; }
        }

        private sealed class Timings
        {
            public TextBody lrc { get; set; }
            public TextBody yrc { get; set; }
            public TextBody tlyric { get; set; }
            public TextBody ytlrc { get; set; }
            public int code { get; set; }
        }

        private static async Task<List<Edition>> Lr(Playback track, string title, CancellationToken token)
        {
            var queries = new[] { new { Title = title, Artist = track.LeadArtist },
                new { Title = Simplify(title), Artist = Simplify(track.LeadArtist) } };
            foreach (var query in queries.Distinct())
            {
                var rows = await Json<LrRecord[]>("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(query.Title)
                    + "&artist_name=" + Uri.EscapeDataString(query.Artist), token, false).ConfigureAwait(false);
                var hits = Rank((rows ?? new LrRecord[0]).Select(r => new Edition { Source = "LRCLIB", Id = r.id,
                    Title = r.trackName, Artist = r.artistName, Album = r.albumName, Duration = r.duration ?? 0,
                    Lrc = r.syncedLyrics, Instrumental = r.instrumental }), track, title)
                    .Where(e => e.Instrumental || e.Decode().Count > 0).ToList();
                if (hits.Count != 0) return hits;
            }
            return new List<Edition>();
        }

        private static async Task<List<Edition>> Netease(Playback track, string title, CancellationToken token, bool all)
        {
            var data = await Json<SearchReply>("https://music.163.com/api/search/get/web?type=1&limit=12&s=" + Uri.EscapeDataString(title + " " + track.LeadArtist), token, true).ConfigureAwait(false);
            if (data == null || data.code != 200)
                throw new HttpRequestException("网易云查询失败");
            var songs = data.result == null ? null : data.result.songs;
            var editions = (songs ?? new Song[0]).Select(s => new Edition { Source = "网易云", Id = s.id.ToString(), Title = s.name, Aliases = s.alias, Artist = string.Join("、", (s.artists ?? new Person[0]).Select(p => p.name)), Album = s.album == null ? "" : s.album.name, Duration = s.duration / 1000 });
            var hits = new List<Edition>();
            foreach (var e in Rank(editions, track, title).Take(3))
            {
                var t = await Json<Timings>("https://music.163.com/api/song/lyric/v1?id=" + e.Id + "&cp=false&lv=0&kv=0&tv=0&rv=0&yv=0&ytv=0&yrv=0", token, true).ConfigureAwait(false);
                if (t == null || t.code != 200)
                    continue;
                e.Lrc = StripCredits(t.lrc == null ? "" : t.lrc.lyric);
                e.WordTiming = t.yrc == null ? null : t.yrc.lyric;
                e.Translation = t.tlyric == null ? null : t.tlyric.lyric;
                if (e.Decode().Count == 0)
                    continue;
                hits.Add(e);
                if (!all)
                    break;
            }

            return hits;
        }

        private static async Task<Edition> SupplementWords(Edition song, CancellationToken token)
        {
            // AMLL indexes authored word timings by the NetEase recording ID.
            if (song == null || song.Decode().HasWords)
                return null;
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var url = "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main/ncm-lyrics/" + song.Id + ".yrc";
                    using (var response = await Http.GetAsync(url, budget.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return null;
                        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var rich = new Edition
                        {
                            Source = "网易云 / AMLL",
                            Id = song.Id,
                            Title = song.Title,
                            Aliases = song.Aliases,
                            Artist = song.Artist,
                            Album = song.Album,
                            Duration = song.Duration,
                            Translation = song.Translation,
                            WordTiming = raw
                        };
                        return rich.Decode().HasWords ? rich : null;
                    }
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    return null;
                }
                catch (HttpRequestException)
                {
                    return null;
                }
            }
        }

        private static string StripCredits(string lrc)
        {
            return Regex.Replace(lrc ?? "", @"(?im)^\[[\d:.]+\]\s*(?:作词|作曲|编曲|制作人|混音|母带|发行|监制|Producer|Composer|Lyricist|OP|SP)(?:\s+[a-z ]+)?\s*[:：][^\n]*(?:\n|$)", "");
        }

        internal static async Task<CatalogResult> Find(Playback track, string title, bool all, bool wantWords, bool wantTranslation, CancellationToken token)
        {
            var result = new CatalogResult();
            try
            {
                result.Items.AddRange(await Lr(track, title, token).ConfigureAwait(false));
            }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                result.Failures++;
            }

            token.ThrowIfCancellationRequested();
            var exact = result.Items.FirstOrDefault(e => !e.Instrumental && Full(e.Artist, track) && Math.Abs(e.Duration - track.Duration) <= 2);
            var decoded = exact == null ? null : exact.Decode();
            if (all || decoded == null || (wantWords && !decoded.HasWords) || (wantTranslation && !decoded.HasTranslation))
            {
                try
                {
                    var local = await Netease(track, title, token, all).ConfigureAwait(false);
                    result.Items.AddRange(local);
                    if (wantWords || all)
                    {
                        var rich = await SupplementWords(local.FirstOrDefault(), token).ConfigureAwait(false);
                        if (rich != null)
                            result.Items.Add(rich);
                    }
                }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested();
                    result.Failures++;
                }
            }

            var ranked = Rank(result.Items, track, title).ToList();
            result.Instrumental = ranked.Any(e => e.Instrumental);
            result.Items.Clear();
            // Among equally trustworthy exact recordings, prefer a requested timing or translation layer.
            result.Items.AddRange(ranked.Where(e => !e.Instrumental).OrderBy(e => Math.Abs(e.Duration - track.Duration) > 2).ThenByDescending(e => Full(e.Artist, track)).ThenByDescending(e => Math.Abs(e.Duration - track.Duration) <= 2 && wantWords && e.Decode().HasWords).ThenByDescending(e => Math.Abs(e.Duration - track.Duration) <= 2 && wantTranslation && e.Decode().HasTranslation));
            return result;
        }
    }
}
