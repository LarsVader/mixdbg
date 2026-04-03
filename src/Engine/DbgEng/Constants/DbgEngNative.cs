using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

public static class DbgEngNative
{
    [DllImport("dbgeng.dll")]
    public static extern int DebugCreate(
        [In] ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object @interface);
}
