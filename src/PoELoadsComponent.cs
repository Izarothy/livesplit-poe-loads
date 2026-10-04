using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

[assembly: ComponentFactory(typeof(LiveSplit.PoELoads.PoELoadsFactory))]

namespace LiveSplit.PoELoads
{
    public sealed class PoELoadsFactory : IComponentFactory
    {
        public string ComponentName => "PoE Loads";
        public string Description => "Live instance-creation and loading-screen time of the current run.";
        public ComponentCategory Category => ComponentCategory.Information;
        public IComponent Create(LiveSplitState state) => new PoELoadsComponent(state);

        public string UpdateName => ComponentName;
        public string XMLURL => "";
        public string UpdateURL => "";
        public Version Version => new Version(1, 4, 3);
    }

    /// <summary>One finished transition of the current run.</summary>
    public sealed class Load
    {
        public string Area;
        public bool IsLogin;
        public double? Ict;         // click ("Entering" / "Contacting server" onset) -> loading screen
        public double LoadScreen;   // loading screen on screen
        public float BannerBest;    // diagnostics: best banner score in the 3 s before the loading screen
        public int BannerFrames;    // diagnostics: frames watched in those 3 s (0 = game covered or minimised)
    }

    /// <summary>
    /// Four rows for the current run: load time (ICT + loading screens), the difference from the
    /// usual pace (averages of recent runs times this run's loads), ICT alone and loading screens alone. Loading screens come from Client.txt; ICT needs the
    /// on-screen cue that marks the click, so the screen is watched while the timer runs.
    /// Numbers stay on screen after a reset until the next start; every load is also written to
    /// a per-run CSV next to LiveSplit (PoELoads\*.csv).
    /// </summary>
    public sealed class PoELoadsComponent : IComponent
    {
        const double PollSeconds = 0.1;
        const string NewCharacterArea = "1_1_1"; // The Twilight Strand
        const float RowHeight = 30;
        const float SidePadding = 7;

        readonly LiveSplitState state;
        readonly Settings settings = new Settings();
        readonly List<Load> loads = new List<Load>();
        readonly ScreenWatcher watcher = new ScreenWatcher();
        ClientLog log;
        DateTime lastPoll = DateTime.MinValue;
        bool loginInProgress;
        Load current;          // generated, loading screen not finished yet
        string runFile;
        Baseline usual;
        string drawn = "";

        public PoELoadsComponent(LiveSplitState state)
        {
            this.state = state;
            log = new ClientLog(settings.ResolveLogPath());
            log.SkipToEnd();
            usual = Baseline.FromHistory(RunFolder);
            state.OnStart += OnStart;
        }

        void OnStart(object sender, EventArgs e)
        {
            loads.Clear();
            current = null;
            loginInProgress = false;
            watcher.Clear();
            log = new ClientLog(settings.ResolveLogPath()); // the game may have started since
            log.SkipToEnd();
            usual = Baseline.FromHistory(RunFolder); // before this run's file exists
            runFile = NewRunFile();
            watcher.Diagnostics = settings.WriteDiagnostics && runFile != null
                ? new Diagnostics(Path.Combine(RunFolder, "diagnostics", Path.GetFileNameWithoutExtension(runFile)))
                : null;
        }

        bool Running => state.CurrentPhase == TimerPhase.Running || state.CurrentPhase == TimerPhase.Paused;

