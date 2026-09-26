using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveSplit.PoELoads
{
    public enum LogEventKind
    {
        Logout,      // abnormal disconnect or login-server connect: a login flow has started
        Generating,  // instance ready, loading screen starts (tick = its start)
        LoadFinished // loading screen gone (Seconds = its duration)
    }

    public sealed class LogEvent
    {
        public LogEventKind Kind;
        public long Tick;       // Client.txt millisecond tick (system uptime)
        public string Area;     // LoadFinished only
        public double Seconds;  // LoadFinished only
    }

    /// <summary>
    /// Follows Client.txt from a given point and turns new lines into transition events.
    /// </summary>
    public sealed class ClientLog
    {
        static readonly Regex Line = new Regex(@"^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d (\d+) \S+ \[\w+ Client \d+\] (.*)$", RegexOptions.Compiled);
        static readonly Regex Generating = new Regex(@"^Generating level \d+ area ""[^""]+""", RegexOptions.Compiled);
        static readonly Regex LoadingScreen = new Regex(@"^\[LOADING SCREEN\] \((.*)\) Duration = ([\d.]+) seconds", RegexOptions.Compiled);

        readonly string path;
        long position;
        string partial = "";

        public ClientLog(string path)
        {
            this.path = path;
        }

        public bool Exists => File.Exists(path);

        /// <summary>Ignore everything written so far.</summary>
        public void SkipToEnd()
        {
            position = Exists ? new FileInfo(path).Length : 0;
            partial = "";
        }

        public List<LogEvent> ReadNew()
        {
            var events = new List<LogEvent>();
            if (!Exists)
                return events;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < position)
                    position = 0; // log was truncated or replaced
                if (stream.Length == position)
                    return events;

                stream.Seek(position, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - position];
                var read = stream.Read(buffer, 0, buffer.Length);
                position += read;

                var lines = (partial + Encoding.UTF8.GetString(buffer, 0, read)).Split('\n');
                partial = lines[lines.Length - 1];
                for (var i = 0; i < lines.Length - 1; i++)
                {
                    var parsed = Parse(lines[i].TrimEnd('\r'));
                    if (parsed != null)
                        events.Add(parsed);
                }
            }
            return events;
        }

        static LogEvent Parse(string raw)
        {
            var match = Line.Match(raw);
            if (!match.Success)
                return null;
            var tick = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var message = match.Groups[2].Value;

            if (message.StartsWith("Abnormal disconnect") || message.StartsWith("Async connecting to"))
                return new LogEvent { Kind = LogEventKind.Logout, Tick = tick };
            if (Generating.IsMatch(message))
                return new LogEvent { Kind = LogEventKind.Generating, Tick = tick };
            var loading = LoadingScreen.Match(message);
            if (loading.Success)
                return new LogEvent
                {
                    Kind = LogEventKind.LoadFinished,
                    Tick = tick,
                    Area = loading.Groups[1].Value,
                    Seconds = double.Parse(loading.Groups[2].Value, CultureInfo.InvariantCulture),
                };
            return null;
        }
    }
}
