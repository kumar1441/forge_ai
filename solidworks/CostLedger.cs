using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Local AI-cost ledger in %APPDATA%\Forge. One human-readable line per operation, plus a persisted
    /// running total so Trial Diagnostics shows total cost across sessions. Contains only counts/costs —
    /// never a file name, path, part name, or geometry.
    /// </summary>
    internal static class CostLedger
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge");
        private static readonly string LogPath = Path.Combine(Dir, "ledger.log");
        private static readonly string TotalsPath = Path.Combine(Dir, "cost_totals.json");

        public static double TotalCostUsd { get; private set; }
        public static long TotalOps { get; private set; }
        public static long TotalTokens { get; private set; }

        private class Totals { public double cost; public long ops; public long tokens; }

        static CostLedger()
        {
            try
            {
                if (File.Exists(TotalsPath))
                {
                    var t = JsonConvert.DeserializeObject<Totals>(File.ReadAllText(TotalsPath));
                    if (t != null) { TotalCostUsd = t.cost; TotalOps = t.ops; TotalTokens = t.tokens; }
                }
            }
            catch { }
        }

        public static void Record(string op, int parts, long tokensIn, long tokensOut, double costUsd, int cacheHits, int llmCalls)
        {
            TotalCostUsd += costUsd;
            TotalOps++;
            TotalTokens += tokensIn + tokensOut;

            string line = string.Format(CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss}] op={1} parts={2} tokens={3} calls={4} est_cost=${5:F4} cache_hits={6}",
                DateTime.Now, op, parts, tokensIn + tokensOut, llmCalls, costUsd, cacheHits);

            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
                File.WriteAllText(TotalsPath, JsonConvert.SerializeObject(new Totals { cost = TotalCostUsd, ops = TotalOps, tokens = TotalTokens }));
            }
            catch { }
        }

        /// <summary>Human summary for the hidden Trial Diagnostics command.</summary>
        public static string Diagnostics()
        {
            double avg = TotalOps > 0 ? TotalCostUsd / TotalOps : 0;
            string cap = TrialConfig.Enforce ? (" / " + TrialConfig.MaxOperations) : " (measure — no cap)";
            return "Trial diagnostics\n" +
                   "Ops used: " + OpCounter.Count + cap + "\n" +
                   "Total est cost this trial: $" + TotalCostUsd.ToString("F4", CultureInfo.InvariantCulture) + "\n" +
                   "Avg cost / op: $" + avg.ToString("F4", CultureInfo.InvariantCulture) + "\n" +
                   "Total tokens: " + TotalTokens + "\n" +
                   "Mode: " + TrialConfig.Mode + "  ·  Days left: " + TrialInit.DaysLeft();
        }
    }
}
