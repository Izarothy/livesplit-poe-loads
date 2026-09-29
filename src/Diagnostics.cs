using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveSplit.PoELoads
{
    /// <summary>
    /// Troubleshooting for "ICT is never measured": every 10 s while the timer runs, writes what the
    /// screen watcher sees (window, coordinates, DPI, what covers the banner, banner score) to
    /// PoELoads\diagnostics\report.txt, plus the captured game frame and banner band as PNGs.
    /// </summary>
    public sealed class Diagnostics
    {
        public const int ReportMs = 10000;

        readonly string folder;
        bool environmentWritten;

        public Diagnostics(string folder)
        {
            this.folder = folder;
        }

        internal void Report(IntPtr window, Capture capture, bool located, bool visible, float score)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var stamp = DateTime.Now.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
                var text = new StringBuilder();
                if (!environmentWritten)
                {
                    WriteEnvironment(text);
                    environmentWritten = true;
                }

                text.AppendLine($"[{stamp}]");
                if (window == IntPtr.Zero)
                {
                    text.AppendLine("  game window: not found (no PathOfExile process with a main window)");
                    Append(text);
                    return;
                }

                text.AppendLine($"  game window: {ProcessNameOf(window)}, dpi {WindowDpi(window)}, awareness {WindowAwareness(window)}");
                text.AppendLine($"  our thread awareness: {ThreadAwareness()}");
                if (!located)
                {
                    text.AppendLine("  client area: unavailable (minimised?)");
                    Append(text);
                    return;
                }

                text.AppendLine($"  client area as used: {Describe(capture.Client)}");
                text.AppendLine($"  client area per-monitor-aware: {PerMonitorClient(window)}");
                text.AppendLine($"  banner capture rect: {Describe(capture.BannerRect)}");
                var point = capture.BannerPoint;
                var cover = CoveringWindow(point);
                text.AppendLine(visible
                    ? "  banner point: game window on top"
                    : $"  banner point ({point.X},{point.Y}): covered by {cover}");

                if (visible)
                {
                    var band = capture.Banner();
                    var pixels = band.Pixels;
                    var mean = pixels.Average();
                    var sd = Math.Sqrt(pixels.Average(p => (p - mean) * (p - mean)));
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  band brightness mean {0:0.0} sd {1:0.0}, last banner score {2:0.00}", mean, sd, score));
                    SaveGray(band, Path.Combine(folder, $"{stamp} band.png"));
                }
                using (var frame = capture.Frame(960))
                    frame?.Save(Path.Combine(folder, $"{stamp} frame.png"), ImageFormat.Png);
                Append(text);
            }
            catch (Exception ex) // diagnostics must never stop the capture; record why they failed instead
            {
                TryAppend($"  diagnostics error: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }

        void WriteEnvironment(StringBuilder text)
        {
            text.AppendLine($"PoE Loads {typeof(Diagnostics).Assembly.GetName().Version} diagnostics, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Windows {Environment.OSVersion.Version}, 64-bit process: {Environment.Is64BitProcess}");
            text.AppendLine($"Process DPI awareness: {ProcessAwareness()}");
            text.AppendLine("Monitors:");
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data) =>
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(monitor, ref info);
                    var dpi = "?";
                    try
                    {
                        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
                            dpi = dpiX.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (EntryPointNotFoundException) { }
                    catch (DllNotFoundException) { }
                    var r = info.rcMonitor;
                    text.AppendLine($"  {r.Right - r.Left}x{r.Bottom - r.Top} at ({r.Left},{r.Top}), dpi {dpi}{((info.dwFlags & 1) != 0 ? ", primary" : "")}");
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                text.AppendLine($"  (could not list monitors: {ex.Message})");
            }
            text.AppendLine();
        }

        void Append(StringBuilder text) => File.AppendAllText(Path.Combine(folder, "report.txt"), text.ToString());

        void TryAppend(string text)
        {
            try { File.AppendAllText(Path.Combine(folder, "report.txt"), text); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Describe(Rectangle r) => $"{r.Width}x{r.Height} at ({r.X},{r.Y})";

        static string ProcessNameOf(IntPtr window)
        {
            GetWindowThreadProcessId(window, out var id);
            try
            {
                using (var process = Process.GetProcessById((int)id))
                    return process.ProcessName;
            }
            catch (ArgumentException) { return $"pid {id}"; }
        }

        static string CoveringWindow(Point point)
        {
            var root = GetAncestor(WindowFromPoint(new POINT { X = point.X, Y = point.Y }), 2);
            if (root == IntPtr.Zero)
                return "nothing";
            var name = new StringBuilder(256);
            GetClassName(root, name, name.Capacity);
            var title = new StringBuilder(256);
            GetWindowText(root, title, title.Capacity);
            return $"{ProcessNameOf(root)} (class \"{name}\", title \"{title}\")";
        }

        static string PerMonitorClient(IntPtr window)
        {
            try
            {
                var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor aware v2
                try
                {
                    GetClientRect(window, out var rect);
                    var origin = new POINT();
                    ClientToScreen(window, ref origin);
                    return $"{rect.Right}x{rect.Bottom} at ({origin.X},{origin.Y})";
                }
                finally
                {
                    SetThreadDpiAwarenessContext(previous);
                }
            }
            catch (EntryPointNotFoundException) { return "n/a (Windows too old)"; }
        }

        static string WindowDpi(IntPtr window)
        {
            try { return GetDpiForWindow(window).ToString(CultureInfo.InvariantCulture); }
            catch (EntryPointNotFoundException) { return "?"; }
        }

        static string WindowAwareness(IntPtr window)
        {
            try { return AwarenessName(GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(window))); }
            catch (EntryPointNotFoundException) { return "?"; }
        }

        static string ThreadAwareness()
        {
            try { return AwarenessName(GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext())); }
            catch (EntryPointNotFoundException) { return "?"; }
        }

        static string ProcessAwareness()
        {
            try
            {
                return GetProcessDpiAwareness(IntPtr.Zero, out var value) == 0 ? AwarenessName(value) : "?";
            }
            catch (EntryPointNotFoundException) { return "?"; }
            catch (DllNotFoundException) { return "?"; }
        }

        static string AwarenessName(int value) =>
            value == 0 ? "unaware" : value == 1 ? "system" : value == 2 ? "per-monitor" : value.ToString(CultureInfo.InvariantCulture);

        static void SaveGray(Gray image, string path)
        {
            using (var bitmap = new Bitmap(image.Width, image.Height))
            {
                for (var y = 0; y < image.Height; y++)
                    for (var x = 0; x < image.Width; x++)
                    {
                        var v = (int)Math.Max(0f, Math.Min(255f, image.Pixels[y * image.Width + x]));
                        bitmap.SetPixel(x, y, Color.FromArgb(v, v, v));
                    }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("shcore.dll")] static extern int GetProcessDpiAwareness(IntPtr process, out int value);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref POINT point);
    }
}
