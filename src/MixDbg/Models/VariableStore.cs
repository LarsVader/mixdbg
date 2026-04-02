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
    private readonly Dictionary<int, VariableContainer> _containers = new();

    /// <summary>
    /// Allocates a new variablesReference handle for the given symbol group
    /// and index range.
    /// </summary>
    internal int Allocate(IDebugSymbolGroup2 group, uint startIndex, uint count)
    {
        var id = _nextRef++;
        _containers[id] = new VariableContainer(group, startIndex, count);
        return id;
    }

    /// <summary>Looks up a container by its variablesReference handle.</summary>
    internal VariableContainer? Get(int variablesReference)
    {
        _containers.TryGetValue(variablesReference, out var container);
        return container;
    }

    /// <summary>
    /// Clears all variable references and resets the counter.
    /// Called on continue/step when variables become stale.
    /// </summary>
    internal void Clear()
    {
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
