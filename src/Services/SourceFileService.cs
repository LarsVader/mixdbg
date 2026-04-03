namespace MixDbg.Services;

public sealed class SourceFileService : ISourceFileService
{
    public bool IsNativeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".cpp" and not ".c" and not ".cc" and not ".cxx"
            and not ".h" and not ".hpp")
            return false;

        // C++/CLI projects compile to IL — not debuggable via dbgeng.
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            try
            {
                foreach (var vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    var text = File.ReadAllText(vcx);
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
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // C# files are always managed.
        if (ext == ".cs")
            return true;

        // C++/CLI files: .cpp/.c in a project with <CLRSupport>.
        if (ext is ".cpp" or ".c" or ".cc" or ".cxx")
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                try
                {
                    foreach (var vcx in Directory.GetFiles(dir, "*.vcxproj"))
                    {
                        var text = File.ReadAllText(vcx);
                        if (text.Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { /* ignore IO errors */ }
            }
        }

        return false;
    }
}