        void Poll()
        {
            watcher.Active = Running && settings.CaptureScreen;
            if (!Running || (DateTime.UtcNow - lastPoll).TotalSeconds < PollSeconds)
                return;
            lastPoll = DateTime.UtcNow;

            foreach (var e in log.ReadNew())
            {
                switch (e.Kind)
                {
                    case LogEventKind.Logout:
                        // a logout while a loading screen is still up cuts that load short
                        // (its log line still follows): leave it out of the stats
                        current = null;
                        loginInProgress = true;
                        watcher.ContactArmed = true;
                        break;

                    case LogEventKind.Generating when e.AreaId == NewCharacterArea:
                        // a new character entering The Twilight Strand starts a run; it is not a load of one
                        current = null;
                        loginInProgress = false;
                        watcher.ContactArmed = false;
                        break;

                    case LogEventKind.Generating:
                        current = new Load { IsLogin = loginInProgress };
                        loginInProgress = false;
                        watcher.ContactArmed = false;
                        break;

                    case LogEventKind.LoadFinished when current != null && state.CurrentPhase == TimerPhase.Paused:
                        // the run is paused: this load is not part of it
                        current = null;
                        break;

                    case LogEventKind.LoadFinished when current != null:
                    {
                        // the loading screen appears at "Got Instance Details", ~0.1 s before "Generating level";
                        // its logged duration runs from there, so its start is this line's tick minus the duration
                        var loadStart = e.Tick - (long)Math.Round(e.Seconds * 1000);
                        var onset = watcher.OnsetBefore(loadStart, current.IsLogin);
                        var ict = onset.HasValue ? (loadStart - onset.Value) / 1000.0 : (double?)null;
                        var (best, frames) = watcher.BannerStats(loadStart - 3000, loadStart);
                        if (settings.SaveMissSnapshots && !current.IsLogin && ict == null && frames > 0)
                            SaveMiss(loadStart);
                        current.Ict = ict >= 0 && ict < 60 ? ict : null;
                        current.BannerBest = best;
                        current.BannerFrames = frames;
                        current.Area = e.Area;
                        current.LoadScreen = e.Seconds;
                        loads.Add(current);
                        Append(current);
                        current = null;
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ numbers

        IEnumerable<(string Label, string Value, string Detail, Color? ValueColor)> Rows()
        {
            var icts = loads.Where(l => l.Ict.HasValue).Select(l => l.Ict.Value).ToList();
            var screens = loads.Select(l => l.LoadScreen).ToList();
            var none = loads.Count == 0;
            var measured = icts.Sum() + screens.Sum();

            // Load time: a missed click is filled with this run's average ICT for its kind.
            double RunIct(bool login) =>
                loads.Where(l => l.IsLogin == login && l.Ict.HasValue).Select(l => l.Ict.Value)
                     .DefaultIfEmpty(login ? usual.LoginIct : usual.ZoneIct).Average();
            var actual = measured + loads.Where(l => !l.Ict.HasValue).Sum(l => RunIct(l.IsLogin));

            // vs usual: like for like, so a missed click moves neither side.
            var par = loads.Sum(l => l.Ict.HasValue ? usual.Par(l) : usual.Screen(l));
            var delta = measured - par;
            var layout = state.LayoutSettings;
            Color? deltaColor = Math.Abs(delta) < 0.05 ? (Color?)null
                : delta > 0 ? layout.BehindLosingTimeColor : layout.AheadGainingTimeColor;

            if (settings.ShowOnNa)
            {
                // this run's loads at NA speed: ICT where measured, and every loading screen
                var saved = loads.Where(l => l.Ict.HasValue).Sum(l => l.Ict.Value - NaIct(l))
                          + loads.Sum(l => l.LoadScreen - NaScreen(l));
                var now = (state.CurrentTime[state.CurrentTimingMethod] ?? TimeSpan.Zero).TotalSeconds;
                yield return ("on NA naively", Clock(Math.Max(0, now - saved)), "", null);
            }
            yield return ("Load time", none ? "-" : Clock(actual), "", null);
            yield return ("vs usual", none ? "-" : Signed(delta), none ? "" : Clock(par), none ? null : deltaColor);
            var missed = loads.Count - icts.Count;
            yield return (missed > 0 ? $"ICT  ({missed}?)" : "ICT", icts.Count == 0 ? "-" : Clock(icts.Sum()),
                          icts.Count == 0 ? "" : "avg " + Fixed(icts.Average()), null);
            yield return ("Load screens", none ? "-" : Clock(screens.Sum()), none ? "" : "avg " + Fixed(screens.Average()), null);
        }

        // what an NA-realm runner gets for the same kind of load: averages over imexile's
        // 118 transitions in an Exilecon 2026 qualifier race (US realm)
        const double NaIctNew = 0.51, NaIctExisting = 0.15, NaIctLogout = 0.42;
        const double NaScreenNew = 0.56, NaScreenExisting = 0.60, NaScreenLogout = 0.64;
        const double ExistingInstanceIct = 0.55; // below this a zone change reused an existing instance

        static double NaIct(Load load) =>
            load.IsLogin ? NaIctLogout : load.Ict < ExistingInstanceIct ? NaIctExisting : NaIctNew;

        static double NaScreen(Load load) =>
            load.IsLogin ? NaScreenLogout : load.Ict < ExistingInstanceIct ? NaScreenExisting : NaScreenNew;

        static string Signed(double seconds) =>
            (seconds >= 0 ? "+" : "-") + Math.Abs(seconds).ToString("0.0", CultureInfo.InvariantCulture);

        static string Clock(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 100}";
            return span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}"
                : seconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        static string Fixed(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ per-run file

        static string RunFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PoELoads");

        static string NewRunFile()
        {
            try
            {
                Directory.CreateDirectory(RunFolder);
                var file = Path.Combine(RunFolder, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".csv");
                File.WriteAllText(file, "n,kind,area,ict_s,loadscreen_s,banner_best,banner_frames,ict_to\r\n");
                return file;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Keep the best banner frame of a missed click, to see why it was missed.</summary>
        void SaveMiss(long generateTick)
        {
            if (runFile == null)
                return;
            try
            {
                var folder = Path.Combine(RunFolder, "misses");
                Directory.CreateDirectory(folder);
                var name = $"{Path.GetFileNameWithoutExtension(runFile)} load {loads.Count + 1}.png";
                watcher.SaveBestBanner(generateTick - 3000, generateTick, Path.Combine(folder, name));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }

        void Append(Load load)
        {
            if (runFile == null)
                return;
            var ict = load.Ict?.ToString("0.000", CultureInfo.InvariantCulture) ?? "";
            var line = string.Join(",", loads.Count, load.IsLogin ? "login" : "zone", "\"" + load.Area.Replace("\"", "\"\"") + "\"",
                                   ict, load.LoadScreen.ToString("0.000", CultureInfo.InvariantCulture),
                                   float.IsNaN(load.BannerBest) ? "" : load.BannerBest.ToString("0.00", CultureInfo.InvariantCulture),
                                   load.BannerFrames, "loadscreen");
            try { File.AppendAllText(runFile, line + "\r\n"); }
            catch (IOException) { }
        }

        // ------------------------------------------------------------------ IComponent

        public string ComponentName => "PoE Loads";
        int RowCount => settings.ShowOnNa ? 5 : 4;
        public float VerticalHeight => RowHeight * RowCount;
        public float MinimumWidth => 120;
        public float HorizontalWidth => 190 * RowCount;
        public float MinimumHeight => RowHeight;
        public float PaddingTop => 0;
        public float PaddingBottom => 0;
        public float PaddingLeft => SidePadding;
        public float PaddingRight => SidePadding;
        public IDictionary<string, Action> ContextMenuControls => null;

        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            Poll();
            var text = string.Concat(Rows().Select(r => r.Value + r.Detail));
            if (text != drawn)
            {
                drawn = text;
                invalidator?.Invalidate(0, 0, width, height);
            }
        }

        public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
        {
            var layout = state.LayoutSettings;
            var detailColumn = DetailColumnWidth(g, layout.TimesFont);
            var y = 0f;
            foreach (var row in Rows())
            {
                var box = new RectangleF(SidePadding, y, width - 2 * SidePadding, RowHeight);
                if (row.Label == "on NA naively")
                {
                    // a second timer: full width, set off from the load stats by a separator
                    DrawRow(g, state, box, row, 0);
                    using (var pen = new Pen(layout.SeparatorsColor, 1))
                        g.DrawLine(pen, 0, y + RowHeight - 1, width, y + RowHeight - 1);
                }
                else
                {
                    DrawRow(g, state, box, row, detailColumn);
                }
                y += RowHeight;
            }
        }

        public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
        {
            var detailColumn = DetailColumnWidth(g, state.LayoutSettings.TimesFont);
            var x = 0f;
            foreach (var row in Rows())
            {
                DrawRow(g, state, new RectangleF(x + SidePadding, 0, 190 - 2 * SidePadding, height), row, row.Detail.Length > 0 ? detailColumn : 0);
                x += 190;
            }
        }

        /// <summary>Room for the muted right column ("avg 0.00", "12:34.5") so every value lines up.</summary>
        static float DetailColumnWidth(Graphics g, Font font) =>
            Math.Max(g.MeasureString("avg 0.00", font, PointF.Empty, StringFormat.GenericTypographic).Width,
                     g.MeasureString("00:00.0", font, PointF.Empty, StringFormat.GenericTypographic).Width) + 14;

        static void DrawRow(Graphics g, LiveSplitState state, RectangleF box,
                            (string Label, string Value, string Detail, Color? ValueColor) row, float detailColumn)
        {
            var layout = state.LayoutSettings;
            g.TextRenderingHint = layout.AntiAliasing ? TextRenderingHint.AntiAlias : TextRenderingHint.SingleBitPerPixel;
            using (var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var muted = new SolidBrush(Color.FromArgb(200, layout.TextColor)))
            using (var bright = new SolidBrush(row.ValueColor ?? layout.TextColor))
            {
                g.DrawString(row.Label, layout.TextFont, muted, box, left);
                if (row.Detail.Length > 0)
                    g.DrawString(row.Detail, layout.TimesFont, muted, box, right);
                g.DrawString(row.Value, layout.TimesFont, bright, new RectangleF(box.X, box.Y, box.Width - detailColumn, box.Height), right);
            }
        }

        public Control GetSettingsControl(LayoutMode mode) => new SettingsControl(settings, ApplySettings);

        public XmlNode GetSettings(XmlDocument document) => settings.ToXml(document);

        public void SetSettings(XmlNode node)
        {
            settings.FromXml(node);
            ApplySettings();
        }

        void ApplySettings()
        {
            log = new ClientLog(settings.ResolveLogPath());
            log.SkipToEnd();
        }

        public void Dispose()
        {
            state.OnStart -= OnStart;
            watcher.Dispose();
        }
    }
}
