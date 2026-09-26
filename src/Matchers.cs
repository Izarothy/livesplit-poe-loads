using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

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
            xMin = Math.Max(0, xMin);
            xMax = Math.Min(xMax, image.Width - Width);
            var w = image.Width;
            var pixels = image.Pixels;

            // integral images of the pixels and their squares: window sums in O(1)
            var stride = w + 1;
            var sum = new double[stride * (image.Height + 1)];
            var squares = new double[sum.Length];
            for (var y = 0; y < image.Height; y++)
            {
                double rowSum = 0, rowSquares = 0;
                for (var x = 0; x < w; x++)
                {
                    var p = pixels[y * w + x];
                    rowSum += p;
                    rowSquares += p * p;
                    sum[(y + 1) * stride + x + 1] = sum[y * stride + x + 1] + rowSum;
                    squares[(y + 1) * stride + x + 1] = squares[y * stride + x + 1] + rowSquares;
                }
            }

            var n = Width * Height;
            var best = -1f;
            for (var y = 0; y + Height <= image.Height; y++)
                for (var x = xMin; x <= xMax; x++)
                {
                    int a = y * stride + x, b = a + Width, c = (y + Height) * stride + x, d = c + Width;
                    var windowSum = sum[d] - sum[b] - sum[c] + sum[a];
                    var variance = squares[d] - squares[b] - squares[c] + squares[a] - windowSum * windowSum / n;
                    if (variance <= 1e-6)
                        continue;
                    var dot = 0f;
                    for (var ty = 0; ty < Height; ty++)
                    {
                        var row = (y + ty) * w + x;
                        var trow = ty * Width;
                        for (var tx = 0; tx < Width; tx++)
                            dot += centered[trow + tx] * pixels[row + tx];
                    }
                    var score = (float)(dot / (norm * Math.Sqrt(variance)));
                    if (score > best)
                        best = score;
                }
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
        public const float BannerThreshold = 0.5f;      // starts a detection; live non-banner frames stay under ~0.3
        public const float BannerKeepThreshold = 0.35f; // keeps one going when the game background flickers behind the text

        // "Contacting server..." box: 1080p y 972..1056, x 700..1180, matched at quarter resolution.
        public const int ContactWidth = 480, ContactHeight = 84, ContactTop = 972, ContactLeft = 700;
        public const float ContactThreshold = 0.7f;

        // the Bahnschrift UI font and the default font
        static readonly Template[] banners =
        {
            new Template(Load("entering.png").Shrink(2)),
            new Template(Load("entering_fontin.png").Shrink(2)),
        };
        static readonly Template contacting = new Template(Load("contacting.png"));              // whole text
        static readonly Template contactingFontin = new Template(Load("contacting_fontin.png")); // right end of the box

        static Gray Load(string name)
        {
            using (var stream = typeof(Cues).Assembly.GetManifestResourceStream("LiveSplit.PoELoads.Templates." + name))
                return Gray.FromPng(stream);
        }

        const float FontLockScore = 0.75f;
        static volatile int font = -1; // index into banners once the player's UI font is known

        /// <summary>Forget the detected UI font (e.g. at the start of a run).</summary>
        public static void ResetFont() => font = -1;

        /// <summary>Banner band at 1080p scale (960x30) -> score.</summary>
        public static float BannerScore(Gray band)
        {
            // the text is centred, so "Entering" starts left of the middle; x range in half-res pixels
            var half = band.Shrink(2);
            if (font >= 0)
                return banners[font].BestScore(half, 20, 245);
            var scores = banners.Select(t => t.BestScore(half, 20, 245)).ToArray();
            var best = Array.IndexOf(scores, scores.Max());
            if (scores[best] >= FontLockScore)
                font = best; // only check the font the player uses from now on
            return scores[best];
        }

        /// <summary>Contacting region at 1080p scale (480x84) -> score.</summary>
        public static float ContactScore(Gray region)
        {
            var quarter = region.Shrink(4);
            return Math.Max(contacting.ScoreAt(quarter, 0, 0), contactingFontin.ScoreAt(quarter, 63, 2));
        }
    }
}
