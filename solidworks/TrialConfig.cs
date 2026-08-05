namespace Forge.SolidWorks
{
    /// <summary>
    /// Per-build usage configuration. ONE place, flipped per usage build:
    ///   Usage 1 → Mode = "measure"  (learn real costs, NEVER block)
    ///   Usage 2 → Mode = "enforce"  (caps active)
    /// Expiry itself is handled dynamically by UsageInit (14 days from first run).
    /// </summary>
    internal static class UsageConfig
    {
        public const string Mode = "measure";                     // "measure" | "enforce"
        public const int MaxOperations = 50;                      // enforce mode only
        public const int MaxTokensPerOp = 150000;                 // enforce mode only
        public static string ModelPrimary { get { return ForgeConfig.ModelPrimary; } } // first/novel structure
        public static string ModelLight { get { return ForgeConfig.ModelLight; } }   // simple/repetitive subtasks

        public static bool Enforce { get { return Mode == "enforce"; } }
    }
}
