using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;

namespace MixDbg.Engine.CorDebug;

/// <summary>
/// Implements <see cref="ICLRDebuggingLibraryProvider"/> to locate <c>mscordbi.dll</c>
/// next to the target process's <c>coreclr.dll</c>. Called by
/// <c>ICLRDebugging::OpenVirtualProcess</c> to load the correct debugging library
/// matching the target CLR version.
/// </summary>
[GeneratedComClass]
internal sealed partial class RuntimeLibraryProvider : ICLRDebuggingLibraryProvider
{
    private readonly string _coreClrDirectory;

    /// <param name="coreClrPath">Full path to the target's coreclr.dll (from dbgeng LoadModule).</param>
    public RuntimeLibraryProvider(string coreClrPath)
    {
        _coreClrDirectory = Path.GetDirectoryName(coreClrPath)
            ?? throw new ArgumentException("Invalid coreclr path", nameof(coreClrPath));
    }

    public HRESULT ProvideLibrary(string pwszFileName, int dwTimestamp, int dwSizeOfImage, out IntPtr phModule)
    {
        var fullPath = Path.Combine(_coreClrDirectory, pwszFileName);
        if (!File.Exists(fullPath))
        {
            phModule = IntPtr.Zero;
            return HRESULT.E_FAIL;
        }

        phModule = NativeLibrary.Load(fullPath);
        return HRESULT.S_OK;
    }
}
