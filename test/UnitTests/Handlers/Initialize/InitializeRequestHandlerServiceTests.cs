using MixDbg.Models;
using MixDbg.Models.Dap;
using MixDbg.Services;
using MixDbg.Services.Handlers.Initialize;

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
    public void Execute_WhenCalled_SendsInitializedEvent()
    {
        WhenExecuting();

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