using System;
using System.IO;
using Newtonsoft.Json;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Persistent operation counter. Stored in %APPDATA%\Forge (roaming) so it survives app restarts
    /// AND reinstalls — never in the install folder. measure mode counts silently; enforce mode blocks
    /// once MaxOperations is reached. Incremented at the START of a real operation so a killed op still
    /// counts (counter stays consistent across a crash).
    /// </summary>
    internal static class OpCounter
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "counter.json");

        private class State { public int ops; }

        public static int Count { get; private set; }

        static OpCounter()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonConvert.DeserializeObject<State>(File.ReadAllText(FilePath));
                    Count = s != null ? s.ops : 0;
                }
            }
            catch { Count = 0; }
        }

        public static void Increment()
        {
            Count++;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(new State { ops = Count }));
            }
            catch { }
        }

        public static bool LimitReached { get { return UsageConfig.Enforce && Count >= UsageConfig.MaxOperations; } }
    }
}
