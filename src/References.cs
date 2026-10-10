using System.Linq;

namespace LiveSplit.PoELoads
{
    /// <summary>
    /// What another setup gets for the same kind of load: average ICT and loading screen per load kind,
    /// measured from race footage. Drives the optional "on X naively" timer row.
    /// </summary>
    sealed class Reference
    {
        public string Name;     // shown in the settings list
        public string Label;    // the timer row's label
        public string Source;   // where the numbers come from
        public double IctNew, IctExisting, IctLogout;
        public double ScreenNew, ScreenExisting, ScreenLogout;

        public static readonly Reference[] All =
        {
            new Reference
            {
                Name = "NA", Label = "on NA naively",
                Source = "imexile, 118 transitions, ExileCon 2026 qualifier race (US realm)",
                IctNew = 0.51, IctExisting = 0.15, IctLogout = 0.42,
                ScreenNew = 0.56, ScreenExisting = 0.60, ScreenLogout = 0.64,
            },
            new Reference
            {
                Name = "NA Texas", Label = "on Texas naively",
                Source = "tytykiller, 102 transitions, ExileCon 2026 qualifier #4 (Texas realm)",
                IctNew = 0.40, IctExisting = 0.08, IctLogout = 0.11,
                ScreenNew = 0.90, ScreenExisting = 0.96, ScreenLogout = 1.18,
            },
            new Reference
            {
                Name = "NZ LAN", Label = "on NZ LAN naively",
                Source = "imexile, 83 transitions, ExileCon 2023 LAN race (official stream)",
                IctNew = 0.11, IctExisting = 0.04, IctLogout = 0.03,
                ScreenNew = 0.74, ScreenExisting = 0.46, ScreenLogout = 0.82,
            },
        };

        /// <summary>The reference with this name, or the first one for an unknown or empty name.</summary>
        public static Reference Find(string name) => All.FirstOrDefault(r => r.Name == name) ?? All[0];
    }
}
