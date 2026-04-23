using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

public sealed class SourceFileService : ISourceFileService
{
    /// <summary>
    /// Cache of directory path → whether a vcxproj with CLRSupport exists.
    /// Vcxproj content never changes during a debug session, so this is safe
    /// to cache for the lifetime of the service (singleton).
    /// </summary>
    private readonly Dictionary<string, bool> _clrSupportCache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsNativeFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".cpp" and not ".c" and not ".cc" and not ".cxx"
            and not ".h" and not ".hpp")
        {
            return false;
        }

        // C++/CLI projects compile to IL — not debuggable via dbgeng.
        return !HasClrSupport(Path.GetDirectoryName(path));
    }

    public bool IsManagedFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        // C# files are always managed.
        if (ext == ".cs")
            return true;

        // C++/CLI files: .cpp/.c/.h/.hpp in a project with <CLRSupport>.
        return ext is ".cpp" or ".c" or ".cc" or ".cxx" or ".h" or ".hpp"
            && HasClrSupport(Path.GetDirectoryName(path));
    }

    public bool IsCliFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cpp" or ".c" or ".cc" or ".cxx" or ".h" or ".hpp"
            && HasClrSupport(Path.GetDirectoryName(path));
    }

    /// <summary>
    /// Checks whether the directory (or a parent up to 5 levels) contains a
    /// vcxproj with CLRSupport. Results are cached per directory to avoid
    /// repeated disk reads.
    /// </summary>
    private bool HasClrSupport(string? dir)
    {
        if (dir == null)
            return false;

        if (_clrSupportCache.TryGetValue(dir, out bool cached))
            return cached;

        bool result = false;
        string? current = dir;
        for (int up = 0; up < 5 && current != null; up++)
        {
            // Check cache for ancestor directories too.
            if (_clrSupportCache.TryGetValue(current, out bool ancestorCached))
            {
                result = ancestorCached;
                break;
            }

            try
            {
                // Stop at solution or repo root — don't cross project boundaries.
                if (Directory.GetFiles(current, "*.sln").Length > 0
                    || Directory.Exists(Path.Combine(current, ".git")))
                {
                    break;
                }

                string[] vcxprojs = Directory.GetFiles(current, "*.vcxproj");
                if (vcxprojs.Length > 0)
                {
                    // Found a vcxproj — check for CLR support and stop walking
                    // regardless (this is the owning project).
                    foreach (string vcx in vcxprojs)
                    {
                        string text = File.ReadAllText(vcx);
                        if (text.Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                        {
                            result = true;
                            break;
                        }
                    }
                    break;
                }
            }
            catch { /* ignore IO errors */ }

            current = Path.GetDirectoryName(current);
        }

        _clrSupportCache[dir] = result;
        return result;
    }
}