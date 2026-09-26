using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiveSplit.PoELoads
{
    /// <summary>
    /// "Usual" cost of one load, per kind, from the most recent saved runs (PoELoads\*.csv).
    /// Falls back to values measured on recordings until enough runs exist.
    /// </summary>
    public sealed class Baseline
    {
        const int RecentRuns = 10;
        const int MinimumLoadsPerRun = 5;

        static readonly Regex Row = new Regex(@"^\d+,(zone|login),"".*"",([\d.]*),([\d.]+)(,.*)?$", RegexOptions.Compiled);

        public double ZoneIct = 0.70, ZoneScreen = 1.20;
        public double LoginIct = 0.65, LoginScreen = 1.45;
        public int Runs;

        public double Par(Load load) => load.IsLogin ? LoginIct + LoginScreen : ZoneIct + ZoneScreen;
        public double Screen(Load load) => load.IsLogin ? LoginScreen : ZoneScreen;

        public static Baseline FromHistory(string folder)
        {
            var baseline = new Baseline();
            if (!Directory.Exists(folder))
                return baseline;

            var runs = Directory.GetFiles(folder, "*.csv")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Select(ReadRun)
                .Where(run => run.Count >= MinimumLoadsPerRun)
                .Take(RecentRuns)
                .ToList();
            var rows = runs.SelectMany(run => run).ToList();
            baseline.Runs = runs.Count;

            void Apply(string kind, ref double ict, ref double screen)
            {
                var group = rows.Where(r => r.Kind == kind).ToList();
                var icts = group.Where(r => r.Ict.HasValue).Select(r => r.Ict.Value).ToList();
                if (icts.Count > 0)
                    ict = icts.Average();
                if (group.Count > 0)
                    screen = group.Average(r => r.Screen);
            }

            Apply("zone", ref baseline.ZoneIct, ref baseline.ZoneScreen);
            Apply("login", ref baseline.LoginIct, ref baseline.LoginScreen);
            return baseline;
        }

        static List<(string Kind, double? Ict, double Screen)> ReadRun(string file)
        {
            var rows = new List<(string, double?, double)>();
            try
            {
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var match = Row.Match(line.Trim());
                    if (!match.Success)
                        continue;
                    var ict = match.Groups[2].Value.Length > 0
                        ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                        : (double?)null;
                    rows.Add((match.Groups[1].Value, ict, double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)));
                }
            }
            catch (IOException) { }
            return rows;
        }
    }
}
