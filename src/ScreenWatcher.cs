using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveSplit.PoELoads
{
    /// <summary>A continuous stretch of frames where a cue was visible (ticks = system uptime ms).</summary>
    public sealed class Episode
    {
        public long Onset;
        public long LastSeen;
    }

    /// <summary>
    /// Watches the Path of Exile window for the "Entering" banner (always) and the
    /// "Contacting server" text (only while a login is in progress), ~60 times a second.
    /// Only a 960x30 band (plus a 440x80 box during logins) is copied from the screen.
    /// </summary>
    public sealed class ScreenWatcher : IDisposable
    {
        const int FrameMs = 16;
        const int GapMs = 400;        // a cue missing for longer than this ends its episode (background flicker)
        const int KeepEpisodes = 32;
        const int KeepSamples = 600;  // ~10 s of banner scores, for diagnostics
        const int KeepImages = 480;   // ~8 s of banner frames, to save the best one when a click is missed

        static readonly string[] ProcessNames =
        {
            "PathOfExile", "PathOfExile_x64", "PathOfExileSteam", "PathOfExile_x64Steam",
            "PathOfExileEGS", "PathOfExile_x64EGS", "PathOfExile_KG", "PathOfExile_x64_KG",
        };

        readonly object sync = new object();
        readonly List<Episode> banners = new List<Episode>();
        readonly List<Episode> contacts = new List<Episode>();
        readonly Queue<(long Tick, float Score)> samples = new Queue<(long, float)>();
        readonly Queue<(long Tick, byte[] Pixels)> images = new Queue<(long, byte[])>();
        readonly Func<IntPtr> findWindow;
        readonly bool requireVisible;
        Thread thread;
        volatile bool active;
        volatile bool contactArmed;
        volatile bool disposed;

        /// <param name="findWindow">Window to watch (default: the Path of Exile client).</param>
        /// <param name="requireVisible">Skip frames while another window covers the banner (alt-tab).
        /// The game does not need focus: typing on another monitor is fine.</param>
        public ScreenWatcher(Func<IntPtr> findWindow = null, bool requireVisible = true)
        {
            this.findWindow = findWindow ?? FindPoeWindow;
            this.requireVisible = requireVisible;
        }

        /// <summary>Capture only while active (the timer is running).</summary>
        public bool Active
        {
            get => active;
            set
            {
                active = value;
                if (value && thread == null)
                {
                    thread = new Thread(Run) { IsBackground = true, Name = "PoE Loads capture", Priority = ThreadPriority.AboveNormal };
                    thread.Start();
                }
            }
        }

        /// <summary>Write a troubleshooting report while capturing (null = off).</summary>
        public volatile Diagnostics Diagnostics;

        /// <summary>A login is in progress: also watch for "Contacting server".</summary>
        public bool ContactArmed
        {
            get => contactArmed;
            set => contactArmed = value;
        }

        public void Clear()
        {
            lock (sync)
            {
                banners.Clear();
                contacts.Clear();
                Cues.ResetFont();
                samples.Clear();
                images.Clear();
            }
        }

        /// <summary>
        /// Onset of the cue episode that was still visible when the loading screen started
        /// (the cue disappears as the loading screen appears).
        /// </summary>
        public long? OnsetBefore(long loadStartTick, bool login)
        {
            lock (sync)
            {
                var episodes = login ? contacts : banners;
                var tolerance = login ? 1500 : 800;
                var match = episodes.LastOrDefault(e => e.Onset <= loadStartTick
                                                       && e.LastSeen >= loadStartTick - tolerance
                                                       && loadStartTick - e.Onset <= 60000);
                return match?.Onset;
            }
        }

        /// <summary>Best banner score and number of frames watched in [from, to] (diagnostics).</summary>
        public (float Best, int Frames) BannerStats(long from, long to)
        {
            lock (sync)
            {
                var inRange = samples.Where(x => x.Tick >= from && x.Tick <= to).ToList();
                return (inRange.Count == 0 ? float.NaN : inRange.Max(x => x.Score), inRange.Count);
            }
        }

        /// <summary>Save the best-matching banner frame in [from, to] as a PNG; false if none is kept.</summary>
        public bool SaveBestBanner(long from, long to, string path)
        {
            byte[] pixels;
            lock (sync)
            {
                var best = samples.Where(x => x.Tick >= from && x.Tick <= to)
                                  .OrderByDescending(x => x.Score).Select(x => (long?)x.Tick).FirstOrDefault();
                pixels = best == null ? null : images.Where(x => x.Tick == best.Value).Select(x => x.Pixels).FirstOrDefault();
            }
            if (pixels == null)
                return false;
            using (var bitmap = new System.Drawing.Bitmap(Cues.BannerWidth, Cues.BannerHeight))
            {
                for (var y = 0; y < Cues.BannerHeight; y++)
                    for (var x = 0; x < Cues.BannerWidth; x++)
                    {
                        var v = pixels[y * Cues.BannerWidth + x];
                        bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(v, v, v));
                    }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return true;
        }

        /// <summary>Snapshot of recent banner episodes (diagnostics).</summary>
        public Episode[] BannerEpisodes()
        {
            lock (sync)
                return banners.Select(e => new Episode { Onset = e.Onset, LastSeen = e.LastSeen }).ToArray();
        }

        static long Now() => (uint)Environment.TickCount; // same clock as Client.txt ticks

        void Run()
        {
            TimeBeginPeriod(1);
            try
            {
                // With display scaling, a DPI-unaware LiveSplit would get scaled-down window coordinates
                // while the screen copy is in real pixels. Work in real pixels on this thread.
                try { SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
                catch (EntryPointNotFoundException) { } // before Windows 10 1607

                using (var capture = new Capture())
                {
                    var window = IntPtr.Zero;
                    var lastLookup = 0L;
                    var lastReport = -(long)PoELoads.Diagnostics.ReportMs;
                    var lastScore = float.NaN;
                    var clock = Stopwatch.StartNew();
                    var next = 0L;
                    while (!disposed)
                    {
                        next += FrameMs;
                        var wait = next - clock.ElapsedMilliseconds;
                        if (wait > 0)
                            Thread.Sleep((int)wait);
                        else
                            next = clock.ElapsedMilliseconds; // fell behind: don't try to catch up

                        if (!active)
                        {
                            Thread.Sleep(100);
                            next = clock.ElapsedMilliseconds;
                            continue;
                        }
                        if (window == IntPtr.Zero || clock.ElapsedMilliseconds - lastLookup > 2000)
                        {
                            window = findWindow();
                            lastLookup = clock.ElapsedMilliseconds;
                        }
                        var located = window != IntPtr.Zero && capture.Locate(window);
                        var visible = located && (!requireVisible || capture.BannerVisible(window));
                        var diagnostics = Diagnostics;
                        if (diagnostics != null && clock.ElapsedMilliseconds - lastReport >= PoELoads.Diagnostics.ReportMs)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            diagnostics.Report(window, capture, located, visible, lastScore);
                        }
                        if (!visible)
                            continue;

                        var now = Now();
                        var band = capture.Banner();
                        var score = Cues.BannerScore(band);
                        lastScore = score;
                        Track(banners, score, Cues.BannerThreshold, Cues.BannerKeepThreshold, now);
                        var pixels = new byte[band.Pixels.Length];
                        for (var i = 0; i < pixels.Length; i++)
                            pixels[i] = (byte)Math.Min(255f, band.Pixels[i]);
                        lock (sync)
                        {
                            samples.Enqueue((now, score));
                            if (samples.Count > KeepSamples)
                                samples.Dequeue();
                            images.Enqueue((now, pixels));
                            if (images.Count > KeepImages)
                                images.Dequeue();
                        }
                        if (contactArmed)
                            Track(contacts, Cues.ContactScore(capture.Contact()), Cues.ContactThreshold, Cues.ContactThreshold, now);
                    }
                }
            }
            finally
            {
                TimeEndPeriod(1);
            }
        }

        /// <summary>Start an episode at `start`; keep an ongoing one alive down to `keep` (hysteresis).</summary>
        void Track(List<Episode> episodes, float score, float start, float keep, long now)
        {
            lock (sync)
            {
                var last = episodes.Count > 0 ? episodes[episodes.Count - 1] : null;
                var ongoing = last != null && now - last.LastSeen <= GapMs;
                if (score < (ongoing ? keep : start))
                    return;
                if (ongoing)
                {
                    last.LastSeen = now;
                    return;
                }
                episodes.Add(new Episode { Onset = now, LastSeen = now });
                if (episodes.Count > KeepEpisodes)
                    episodes.RemoveAt(0);
            }
        }

        static IntPtr FindPoeWindow()
        {
            foreach (var name in ProcessNames)
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                        if (process.MainWindowHandle != IntPtr.Zero)
                            return process.MainWindowHandle;
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            disposed = true;
            thread?.Join(500);
        }

        static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] static extern uint TimeEndPeriod(uint ms);
    }

    /// <summary>GDI copies of the two cue regions, scaled to their 1080p size.</summary>
    sealed class Capture : IDisposable
    {
        readonly IntPtr screen = GetDC(IntPtr.Zero);
        readonly Region banner = new Region(Cues.BannerWidth, Cues.BannerHeight);
        readonly Region contact = new Region(Cues.ContactWidth, Cues.ContactHeight);
        int left, top, width, height;

        /// <summary>Find the game's client area on screen; false when minimised.</summary>
        public bool Locate(IntPtr window)
        {
            if (!GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
                return false;
            var origin = new POINT();
            ClientToScreen(window, ref origin);
            left = origin.X;
            top = origin.Y;
            width = rect.Right;
            height = rect.Bottom;
            return true;
        }

        public Gray Banner() => Grab(banner, Cues.BannerLeft, Cues.BannerTop);

        /// <summary>True when the game window, not something on top of it, is at the banner's position.</summary>
        public bool BannerVisible(IntPtr window)
        {
            var point = BannerPoint;
            return GetAncestor(WindowFromPoint(new POINT { X = point.X, Y = point.Y }), GA_ROOT) == window;
        }

        public System.Drawing.Point BannerPoint => new System.Drawing.Point(
            left + (Cues.BannerLeft + Cues.BannerWidth / 2) * width / 1920,
            top + (Cues.BannerTop + Cues.BannerHeight / 2) * height / 1080);

        public System.Drawing.Rectangle Client => new System.Drawing.Rectangle(left, top, width, height);

        public System.Drawing.Rectangle BannerRect => ScreenRect(banner, Cues.BannerLeft, Cues.BannerTop);

        public Gray Contact() => Grab(contact, Cues.ContactLeft, Cues.ContactTop);

        /// <summary>The whole client area as currently captured, scaled to at most `maxWidth` wide (diagnostics).</summary>
        public System.Drawing.Bitmap Frame(int maxWidth)
        {
            if (width <= 0 || height <= 0)
                return null;
            var w = Math.Min(maxWidth, width);
            var h = Math.Max(1, height * w / width);
            using (var region = new Region(w, h))
            {
                StretchBlt(region.Dc, 0, 0, w, h, screen, left, top, width, height, SRCCOPY);
                var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                var bytes = new byte[w * h * 4];
                Marshal.Copy(region.Bits, bytes, 0, bytes.Length);
                for (var row = 0; row < h; row++)
                    Marshal.Copy(bytes, row * w * 4, data.Scan0 + row * data.Stride, w * 4);
                bitmap.UnlockBits(data);
                return bitmap;
            }
        }

        // 1080p reference coordinates, scaled to the actual client size (16:9 assumed, as in the video tool)
        System.Drawing.Rectangle ScreenRect(Region region, int left1080, int top1080) => new System.Drawing.Rectangle(
            left + left1080 * width / 1920,
            top + top1080 * height / 1080,
            region.Width * width / 1920,
            region.Height * height / 1080);

        Gray Grab(Region region, int left1080, int top1080)
        {
            var r = ScreenRect(region, left1080, top1080);
            if (r.Width == region.Width && r.Height == region.Height)
                BitBlt(region.Dc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY);
            else
                StretchBlt(region.Dc, 0, 0, region.Width, region.Height, screen, r.X, r.Y, r.Width, r.Height, SRCCOPY);
            Gray.FromBgra(region.Bits, region.Width, region.Height, region.Image);
            return region.Image;
        }

        public void Dispose()
        {
            banner.Dispose();
            contact.Dispose();
            ReleaseDC(IntPtr.Zero, screen);
        }

        sealed class Region : IDisposable
        {
            public readonly int Width, Height;
            public readonly IntPtr Dc, Bits;
            public readonly Gray Image;
            readonly IntPtr bitmap;

            public Region(int width, int height)
            {
                Width = width;
                Height = height;
                Image = new Gray(width, height);
                var info = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
                Dc = CreateCompatibleDC(IntPtr.Zero);
                bitmap = CreateDIBSection(Dc, ref info, 0, out Bits, IntPtr.Zero, 0);
                SelectObject(Dc, bitmap);
                SetStretchBltMode(Dc, HALFTONE);
            }

            public void Dispose()
            {
                DeleteDC(Dc);
                DeleteObject(bitmap);
            }
        }

        const uint SRCCOPY = 0x00CC0020;
        const uint GA_ROOT = 2;
        const int HALFTONE = 4;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref POINT point);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, uint rop);
    }
}
