using System;
using System.Collections.Generic;
using System.Linq;

namespace SpotiBee
{
    /// <summary>
    /// Lightweight logging to MusicBee's trace/error log plus an in-memory history for
    /// the "Save Diagnostics Report" command. Never throws.
    /// </summary>
    public static class Diagnostics
    {
        private const int MaxLines = 300;
        private static readonly Queue<string> Recent = new Queue<string>();
        private static readonly object Sync = new object();

        /// <summary>Where lines go besides the history; set by the plugin to MB_Trace.</summary>
        public static Action<string> Sink { get; set; }

        public static void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            lock (Sync)
            {
                Recent.Enqueue(line);
                while (Recent.Count > MaxLines)
                    Recent.Dequeue();
            }
            try { Sink?.Invoke("SpotiBee: " + message); }
            catch { /* logging must never break the caller */ }
        }

        public static void Log(string context, Exception ex) => Log(context + " failed: " + ex);

        public static IReadOnlyList<string> History
        {
            get
            {
                lock (Sync)
                    return Recent.ToList();
            }
        }
    }
}
