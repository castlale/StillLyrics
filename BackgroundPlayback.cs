using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace StillLyrics
{
    internal sealed class BackgroundPlayback
    {
        private readonly SpotifyBar desktop = new SpotifyBar();
        private GlobalSystemMediaTransportControlsSessionManager manager;
        private long retryAfter;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Playback lastDesktop;

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        private static bool HasExpandedPlayer()
        {
            bool expanded = false;
            foreach (var process in Process.GetProcessesByName("Spotify"))
            {
                using (process)
                {
                    try
                    {
                        var handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero && !IsIconic(handle)) expanded = true;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            return expanded;
        }

        internal async Task<Playback> Read()
        {
            bool expanded = HasExpandedPlayer();
            if (expanded)
            {
                lastDesktop = desktop.Read();
                if (lastDesktop != null) return lastDesktop;
            }
            var system = await ReadSession().ConfigureAwait(false);
            if (system != null)
            {
                if (lastDesktop != null && lastDesktop.Title == system.Title &&
                    Math.Abs(lastDesktop.Duration - system.Duration) <= 2 &&
                    (lastDesktop.LeadArtist == system.LeadArtist || lastDesktop.Artist == system.Artist))
                {
                    system.Artist = lastDesktop.Artist;
                    system.LeadArtist = lastDesktop.LeadArtist;
                }
                return system;
            }
            return expanded ? null : desktop.Read();
        }

        internal static async Task<T> Complete<T>(IAsyncOperation<T> operation, CancellationToken token)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (op, status) =>
            {
                try { result.TrySetResult(op.GetResults()); }
                catch (Exception ex) { result.TrySetException(ex); }
            };
            using (token.Register(() => { if (result.TrySetCanceled()) operation.Cancel(); }))
                return await result.Task.ConfigureAwait(false);
        }

        private static GlobalSystemMediaTransportControlsSession SpotifySession(GlobalSystemMediaTransportControlsSessionManager sessions)
        {
            return sessions.GetSessions().FirstOrDefault(s =>
                s.SourceAppUserModelId.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static async Task<bool> SetPlaying(bool play)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(2500))
                {
                    var sessions = await Complete(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), timeout.Token).ConfigureAwait(false);
                    var session = SpotifySession(sessions);
                    if (session != null && await Complete(play ? session.TryPlayAsync() : session.TryPauseAsync(), timeout.Token).ConfigureAwait(false))
                        return true;
                }
            }
            catch (Exception) { /* Some Connect sessions expose controls only in Spotify's playback bar. */ }
            return SpotifyBar.SetPlaying(play);
        }

        internal static async Task<bool> Skip(bool forward)
        {
            using (var timeout = new CancellationTokenSource(2500))
            {
                var sessions = await Complete(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), timeout.Token).ConfigureAwait(false);
                var session = SpotifySession(sessions);
                if (session == null) return false;
                return await Complete(forward ? session.TrySkipNextAsync() : session.TrySkipPreviousAsync(), timeout.Token).ConfigureAwait(false);
            }
        }

        internal static async Task<Bitmap> ReadCover(Playback track, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(3000);
                var ct = deadline.Token;
                var sessions = await Complete(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), ct).ConfigureAwait(false);
                var session = SpotifySession(sessions);
                if (session == null) return null;
                var media = await Complete(session.TryGetMediaPropertiesAsync(), ct).ConfigureAwait(false);
                var artists = Regex.Split((media.Artist ?? "").Trim(), @"\s*[;、，]\s*|,\s+");
                if (!string.Equals(track.Title.Trim(), (media.Title ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ||
                    !(artists.Contains(track.LeadArtist, StringComparer.OrdinalIgnoreCase) ||
                      string.Equals(media.Artist, track.Artist, StringComparison.OrdinalIgnoreCase)) ||
                    media.Thumbnail == null) return null;
                var timeline = session.GetTimelineProperties();
                if (timeline != null && timeline.EndTime.TotalSeconds > 0 &&
                    Math.Abs(timeline.EndTime.TotalSeconds - track.Duration) > 2) return null;
                using (var stream = await Complete(media.Thumbnail.OpenReadAsync(), ct).ConfigureAwait(false))
                {
                    if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
                    using (var input = stream.GetInputStreamAt(0))
                    using (var data = new DataReader(input))
                    {
                        uint size = (uint)stream.Size;
                        uint loaded = await Complete<uint>(data.LoadAsync(size), ct).ConfigureAwait(false);
                        if (loaded != size) return null;
                        var bytes = new byte[size];
                        data.ReadBytes(bytes);
                        using (var memory = new MemoryStream(bytes, false))
                        using (var image = Image.FromStream(memory))
                        {
                            var thumbnail = new Bitmap(160, 160);
                            try
                            {
                                using (var g = Graphics.FromImage(thumbnail))
                                {
                                    int side = Math.Min(image.Width, image.Height);
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(image, new Rectangle(0, 0, 160, 160),
                                        (image.Width - side) / 2, (image.Height - side) / 2, side, side, GraphicsUnit.Pixel);
                                }
                                return thumbnail;
                            }
                            catch { thumbnail.Dispose(); throw; }
                        }
                    }
                }
            }
        }

        internal async Task<Playback> ReadSession()
        {
            if (clock.ElapsedMilliseconds < retryAfter) return null;
            try
            {
                using (var deadline = new CancellationTokenSource(1800))
                {
                    if (manager == null) manager = await Complete(GlobalSystemMediaTransportControlsSessionManager
                        .RequestAsync(), deadline.Token).ConfigureAwait(false);
                    var session = SpotifySession(manager);
                    if (session == null) return null;
                    var media = await Complete(session.TryGetMediaPropertiesAsync(), deadline.Token).ConfigureAwait(false);
                    var timeline = session.GetTimelineProperties();
                    var status = session.GetPlaybackInfo().PlaybackStatus;
                    if (string.IsNullOrWhiteSpace(media.Title) || string.IsNullOrWhiteSpace(media.Artist) ||
                        timeline == null || timeline.EndTime.TotalSeconds <= 0 ||
                        (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                         status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)) return null;
                    bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    double position = timeline.Position.TotalSeconds;
                    if (playing)
                    {
                        double age = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                        if (age >= 0 && age <= timeline.EndTime.TotalSeconds - position + 2) position += age;
                    }
                    var artists = Regex.Split(media.Artist.Trim(), @"\s*[;、，]\s*|,\s+");
                    return new Playback { Title = media.Title.Trim(), Artist = string.Join("、",artists),
                        LeadArtist = artists[0], Duration = (int)Math.Round(timeline.EndTime.TotalSeconds),
                        Position = (int)Math.Min(timeline.EndTime.TotalSeconds,Math.Max(0,position)), Playing = playing };
                }
            }
            catch (Exception)
            {
                manager = null;
                retryAfter = clock.ElapsedMilliseconds + 3000;
                return null;
            }
        }
    }
}
