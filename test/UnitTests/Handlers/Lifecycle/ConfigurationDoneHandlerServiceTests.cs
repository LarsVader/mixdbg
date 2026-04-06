using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services;
using MixDbg.Services.Handlers.Lifecycle;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests.Handlers.Lifecycle;

public sealed class ConfigurationDoneHandlerServiceTests : IDisposable
{
    [Fact]
    public void Execute_WhenNoEngine_SetsConfiguredState()
    {
        WhenExecuting();

        ThenSessionStateIs(SessionState.Configured);
    }

    [Fact]
    public void Execute_WhenEngineAndPendingBreakpoints_AppliesThem()
    {
        GivenAnEngineIsRunning();
        GivenPendingBreakpointsExist("C:/src/main.cpp", [10]);
        GivenNativeDebuggerReturnsBreakpoints();

        WhenExecutingWithDrain();

        ThenNativeDebuggerSetBreakpointsWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Execute_WhenEngineAndPendingBreakpoints_SendsBreakpointEvents()
    {
        GivenAnEngineIsRunning();
        GivenPendingBreakpointsExist("C:/src/main.cpp", [10]);
        GivenNativeDebuggerReturnsBreakpoints();

        WhenExecutingWithDrain();

        ThenBreakpointEventWasSent();
    }

    #region Given

    private void GivenAnEngineIsRunning() => _session.Engine = _engineModel;

    private void GivenPendingBreakpointsExist(string filePath, int[] lines) => _session.PendingBreakpoints.Add(new SetBreakpointsArguments
    {
        Source = new Source { Path = filePath },
        Breakpoints = [.. lines.Select(l => new SourceBreakpoint { Line = l })],
    });

    private void GivenNativeDebuggerReturnsBreakpoints() => _ = _engine.SetBreakpointsOnEngine(Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(), Arg.Any<SourceBreakpoint[]>())
            .Returns(ci => [.. ci.ArgAt<SourceBreakpoint[]>(2).Select((bp, i) => new Breakpoint { Id = i + 1, Verified = true, Line = bp.Line })]);

    #endregion

    #region When

    private void WhenExecuting() => _testee.ExecuteInternal(new EmptyArguments());

    private void WhenExecutingWithDrain()
    {
        // Start a background drain thread to process QueueEngineQuery commands.
        Thread drainThread = new(() =>
        {
            try
            {
                foreach (Action cmd in _engineModel.Commands.GetConsumingEnumerable())
                    cmd();
            }
            catch (OperationCanceledException) { }
        })
        { IsBackground = true };
        drainThread.Start();

        _testee.ExecuteInternal(new EmptyArguments());

        _engineModel.Commands.CompleteAdding();
        _ = drainThread.Join(TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected) => Assert.Equal(expected, _session.State);
    private void ThenNativeDebuggerSetBreakpointsWasCalled() =>
        _engine.Received().SetBreakpointsOnEngine(Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(), Arg.Any<SourceBreakpoint[]>());
    private void ThenBreakpointEventWasSent() =>
        _server.Received().SendEvent(_transport, "breakpoint", Arg.Any<BreakpointEventBody>());

    #endregion

    #region Misc

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly DapServerModel _transport = new(Stream.Null, Stream.Null);
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly ConfigurationDoneHandlerService _testee;

    public ConfigurationDoneHandlerServiceTests() => _testee = new ConfigurationDoneHandlerService(
            Substitute.For<ILoggingService>(),
            new LogStore(Path.Combine(Path.GetTempPath(), "test.log")),
            _engine, _server, _transport, _session);

    public void Dispose()
    {
        try { _engineModel.Commands.CompleteAdding(); } catch { }
        _engineModel.Commands.Dispose();
        _engineModel.Stopped.Dispose();
        _engineModel.EngineReady.Dispose();
    }

    #endregion
}