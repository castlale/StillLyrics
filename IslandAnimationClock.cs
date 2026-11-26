using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed partial class Island
    {
        private bool animationClockRunning;
        private int animationGeneration;

        private bool NeedsAnimationFrame()
        {
            if (closed || !animationsAllowed) return false;
            if (PaletteAnimating || (fadeDuration > 0 && !dragging && !dialogOpen && !menu.Visible)) return true;
            if (!Visible) return false;
            if (notice.Active) return noticeExitStart.HasValue || NoticeAnimationAge() < NoticeMotionMs;
            return lineTransition.Outgoing != null && lineTransition.Progress(clock.Elapsed.TotalMilliseconds) < 1;
        }

        private void UpdateAnimationClock()
        {
            if (!animationsAllowed && !closed)
            {
                if (PaletteAnimating) AdvancePalette(clock.Elapsed.TotalMilliseconds - paletteStarted);
                if (fadeDuration > 0) RefreshVisibility(Cursor.Position, clock.Elapsed.TotalMilliseconds, false);
            }
            bool needed = NeedsAnimationFrame();
            if (needed == animationClockRunning) return;
            animationClockRunning = needed;
            int generation = ++animationGeneration;
            if (needed) RunAnimationFrames(generation);
        }

        [DllImport("dcomp.dll")]
        private static extern uint DCompositionWaitForCompositorClock(uint count, IntPtr handles, uint timeoutMs);

        private static bool WaitForAnimationFrame(bool compositor)
        {
            if (compositor)
            {
                try { if (DCompositionWaitForCompositorClock(0, IntPtr.Zero, 80) == 0) return true; }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
            }
            Thread.Sleep(16);
            return false;
        }

        private async void RunAnimationFrames(int generation)
        {
            bool compositor = true;
            do
            {
                // Queue only the next display tick so a busy UI cannot accumulate stale frames.
                compositor = await Task.Run(() => WaitForAnimationFrame(compositor));
                if (generation != animationGeneration || closed || IsDisposed) return;
                if (noticeExitStart.HasValue)
                {
                    if (NoticeExitFinished())
                    {
                        ClearNotice();
                        if (generation != animationGeneration) return;
                    }
                    else if (View.ShowTransport)
                    {
                        float edge = NoticeExitEdge(clock.Elapsed.TotalMilliseconds - noticeExitStart.Value);
                        previous.Visible = edge <= previous.Left;
                        transport.Visible = edge <= transport.Left;
                        nextSong.Visible = edge <= nextSong.Left;
                    }
                }
                if (fadeDuration > 0) RefreshVisibility(Cursor.Position, clock.Elapsed.TotalMilliseconds, false);
                if (PaletteAnimating) AdvancePalette(clock.Elapsed.TotalMilliseconds - paletteStarted);
                else if (notice.Active) Invalidate();
                else if (lineTransition.Outgoing != null)
                {
                    var column = TextColumn;
                    Invalidate(Rectangle.FromLTRB(column.Left, Px(28), column.Right, Height));
                }
            } while (NeedsAnimationFrame());
            animationClockRunning = false;
        }
    }
}
