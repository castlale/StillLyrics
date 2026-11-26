using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StillLyrics
{
    // The hidden magnifier samples only this window's bounds and excludes its host.
    // Keep the native input frame inside the synchronous callback; retain only a small blur surface.
    internal sealed class FrostedBackdrop : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public uint Width, Height;
            public Guid Format;
            public uint Stride, Offset;
            public UIntPtr Size;
        }
        private delegate bool FrameCallback(IntPtr hwnd, IntPtr source, Header input, IntPtr destination,
            Header output, Rect unclipped, Rect clipped, IntPtr region);
        [DllImport("Magnification.dll")] private static extern bool MagInitialize();
        [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, FrameCallback callback);
        [DllImport("Magnification.dll")] private static extern bool MagSetWindowSource(IntPtr hwnd, Rect rect);
        [DllImport("Magnification.dll")] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] windows);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int styleEx,
            string cls, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);

        private static readonly Guid BgraFormat = new Guid("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");
        private readonly FrameCallback callback;
        private bool initialized, receiving, received, changed;
        private IntPtr magnifier;
        private readonly Size size;
        private readonly Bitmap small;
        private readonly ImageAttributes edges = new ImageAttributes();
        private readonly byte[] pixels, previous;
        internal Bitmap Surface { get; private set; }

        internal FrostedBackdrop(IntPtr owner, Size size, int cell)
        {
            this.size = size;
            callback = Receive;
            int width = Math.Max(1, (size.Width + cell - 1) / cell);
            int height = Math.Max(1, (size.Height + cell - 1) / cell);
            small = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            pixels = new byte[width * height * 4];
            previous = new byte[pixels.Length];
            edges.SetWrapMode(WrapMode.TileFlipXY);
            try
            {
                initialized = MagInitialize();
                if (!initialized) return;
                magnifier = CreateWindowEx(0, "Magnifier", null, 0x40000000, 0, 0, size.Width, size.Height,
                    owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (magnifier != IntPtr.Zero && MagSetWindowFilterList(magnifier, 0, 1, new[] { owner }) &&
                    MagSetImageScalingCallback(magnifier, callback)) return;
                DisposeNative();
            }
            catch (DllNotFoundException) { DisposeNative(); }
            catch (EntryPointNotFoundException) { DisposeNative(); }
        }

        internal bool Capture(Rectangle area, out bool newPixels)
        {
            newPixels = false;
            if (magnifier == IntPtr.Zero || area.Size != size) return false;
            received = changed = false;
            receiving = true;
            try
            {
                var rect = new Rect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
                bool ok = MagSetWindowSource(magnifier, rect) && received;
                newPixels = ok && changed;
                return ok;
            }
            finally { receiving = false; }
        }

        private bool Receive(IntPtr hwnd, IntPtr source, Header input, IntPtr destination, Header output, Rect unclipped, Rect clipped, IntPtr region)
        {
            if (!receiving || source == IntPtr.Zero || input.Format != BgraFormat || input.Width != size.Width ||
                input.Height != size.Height || input.Stride < input.Width * 4 ||
                input.Offset + (ulong)input.Stride * input.Height > input.Size.ToUInt64()) return false;
            try
            {
                using (var frame = new Bitmap(size.Width, size.Height, (int)input.Stride, PixelFormat.Format32bppRgb, IntPtr.Add(source, (int)input.Offset)))
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(frame, new Rectangle(Point.Empty, small.Size), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, edges);
                }
                var rect = new Rectangle(Point.Empty, small.Size);
                var data = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
                finally { small.UnlockBits(data); }
                received = true;
                if (Surface == null) { Surface = new Bitmap(small.Width, small.Height, PixelFormat.Format32bppRgb); changed = true; }
                for (int i = 0; !changed && i < pixels.Length; i++) changed = previous[i] != pixels[i];
                if (!changed) return true;
                Buffer.BlockCopy(pixels, 0, previous, 0, pixels.Length);
                // A [1 2 1] kernel on both axes softens the edge before interpolation back to display size.
                int stride = small.Width * 4;
                for (int y = 0; y < small.Height; y++)
                for (int x = 0; x < small.Width; x++)
                {
                    int i = y * stride + x * 4;
                    int left = x == 0 ? 0 : -4, right = x == small.Width - 1 ? 0 : 4;
                    int up = y == 0 ? 0 : -stride, down = y == small.Height - 1 ? 0 : stride;
                    for (int c = i; c < i + 3; c++)
                        pixels[c] = (byte)((Row(c + up, left, right) + 2 * Row(c, left, right) + Row(c + down, left, right) + 8) / 16);
                }
                data = Surface.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
                finally { Surface.UnlockBits(data); }
                return true;
            }
            catch (ArgumentException) { received = false; return false; }
            catch (ExternalException) { received = false; return false; }
        }

        private int Row(int i, int left, int right)
        {
            return previous[i + left] + 2 * previous[i] + previous[i + right];
        }

        private void DisposeNative()
        {
            if (magnifier != IntPtr.Zero) { DestroyWindow(magnifier); magnifier = IntPtr.Zero; }
            if (initialized) { MagUninitialize(); initialized = false; }
        }

        public void Dispose()
        {
            receiving = false;
            DisposeNative();
            small.Dispose();
            edges.Dispose();
            if (Surface != null) { Surface.Dispose(); Surface = null; }
        }
    }
}
