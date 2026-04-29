using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless source file classification service. All cached state lives in
/// the injected <see cref="VcxprojCache"/> model.
/// </summary>
public sealed class SourceFileService(
    VcxprojCache _cache,
    ILoggingService _log,
    LogStore _logStore) : ISourceFileService
{
    /// <summary>
    /// Vcxproj properties and compiler flags that indicate CLR support.
    /// Projects may use any of these instead of <c>&lt;CLRSupport&gt;</c>.
    /// </summary>
    private static readonly string[] ClrIndicators =
    [
        "<CLRSupport>",
        "<CLRImageType>",
        "<CompileAsManaged>",
        "/clr",
    ];

    public bool IsNativeFile(string path)
    {
        if (!ISourceFileService.IsCppExtension(path))
            return false;

        // C++/CLI projects compile to IL — not debuggable via dbgeng.
        return !HasClrSupport(Path.GetDirectoryName(path));
    }

    public bool IsManagedFile(string path)
    {
        // C# files are always managed.
        if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return true;

        // C++/CLI files: .cpp/.c/.h/.hpp in a project with CLR support.
        return ISourceFileService.IsCppExtension(path)
            && HasClrSupport(Path.GetDirectoryName(path));
    }

    public bool IsCliFile(string path)
        => ISourceFileService.IsCppExtension(path)
            && HasClrSupport(Path.GetDirectoryName(path));

    /// <inheritdoc />
    public bool HasClrIndicator(string vcxprojContent) =>
        ClrIndicators.Any(ind => vcxprojContent.Contains(ind, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public string? ResolveCliAssemblyName(string sourceFile) =>
        Path.GetDirectoryName(sourceFile) is { } sourceDir
            ? _cache.CliAssemblyNameByDirectory.GetOrAdd(sourceDir, FindCliVcxprojName)
            : null;

    /// <summary>
    /// Checks whether the directory (or a parent up to 5 levels) contains a
    /// vcxproj with CLR support indicators. Delegates to <see cref="FindCliVcxprojName"/>
    /// and caches the boolean result per directory.
    /// </summary>
    private bool HasClrSupport(string? dir) =>
        dir != null && _cache.ClrSupportByDirectory.GetOrAdd(dir, d => FindCliVcxprojName(d) != null);

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a vcxproj with CLR
    /// support indicators. Stops at vcxproj boundaries (any vcxproj found),
    /// solution roots, and git roots. Returns the vcxproj project name (without
    /// extension) or null if no CLR-enabled vcxproj is found.
    /// </summary>
    private string? FindCliVcxprojName(string startDir)
    {
        string? dir = startDir;
        for (int up = 0; up < 5 && dir != null; up++)
        {
            try
            {
                // Check for vcxproj BEFORE sln/git boundaries — a directory may
                // contain both a .sln and a .vcxproj (common in large projects).
                string[] vcxprojs = Directory.GetFiles(dir, "*.vcxproj");
                if (vcxprojs.Length > 0)
                {
                    // Found a vcxproj — check for CLR support and stop walking
                    // regardless (this is the owning project).
                    foreach (string vcx in vcxprojs)
                    {
                        if (HasClrIndicator(File.ReadAllText(vcx)))
                            return Path.GetFileNameWithoutExtension(vcx);
                    }
                    return null; // vcxproj found but no CLR support.
                }

                // Stop at solution or repo root — don't cross project boundaries.
                if (Directory.GetFiles(dir, "*.sln").Length > 0
                    || Directory.Exists(Path.Combine(dir, ".git")))
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(_logStore, $"FindCliVcxprojName: IO error at {dir}: {ex.Message}");
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
