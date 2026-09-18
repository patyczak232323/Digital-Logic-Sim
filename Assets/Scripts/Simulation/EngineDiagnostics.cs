using System;

namespace DLS.Simulation
{
    internal static class EngineDiagnostics
    {
        static readonly object sync = new();
        static string latestEvent = "none";

        public static string LatestEvent
        {
            get
            {
                lock (sync) return latestEvent;
            }
        }

        public static void Record(string message)
        {
            lock (sync)
            {
                latestEvent = string.IsNullOrWhiteSpace(message) ? "none" : message;
            }
        }

        public static void Clear()
        {
            lock (sync) latestEvent = "none";
        }
    }
}
