using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.PoELoads
{
    public sealed class Settings
    {
        static readonly string[] KnownLogPaths =
        {
            @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile\logs\Client.txt",
            @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile\logs\Client.txt",
            @"C:\Program Files\Steam\steamapps\common\Path of Exile\logs\Client.txt",
            @"C:\Program Files\Epic Games\PathOfExile\logs\Client.txt",
        };

        public string LogPath = "";           // empty: find it automatically
        public bool CaptureScreen = true;
        public bool SaveMissSnapshots = false; // debugging aid: keep the banner frame of each missed click
        public bool ShowOnNa = false;          // extra row: the timer as it would read with a reference setup's loads
        public string Reference = "NA";        // which reference that row uses (References.cs)
        public bool WriteDiagnostics = false;  // troubleshooting: report what the screen watcher sees (PoELoads\diagnostics)

        /// <summary>The configured Client.txt, or the running game's, or a default install location.</summary>
        public string ResolveLogPath()
        {
            if (!string.IsNullOrWhiteSpace(LogPath))
                return LogPath;
            foreach (var process in Process.GetProcesses().Where(p => p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase)))
                using (process)
                {
                    try
                    {
                        var log = Path.Combine(Path.GetDirectoryName(process.MainModule.FileName), "logs", "Client.txt");
                        if (File.Exists(log))
                            return log;
                    }
                    catch (Win32Exception) { }          // no access to that process
                    catch (InvalidOperationException) { } // it exited meanwhile
                }
            return KnownLogPaths.FirstOrDefault(File.Exists) ?? KnownLogPaths[0];
        }

        public XmlNode ToXml(XmlDocument document)
        {
            var root = document.CreateElement("Settings");
            Add(document, root, "Version", "1.5");
            Add(document, root, "LogPath", LogPath);
            Add(document, root, "CaptureScreen", CaptureScreen.ToString());
            Add(document, root, "SaveMissSnapshots", SaveMissSnapshots.ToString());
            Add(document, root, "ShowOnNa", ShowOnNa.ToString());
            Add(document, root, "Reference", Reference);
            Add(document, root, "WriteDiagnostics", WriteDiagnostics.ToString());
            return root;
        }

        public void FromXml(XmlNode node)
        {
            LogPath = node["LogPath"]?.InnerText ?? "";
            if (bool.TryParse(node["CaptureScreen"]?.InnerText, out var capture))
                CaptureScreen = capture;
            if (bool.TryParse(node["SaveMissSnapshots"]?.InnerText, out var snapshots))
                SaveMissSnapshots = snapshots;
            if (bool.TryParse(node["ShowOnNa"]?.InnerText, out var onNa))
                ShowOnNa = onNa;
            Reference = node["Reference"]?.InnerText ?? "NA";
            if (bool.TryParse(node["WriteDiagnostics"]?.InnerText, out var diagnostics))
                WriteDiagnostics = diagnostics;
        }

        static void Add(XmlDocument document, XmlElement parent, string name, string value)
        {
            var element = document.CreateElement(name);
            element.InnerText = value;
            parent.AppendChild(element);
        }
    }

    sealed class SettingsControl : UserControl
    {
        public SettingsControl(Settings settings, Action changed)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Padding = new Padding(7) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var path = new TextBox { Text = settings.LogPath, Dock = DockStyle.Fill };
            path.TextChanged += (s, e) => { settings.LogPath = path.Text; changed(); };
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "Client.txt|Client.txt|All files|*.*", FileName = settings.ResolveLogPath() })
                    if (dialog.ShowDialog() == DialogResult.OK)
                        path.Text = dialog.FileName;
            };

            var capture = new CheckBox { Text = "Measure ICT from the screen", Checked = settings.CaptureScreen, AutoSize = true };
            capture.CheckedChanged += (s, e) => settings.CaptureScreen = capture.Checked;
            var snapshots = new CheckBox { Text = "Save a snapshot of every missed click (PoELoads\\misses)", Checked = settings.SaveMissSnapshots, AutoSize = true };
            snapshots.CheckedChanged += (s, e) => settings.SaveMissSnapshots = snapshots.Checked;

            layout.Controls.Add(new Label { Text = "Client.txt:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(path, 1, 0);
            layout.Controls.Add(browse, 2, 0);
            layout.Controls.Add(new Label { Text = "(empty = find automatically)", AutoSize = true, ForeColor = SystemColors.GrayText }, 1, 1);
            layout.Controls.Add(capture, 1, 2);
            layout.Controls.Add(snapshots, 1, 3);
            var onNaBox = new CheckBox { Text = "Show the timer with the loads of:", Checked = settings.ShowOnNa, AutoSize = true, Anchor = AnchorStyles.Left };
            onNaBox.CheckedChanged += (s, e) => settings.ShowOnNa = onNaBox.Checked;
            var reference = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            reference.Items.AddRange(Reference.All.Select(r => (object)r.Name).ToArray());
            reference.SelectedItem = Reference.Find(settings.Reference).Name;
            reference.SelectedIndexChanged += (s, e) => settings.Reference = (string)reference.SelectedItem;
            var source = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };
            void ShowSource() => source.Text = Reference.Find(settings.Reference).Source;
            reference.SelectedIndexChanged += (s, e) => ShowSource();
            ShowSource();
            var onNaRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            onNaRow.Controls.Add(onNaBox);
            onNaRow.Controls.Add(reference);
            layout.Controls.Add(onNaRow, 1, 4);
            layout.Controls.Add(source, 1, 5);
            var diagnosticsBox = new CheckBox { Text = "Write diagnostics while the timer runs (PoELoads\\diagnostics)", Checked = settings.WriteDiagnostics, AutoSize = true };
            diagnosticsBox.CheckedChanged += (s, e) => settings.WriteDiagnostics = diagnosticsBox.Checked;
            layout.Controls.Add(diagnosticsBox, 1, 6);

            Controls.Add(layout);
            Size = new Size(460, 210);
        }
    }
}
