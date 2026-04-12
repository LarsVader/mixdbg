using ClrDebug;

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
    private readonly Dictionary<int, ManagedVariableEntry> _entries = [];

    /// <summary>
    /// Allocates a new variablesReference handle for the given entry.
    /// </summary>
    internal int Allocate(ManagedVariableEntry entry)
    {
        int id = _nextRef++;
        _entries[id] = entry;
        return id;
    }

    /// <summary>Looks up an entry by its variablesReference handle.</summary>
    internal ManagedVariableEntry? Get(int variablesReference)
    {
        _ = _entries.TryGetValue(variablesReference, out ManagedVariableEntry? entry);
        return entry;
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

/// <summary>
/// A managed variable entry. Discriminated by which field is non-null:
/// <see cref="Locals"/> for a top-level locals/args scope,
/// <see cref="ObjectValue"/> for object field expansion,
/// <see cref="ArrayValue"/> for array element expansion.
/// </summary>
internal sealed class ManagedVariableEntry
{
    /// <summary>Top-level locals + args (name/value pairs from ICorDebug).</summary>
    internal (string Name, CorDebugValue Value)[]? Locals { get; init; }

    /// <summary>Pre-formatted locals from SOS text output (no COM objects).</summary>
    internal VariableInfo[]? SimpleLocals { get; init; }

    /// <summary>Object value for field expansion.</summary>
    internal CorDebugObjectValue? ObjectValue { get; init; }

    /// <summary>Array value for element expansion.</summary>
    internal CorDebugArrayValue? ArrayValue { get; init; }

    /// <summary>Element count for array expansion.</summary>
    internal int ArrayCount { get; init; }
}
