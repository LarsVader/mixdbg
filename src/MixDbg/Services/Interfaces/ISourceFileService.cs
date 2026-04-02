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
}
