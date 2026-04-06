using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using ClrDebug;

using MixDbg.Engine.DbgEng;

namespace MixDbg.Engine.CorDebug;

/// <summary>
/// Implements <see cref="ICLRDataTarget"/> by bridging to dbgeng COM interfaces.
/// Required by <c>CLRDataCreateInstance</c> to initialize the DAC (SOSDacInterface)
/// for querying JIT-compiled method addresses.
/// </summary>
[GeneratedComClass]
internal sealed partial class DbgEngClrDataTarget(
    IDebugDataSpaces dataSpaces,
    IDebugAdvanced advanced,
    IDebugSystemObjects sysObjects) : ICLRDataTarget
{
    private readonly IDebugDataSpaces _dataSpaces = dataSpaces;
    private readonly IDebugAdvanced _advanced = advanced;
    private readonly IDebugSystemObjects _sysObjects = sysObjects;

    /// <summary>Known module base addresses keyed by image path (case-insensitive).</summary>
    private readonly Dictionary<string, ulong> _moduleBaseAddresses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a known module base address for <see cref="GetImageBase"/> lookups.
    /// </summary>
    public void AddModuleBase(string imagePath, ulong baseAddress)
    {
        _moduleBaseAddresses[imagePath] = baseAddress;
        // Also register by filename only for flexible matching.
        string fileName = Path.GetFileName(imagePath);
        if (!string.IsNullOrEmpty(fileName))
            _moduleBaseAddresses[fileName] = baseAddress;
    }

    public HRESULT GetMachineType(out IMAGE_FILE_MACHINE machineType)
    {
        machineType = IMAGE_FILE_MACHINE.AMD64;
        return HRESULT.S_OK;
    }

    public HRESULT GetPointerSize(out int pointerSize)
    {
        pointerSize = 8; // x64
        return HRESULT.S_OK;
    }

    public HRESULT GetImageBase(string imagePath, out CLRDATA_ADDRESS baseAddress)
    {
        // Look up in registered modules (by full path or filename).
        if (_moduleBaseAddresses.TryGetValue(imagePath, out ulong addr))
        {
            baseAddress = (CLRDATA_ADDRESS)addr;
            return HRESULT.S_OK;
        }

        string fileName = Path.GetFileName(imagePath);
        if (!string.IsNullOrEmpty(fileName) && _moduleBaseAddresses.TryGetValue(fileName, out addr))
        {
            baseAddress = (CLRDATA_ADDRESS)addr;
            return HRESULT.S_OK;
        }

        baseAddress = default;
        return HRESULT.E_FAIL;
    }

    public HRESULT ReadVirtual(CLRDATA_ADDRESS address, IntPtr buffer, int bytesRequested, out int bytesRead)
    {
        int hr = _dataSpaces.ReadVirtual((ulong)address, buffer, (uint)bytesRequested, out uint read);
        bytesRead = (int)read;
        return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
    }

    public HRESULT WriteVirtual(CLRDATA_ADDRESS address, IntPtr buffer, int bytesRequested, out int bytesWritten)
    {
        int hr = _dataSpaces.WriteVirtual((ulong)address, buffer, (uint)bytesRequested, out uint written);
        bytesWritten = (int)written;
        return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
    }

    public HRESULT GetTLSValue(int threadID, int index, out CLRDATA_ADDRESS value)
    {
        value = default;
        return HRESULT.E_NOTIMPL;
    }

    public HRESULT SetTLSValue(int threadID, int index, CLRDATA_ADDRESS value) => HRESULT.E_NOTIMPL;

    public HRESULT GetCurrentThreadID(out int threadID)
    {
        int hr = _sysObjects.GetCurrentThreadSystemId(out uint osId);
        threadID = (int)osId;
        return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
    }

    public HRESULT GetThreadContext(int threadID, ContextFlags contextFlags, int contextSize, IntPtr context)
    {
        int hr = _sysObjects.GetCurrentThreadId(out uint savedThreadId);
        if (hr < 0)
            return (HRESULT)hr;

        try
        {
            hr = _sysObjects.GetThreadIdBySystemId((uint)threadID, out uint engineThreadId);
            if (hr < 0)
                return (HRESULT)hr;

            hr = _sysObjects.SetCurrentThreadId(engineThreadId);
            if (hr < 0)
                return (HRESULT)hr;

            hr = _advanced.GetThreadContext(context, (uint)contextSize);
            return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
        }
        finally
        {
            _ = _sysObjects.SetCurrentThreadId(savedThreadId);
        }
    }

    public HRESULT SetThreadContext(int threadID, int contextSize, IntPtr context) => HRESULT.E_NOTIMPL;

    public HRESULT Request(uint reqCode, int inBufferSize, IntPtr inBuffer, int outBufferSize, IntPtr outBuffer) => HRESULT.E_NOTIMPL;
}