using System;
using System.Drawing;

namespace StillLyrics
{
    internal static class CoverColors
    {
        internal static Color? Dominant(Image image)
        {
            const int size = 48;
            int[] count = new int[512], red = new int[512], green = new int[512], blue = new int[512];
            int top = 0;
            using (var bitmap = new Bitmap(image, size, size))
            {
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (c.A < 128) continue;
                    int bin = (c.R >> 5) * 64 + (c.G >> 5) * 8 + (c.B >> 5);
                    red[bin] += c.R;
                    green[bin] += c.G;
                    blue[bin] += c.B;
                    if (++count[bin] > count[top]) top = bin;
                }
            }
            int n = count[top];
            if (n == 0) return null;
            return Color.FromArgb(red[top] / n, green[top] / n, blue[top] / n);
        }

        private static double Linear(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        internal static double Luminance(Color c)
        {
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }

        internal static Color Ink(Color background)
        {
            double l = Luminance(background) + 0.05;
            return l / 0.05 >= 1.05 / l ? Color.Black : Color.White;
        }
    }
}
