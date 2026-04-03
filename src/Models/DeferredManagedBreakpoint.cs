namespace MixDbg.Models;

/// <summary>
/// A managed breakpoint where the method name has been resolved via PDB
/// but the method hasn't been JIT-compiled yet (<c>ClrMethod.NativeCode == 0</c>).
/// Stored until a CLR notification or engine stop reveals the native code address.
/// </summary>
internal record DeferredManagedBreakpoint(
    string FilePath,
    int Line,
    string AssemblyName,
    string MethodName,
    int ILOffset,
    int BpId);
