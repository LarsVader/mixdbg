using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Lifecycle;
using NSubstitute;

namespace MixDbg.Tests.Handlers.Lifecycle;

public sealed class DisconnectRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenTerminateTrue_TerminatesEngine()
    {
        GivenAnEngineIsRunning();

        WhenExecuting(terminateDebuggee: true);

        ThenNativeDebuggerTerminateWasCalled();
        ThenSessionStateIs(SessionState.Terminated);
    }

    [Fact]
    public void Execute_WhenTerminateFalse_DetachesEngine()
    {
        GivenAnEngineIsRunning();

        WhenExecuting(terminateDebuggee: false);

        ThenNativeDebuggerDetachWasCalled();
        ThenSessionStateIs(SessionState.Terminated);
    }

    [Fact]
    public void Execute_WhenNoEngine_SetsTerminatedState()
    {
        WhenExecuting(terminateDebuggee: true);

        ThenSessionStateIs(SessionState.Terminated);
    }

    #region Given

    private void GivenAnEngineIsRunning() => _session.Engine = _engineModel;

    #endregion

    #region When

    private void WhenExecuting(bool terminateDebuggee)
    {
        try { _testee.ExecuteInternal(new DisconnectArguments { TerminateDebuggee = terminateDebuggee }); }
        catch (DisconnectException) { /* expected */ }
    }

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected) => Assert.Equal(expected, _session.State);
    private void ThenNativeDebuggerTerminateWasCalled() => _engine.Received(1).Terminate(Arg.Any<NativeDebuggerModel>());
    private void ThenNativeDebuggerDetachWasCalled() => _engine.Received(1).Detach(Arg.Any<NativeDebuggerModel>());

    #endregion

    #region Misc

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly DisconnectRequestHandlerService _testee;

    public DisconnectRequestHandlerServiceTests()
    {
        _testee = new DisconnectRequestHandlerService(_engine, _session);
    }

    #endregion
}
