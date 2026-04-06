namespace MixDbg.Services;

/// <summary>
/// Reads portable PDB files to map between method tokens, IL offsets, and
/// source file locations. Used for C# code where dbgeng cannot resolve
/// source locations natively. Thread-safe singleton that caches PDB readers.
/// </summary>
public interface IPdbSourceMapper
{
    /// <summary>
    /// Resolves a method token and IL offset to a source file and line number
    /// by reading the portable PDB associated with the given assembly path.
    /// </summary>
    (string File, int Line)? GetSourceLocation(string assemblyPath, int methodToken, int ilOffset);

    /// <summary>
    /// Finds the method token and assembly name for a given source file and line,
    /// by reading the portable PDB associated with the given assembly path.
    /// </summary>
    (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? FindMethodAtLine(
        string assemblyPath, string sourceFile, int line);

    /// <summary>
    /// Resolves a method token to its fully qualified name (Namespace.Type.Method)
    /// by reading the PE metadata of the given assembly.
    /// </summary>
    string? GetMethodName(string assemblyPath, int methodToken);

    /// <summary>
    /// Finds a method token by a relative virtual address (RVA) that falls inside
    /// the method body.
    /// </summary>
    int? FindTokenByRva(string assemblyPath, int rva);
}