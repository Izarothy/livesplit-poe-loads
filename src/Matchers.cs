using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LiveSplit.PoELoads
{
    /// <summary>Grayscale image with float pixels, row-major.</summary>
    public sealed class Gray
    {
        public readonly int Width, Height;
        public readonly float[] Pixels;

        public Gray(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new float[width * height];
        }

        public static Gray FromPng(Stream png)
        {
            using (var bitmap = new Bitmap(png))
            {
                var image = new Gray(bitmap.Width, bitmap.Height);
                for (var y = 0; y < bitmap.Height; y++)
                    for (var x = 0; x < bitmap.Width; x++)
                        image.Pixels[y * bitmap.Width + x] = bitmap.GetPixel(x, y).R; // templates are grayscale
                return image;
            }
        }

        /// <summary>Luma of a top-down 32-bit BGRA buffer.</summary>
        public static unsafe void FromBgra(IntPtr bits, int width, int height, Gray target)
        {
            var source = (byte*)bits;
            var pixels = target.Pixels;
            for (var i = 0; i < width * height; i++, source += 4)
                pixels[i] = 0.114f * source[0] + 0.587f * source[1] + 0.299f * source[2];
        }

        /// <summary>Average factor x factor blocks (same as an exact INTER_AREA downscale).</summary>
        public Gray Shrink(int factor, Gray target = null)
        {
            var width = Width / factor;
            var height = Height / factor;
            target = target ?? new Gray(width, height);
            var scale = 1f / (factor * factor);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var sum = 0f;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var row = (y * factor + dy) * Width + x * factor;
                        for (var dx = 0; dx < factor; dx++)
                            sum += Pixels[row + dx];
                    }
                    target.Pixels[y * width + x] = sum * scale;
                }
            return target;
        }
    }

    /// <summary>Normalized cross-correlation of a template against an image.</summary>
    public sealed class Template
    {
        readonly float[] centered;
        readonly float norm;
        public readonly int Width, Height;

        public Template(Gray image)
        {
            Width = image.Width;
            Height = image.Height;
            var mean = 0f;
            foreach (var p in image.Pixels)
                mean += p;
            mean /= image.Pixels.Length;
            centered = new float[image.Pixels.Length];
            var sum = 0.0;
            for (var i = 0; i < centered.Length; i++)
            {
                centered[i] = image.Pixels[i] - mean;
                sum += centered[i] * centered[i];
            }
            norm = (float)Math.Sqrt(sum);
        }

        /// <summary>Score with the template's top-left at (x, y).</summary>
        public float ScoreAt(Gray image, int x, int y)
        {
            var n = Width * Height;
            double sum = 0, sumSquares = 0, dot = 0;
            for (var ty = 0; ty < Height; ty++)
            {
                var row = (y + ty) * image.Width + x;
                var trow = ty * Width;
                for (var tx = 0; tx < Width; tx++)
                {
                    var p = image.Pixels[row + tx];
                    sum += p;
                    sumSquares += p * p;
                    dot += centered[trow + tx] * p;
                }
            }
            var variance = sumSquares - sum * sum / n;
            return variance <= 1e-6 ? 0 : (float)(dot / (norm * Math.Sqrt(variance)));
        }

        /// <summary>Best score over x in [xMin, xMax] and every row that fits.</summary>
        public float BestScore(Gray image, int xMin, int xMax)
        {
            var best = -1f;
            xMax = Math.Min(xMax, image.Width - Width);
            for (var y = 0; y + Height <= image.Height; y++)
                for (var x = Math.Max(0, xMin); x <= xMax; x++)
                    best = Math.Max(best, ScoreAt(image, x, y));
            return best;
        }
    }

    /// <summary>
    /// The two on-screen cues the log cannot provide. Geometry and thresholds come from
    /// measurements on 1080p recordings (other resolutions are scaled by the capture).
    /// </summary>
    public static class Cues
    {
        // "Entering <area>" banner: 1080p rows 34..64, x 480..1440, matched at half resolution.
        public const int BannerWidth = 960, BannerHeight = 30, BannerTop = 34, BannerLeft = 480;
        public const float BannerThreshold = 0.5f; // live non-banner frames stay under ~0.3

        // "Contacting server..." text: 1080p y 972..1052, x 700..1140, matched at quarter resolution.
        public const int ContactWidth = 440, ContactHeight = 80, ContactTop = 972, ContactLeft = 700;
        public const float ContactThreshold = 0.7f;

        static readonly Template banner = new Template(Load("entering.png").Shrink(2));
        static readonly Template contacting = new Template(Load("contacting.png"));

        static Gray Load(string name)
        {
            using (var stream = typeof(Cues).Assembly.GetManifestResourceStream("LiveSplit.PoELoads.Templates." + name))
                return Gray.FromPng(stream);
        }

        /// <summary>Banner band at 1080p scale (960x30) -> score.</summary>
        public static float BannerScore(Gray band)
        {
            // the text is centred, so "Entering" starts left of the middle; x range in half-res pixels
            return banner.BestScore(band.Shrink(2), 20, 245);
        }

        /// <summary>Contacting region at 1080p scale (440x80) -> score.</summary>
        public static float ContactScore(Gray region) => contacting.ScoreAt(region.Shrink(4), 0, 0);
    }
}
