namespace Forge.Cad
{
    /// <summary>
    /// Process-wide handle to the loaded host adapter. The add-in entry point registers its adapter
    /// on host connect; canonical tools read it here. Null until a host registers — callers fail
    /// closed ("no CAD host is attached").
    /// </summary>
    public static class CadHost
    {
        public static ICadAdapter Current { get; private set; }

        public static void Register(ICadAdapter adapter) => Current = adapter;
        public static void Unregister(ICadAdapter adapter)
        {
            if (ReferenceEquals(Current, adapter)) Current = null;
        }
    }
}
