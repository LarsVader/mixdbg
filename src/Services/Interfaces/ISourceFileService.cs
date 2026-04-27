namespace MixDbg.Services.Interfaces;

/// <summary>
/// Determines whether a source file is debuggable via dbgeng (native C/C++)
/// or belongs to a managed/.NET context (C#, C++/CLI).
/// </summary>
public interface ISourceFileService
{
    /// <summary>
    /// Returns true for native C/C++ files that dbgeng can debug.
    /// Returns false for managed (.cs) files and C++/CLI files
    /// (detected by scanning for CLR indicators in the directory's vcxproj).
    /// </summary>
    bool IsNativeFile(string path);

    /// <summary>
    /// Returns true for managed files: C# (.cs) and C++/CLI (.cpp/.h with
    /// CLR support in vcxproj). These require the managed debugger (SOS + ClrMD).
    /// </summary>
    bool IsManagedFile(string path);

    /// <summary>
    /// Returns true for C++/CLI files: .cpp/.c/.cc/.cxx/.h/.hpp in a directory
    /// with a vcxproj containing CLR support indicators. These can be debugged via dbgeng's
    /// native PDB support (GetOffsetByLine) without portable PDB parsing.
    /// </summary>
    bool IsCliFile(string path);

    /// <summary>
    /// Returns true for C/C++ file extensions: .cpp, .c, .cc, .cxx, .h, .hpp.
    /// Does not check for CLR support — just the extension.
    /// </summary>
    static bool IsCppExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cpp" or ".c" or ".cc" or ".cxx" or ".h" or ".hpp";
    }
}
