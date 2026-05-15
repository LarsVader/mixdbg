namespace MixDbg.Models;

/// <summary>
/// Tracks managed variable containers by DAP variablesReference handle.
/// Uses a base offset of 100,000 to avoid collision with the native
/// <see cref="VariableStore"/> (which starts at 1). Invalidated on every
/// continue/step.
/// </summary>
internal sealed class ManagedVariableStore
{
    /// <summary>
    /// Base offset for managed variable references. Native refs are always
    /// well below this value.
    /// </summary>
    internal const int BaseOffset = 100_000;

    private int _nextRef = BaseOffset;
    private readonly Dictionary<int, VariableInfo[]> _entries = [];

    /// <summary>
    /// Allocates a new variablesReference handle for the given pre-formatted locals.
    /// </summary>
    internal int Allocate(VariableInfo[] locals)
    {
        int id = _nextRef++;
        _entries[id] = locals;
        return id;
    }

    /// <summary>Looks up locals by their variablesReference handle.</summary>
    internal VariableInfo[]? Get(int variablesReference)
    {
        _ = _entries.TryGetValue(variablesReference, out VariableInfo[]? locals);
        return locals;
    }

    /// <summary>Whether the given variablesReference belongs to the managed store.</summary>
    internal static bool IsManaged(int variablesReference) => variablesReference >= BaseOffset;

    /// <summary>
    /// Clears all managed variable references and resets the counter.
    /// Called on continue/step when variables become stale.
    /// </summary>
    internal void Clear()
    {
        _entries.Clear();
        _nextRef = BaseOffset;
    }
}
