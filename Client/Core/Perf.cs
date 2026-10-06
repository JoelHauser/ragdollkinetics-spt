using System.Diagnostics;

namespace RagdollKinetics
{
    internal static class Perf
    {
        internal static bool Enabled =>
            Settings.PerformanceLogging != null &&
            Settings.PerformanceLogging.Value;

        internal static long Start() => Stopwatch.GetTimestamp();

        internal static double Milliseconds(long started) =>
            (Stopwatch.GetTimestamp() - started) * 1000d /
            Stopwatch.Frequency;

        internal static void Log(string message)
        {
            Plugin.Log.LogInfo("[Perf] " + message);
        }
    }
}
