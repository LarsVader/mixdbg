namespace MixDbg.Services;

/// <summary>
/// Determines whether a source file is debuggable via dbgeng (native C/C++)
/// or belongs to a managed/.NET context (C#, C++/CLI).
/// </summary>
public interface ISourceFileService
{
    /// <summary>
    /// Returns true for native C/C++ files that dbgeng can debug.
    /// Returns false for managed (.cs) files and C++/CLI files
    /// (detected by scanning for CLRSupport in the directory's vcxproj).
    /// </summary>
    bool IsNativeFile(string path);

    /// <summary>
    /// Returns true for managed files: C# (.cs) and C++/CLI (.cpp/.h with
    /// CLRSupport in vcxproj). These require the managed debugger (SOS + ClrMD).
    /// </summary>
    bool IsManagedFile(string path);

    /// <summary>
    /// Returns true for C++/CLI files: .cpp/.c/.cc/.cxx/.h/.hpp in a directory
    /// with a vcxproj containing CLRSupport. These can be debugged via dbgeng's
    /// native PDB support (GetOffsetByLine) without portable PDB parsing.
    /// </summary>
    bool IsCliFile(string path);
}