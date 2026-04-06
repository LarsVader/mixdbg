using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;
using MixDbg.Engine.DbgEng;

namespace MixDbg.Engine.CorDebug;

/// <summary>
/// Implements <see cref="ICorDebugMutableDataTarget"/> by bridging to existing
/// dbgeng COM interfaces. This allows <c>ICLRDebugging::OpenVirtualProcess</c>
/// to create an <c>ICorDebugProcess</c> that piggybacks on the dbgeng session
/// for all memory and thread context access.
/// </summary>
[GeneratedComClass]
internal sealed partial class DbgEngDataTarget : ICorDebugMutableDataTarget
{
    private readonly IDebugDataSpaces _dataSpaces;
    private readonly IDebugAdvanced _advanced;
    private readonly IDebugSystemObjects _sysObjects;

    public DbgEngDataTarget(
        IDebugDataSpaces dataSpaces,
        IDebugAdvanced advanced,
        IDebugSystemObjects sysObjects)
    {
        _dataSpaces = dataSpaces;
        _advanced = advanced;
        _sysObjects = sysObjects;
    }

    public HRESULT GetPlatform(out CorDebugPlatform pTargetPlatform)
    {
        pTargetPlatform = CorDebugPlatform.CORDB_PLATFORM_WINDOWS_AMD64;
        return HRESULT.S_OK;
    }

    public HRESULT ReadVirtual(CORDB_ADDRESS address, IntPtr pBuffer, int bytesRequested, out int pBytesRead)
    {
        int hr = _dataSpaces.ReadVirtual(address, pBuffer, (uint)bytesRequested, out uint read);
        pBytesRead = (int)read;
        return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
    }

    public HRESULT GetThreadContext(int dwThreadId, ContextFlags contextFlags, int contextSize, IntPtr pContext)
    {
        // Save the current dbgeng thread so we can restore it.
        int hr = _sysObjects.GetCurrentThreadId(out uint savedThreadId);
        if (hr < 0)
        {
            return (HRESULT)hr;
        }

        try
        {
            // Map the OS thread ID to a dbgeng engine thread ID.
            hr = _sysObjects.GetThreadIdBySystemId((uint)dwThreadId, out uint engineThreadId);
            if (hr < 0)
                return (HRESULT)hr;

            // Switch to the target thread.
            hr = _sysObjects.SetCurrentThreadId(engineThreadId);
            if (hr < 0)
                return (HRESULT)hr;

            // Read the thread context.
            hr = _advanced.GetThreadContext(pContext, (uint)contextSize);
            return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
        }
        finally
        {
            // Restore the original thread.
            _sysObjects.SetCurrentThreadId(savedThreadId);
        }
    }

    // ── ICorDebugMutableDataTarget ─────────────────────

    public HRESULT WriteVirtual(CORDB_ADDRESS address, IntPtr pBuffer, int bytesRequested)
    {
        int hr = _dataSpaces.WriteVirtual(address, pBuffer, (uint)bytesRequested, out _);
        return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
    }

    public HRESULT SetThreadContext(int dwThreadId, int contextSize, IntPtr pContext)
    {
        int hr = _sysObjects.GetCurrentThreadId(out uint savedThreadId);
        if (hr < 0)
            return (HRESULT)hr;

        try
        {
            hr = _sysObjects.GetThreadIdBySystemId((uint)dwThreadId, out uint engineThreadId);
            if (hr < 0)
                return (HRESULT)hr;

            hr = _sysObjects.SetCurrentThreadId(engineThreadId);
            if (hr < 0)
                return (HRESULT)hr;

            hr = _advanced.SetThreadContext(pContext, (uint)contextSize);
            return hr >= 0 ? HRESULT.S_OK : (HRESULT)hr;
        }
        finally
        {
            _sysObjects.SetCurrentThreadId(savedThreadId);
        }
    }

    public HRESULT ContinueStatusChanged(int dwThreadId, int continueStatus)
    {
        return HRESULT.S_OK;
    }
}
