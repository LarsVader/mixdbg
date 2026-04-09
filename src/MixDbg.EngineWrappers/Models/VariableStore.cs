using MixDbg.Engine.DbgEng;

namespace MixDbg.Models;

/// <summary>
/// Tracks variable containers by DAP variablesReference handle.
/// Each container holds a dbgeng symbol group and the index range
/// of symbols to enumerate. Invalidated on every continue/step.
/// </summary>
internal sealed class VariableStore
{
    private int _nextRef = 1;
    private readonly Dictionary<int, VariableContainer> _containers = [];

    // Keep old COM symbol groups alive to prevent the .NET GC from calling
    // Release() on the RCW from a finalizer thread. Releasing COM objects
    // on a non-engine thread while the engine thread is doing COM calls
    // (GetNameByOffset, GetLineByOffset) causes ACCESS_VIOLATION in dbgeng.
    // The objects are small metadata holders — the leak is bounded by the
    // number of continue/step cycles per session.
    private readonly List<IDebugSymbolGroup2> _retainedGroups = [];

    /// <summary>
    /// Allocates a new variablesReference handle for the given symbol group
    /// and index range.
    /// </summary>
    internal int Allocate(IDebugSymbolGroup2 group, uint startIndex, uint count)
    {
        int id = _nextRef++;
        _containers[id] = new VariableContainer(group, startIndex, count);
        return id;
    }

    /// <summary>Looks up a container by its variablesReference handle.</summary>
    internal VariableContainer? Get(int variablesReference)
    {
        _ = _containers.TryGetValue(variablesReference, out VariableContainer? container);
        return container;
    }

    /// <summary>
    /// Clears all variable references and resets the counter.
    /// Called on continue/step when variables become stale.
    /// </summary>
    internal void Clear()
    {
        foreach (VariableContainer container in _containers.Values)
            _retainedGroups.Add(container.Group);
        _containers.Clear();
        _nextRef = 1;
    }
}

/// <summary>
/// A single variable container: a symbol group plus the index range
/// of symbols to enumerate for this DAP variablesReference.
/// </summary>
internal sealed record VariableContainer(
    IDebugSymbolGroup2 Group,
    uint StartIndex,
    uint Count);