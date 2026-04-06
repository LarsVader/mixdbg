using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

// GUID: 4bf58045-d654-4c40-b0af-683090f356dc

/// <summary>
/// Receives text output from dbgeng commands. Used to capture output
/// from SOS commands like <c>!bpmd</c> executed via
/// <see cref="IDebugControl.Execute"/>.
/// </summary>
[ComImport, Guid("4bf58045-d654-4c40-b0af-683090f356dc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugOutputCallbacks
{
    // Slot 0 (after IUnknown)
    [PreserveSig]
    int Output(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Text);
}