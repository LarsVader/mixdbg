using System.Collections.Concurrent;

namespace MixDbg.Models;

/// <summary>
/// Caches vcxproj-derived information for source file classification.
/// Vcxproj content never changes during a debug session, so results
/// are safe to cache for the process lifetime. Registered as a singleton.
/// Thread-safe — may be read/written from both DAP handler and engine threads.
/// </summary>
public sealed class VcxprojCache
{
    /// <summary>
    /// Directory path → whether a vcxproj with CLR support exists.
    /// </summary>
    internal ConcurrentDictionary<string, bool> ClrSupportByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Source directory → CLI assembly name (or null if not CLI).
    /// </summary>
    internal ConcurrentDictionary<string, string?> CliAssemblyNameByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);
}
