using System;
using System.IO;
using Newtonsoft.Json;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Persistent operation counter (usage stat). Stored in %APPDATA%\Forge (roaming) so it survives app restarts
    /// AND reinstalls — never in the install folder. Incremented at the START of a real operation so a killed op
    /// still counts (counter stays consistent across a crash). Forge is unlimited — there is no operation cap.
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

        public static bool LimitReached { get { return false; } }  // no operation cap — never reached
    }
}
