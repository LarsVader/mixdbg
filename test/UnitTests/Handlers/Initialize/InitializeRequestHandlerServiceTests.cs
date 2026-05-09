using MixDbg.Models;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Initialize;
using MixDbg.Services;
using MixDbg.Services.Handlers.Initialize;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests.Handlers.Initialize;

public sealed class InitializeRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenCalled_SetsStateToInitialized()
    {
        WhenExecuting();

        ThenSessionStateIs(SessionState.Initialized);
    }

    [Fact]
    public void Execute_WhenCalled_DoesNotSendInitializedEventBeforeResponse()
    {
        // The initialized event must not fire from ExecuteInternal — that put
        // it on the wire BEFORE the initialize response, which races nvim-dap
        // (with no breakpoints, set_breakpoints calls on_done synchronously
        // before capabilities have been set, skipping configurationDone).
        WhenExecuting();

        _server.DidNotReceive().SendEvent(_transport, "initialized", Arg.Any<InitializedEventBody>());
    }

    [Fact]
    public void OnAfterResponse_WhenCalled_SendsInitializedEvent()
    {
        WhenExecuting();
        _testee.OnAfterResponse();

        ThenInitializedEventWasSent();
    }

    [Fact]
    public void Execute_WhenCalled_ReturnsCapabilities()
    {
        WhenExecuting();

        Assert.True(_result!.SupportsConfigurationDoneRequest);
        Assert.True(_result.SupportsTerminateRequest);
        Assert.True(_result.SupportsEvaluateForHovers);
    }

    #region When

    private void WhenExecuting() => _result = _testee.ExecuteInternal(new InitializeRequestArguments());

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected) => Assert.Equal(expected, _session.State);

    private void ThenInitializedEventWasSent() => _server.Received(1).SendEvent(_transport, "initialized", Arg.Any<InitializedEventBody>());

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly DapServerModel _transport = new(Stream.Null, Stream.Null);
    private readonly DebugSessionModel _session = new();
    private readonly InitializeRequestHandlerService _testee;
    private Capabilities? _result;

    public InitializeRequestHandlerServiceTests() => _testee = new InitializeRequestHandlerService(_server, _transport, _session);

    #endregion
}