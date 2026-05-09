using MixDbg.Engine.DbgEng;
using MixDbg.Engine.DbgEng.Constants;

namespace MixDbg.Tests;

public sealed class DebuggeeOutputForwarderTests
{
    [Fact]
    public void Output_WhenMaskIsDebuggee_InvokesDelegateWithText()
    {
        List<string> received = [];
        DebuggeeOutputForwarder forwarder = new(received.Add);

        int hr = forwarder.Output(DebugOutput.Debuggee, "trace line\n");

        Assert.Equal(0, hr);
        Assert.Equal(["trace line\n"], received);
    }

    [Fact]
    public void Output_WhenMaskIsNotDebuggee_DropsText()
    {
        // The forwarder owns the dbgeng-mask knowledge; consumers outside
        // EngineWrappers don't need to filter (or even know about) mask values.
        List<string> received = [];
        DebuggeeOutputForwarder forwarder = new(received.Add);

        _ = forwarder.Output(DebugOutput.Normal, "engine chatter");
        _ = forwarder.Output(DebugOutput.Warning, "warn");
        _ = forwarder.Output(DebugOutput.Symbols, "sym");

        Assert.Empty(received);
    }

    [Fact]
    public void Output_WhenTextIsNullOrEmpty_DoesNotInvokeDelegate()
    {
        List<string> received = [];
        DebuggeeOutputForwarder forwarder = new(received.Add);

        _ = forwarder.Output(DebugOutput.Debuggee, "");
        _ = forwarder.Output(DebugOutput.Debuggee, null!);

        Assert.Empty(received);
    }
}
