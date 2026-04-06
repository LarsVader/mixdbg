using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

public sealed class SourceFileService : ISourceFileService
{
    public bool IsNativeFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".cpp" and not ".c" and not ".cc" and not ".cxx"
            and not ".h" and not ".hpp")
        {
            return false;
        }

        // C++/CLI projects compile to IL — not debuggable via dbgeng.
        string? dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            try
            {
                foreach (string vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    string text = File.ReadAllText(vcx);
                    if (text.Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            catch { /* ignore IO errors */ }
        }
        return true;
    }

    public bool IsManagedFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        // C# files are always managed.
        if (ext == ".cs")
            return true;

        // C++/CLI files: .cpp/.c/.h/.hpp in a project with <CLRSupport>.
        if (ext is ".cpp" or ".c" or ".cc" or ".cxx" or ".h" or ".hpp")
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                try
                {
                    foreach (string vcx in Directory.GetFiles(dir, "*.vcxproj"))
                    {
                        string text = File.ReadAllText(vcx);
                        if (text.Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { /* ignore IO errors */ }
            }
        }

        return false;
    }

    public bool IsCliFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".cpp" and not ".c" and not ".cc" and not ".cxx"
            and not ".h" and not ".hpp")
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            try
            {
                foreach (string vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    string text = File.ReadAllText(vcx);
                    if (text.Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* ignore IO errors */ }
        }
        return false;
    }
}