using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Breakpoints;
using NSubstitute;

namespace MixDbg.Tests.Handlers.Breakpoints;

public sealed class SetBreakpointsRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenNoEngine_StoresPending()
    {
        GivenBreakpointsArgs("C:/src/main.cpp", [10, 20]);

        WhenExecuting();

        ThenPendingBreakpointsCountIs(1);
        ThenBreakpointResponseCountIs(2);
    }

    [Fact]
    public void Execute_WhenNoEngine_BreakpointsAreVerified()
    {
        GivenBreakpointsArgs("C:/src/main.cpp", [10]);

        WhenExecuting();

        ThenBreakpointAtIndexIsVerified(0, true);
    }

    [Fact]
    public void Execute_WhenEngineReady_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenBreakpointsArgs("C:/src/main.cpp", [10]);
        GivenNativeDebuggerReturnsBreakpoints();

        WhenExecuting();

        ThenNativeDebuggerSetBreakpointsWasCalled();
    }

    #region Given

    private void GivenAnEngineIsRunning() => _session.Engine = _engineModel;

    private void GivenBreakpointsArgs(string filePath, int[] lines)
    {
        _args = new SetBreakpointsArguments
        {
            Source = new Source { Path = filePath },
            Breakpoints = lines.Select(l => new SourceBreakpoint { Line = l }).ToArray(),
        };
    }

    private void GivenNativeDebuggerReturnsBreakpoints()
    {
        _engine.SetBreakpoints(Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(), Arg.Any<SourceBreakpoint[]>())
            .Returns(ci => ci.ArgAt<SourceBreakpoint[]>(2)
                .Select((bp, i) => new Breakpoint { Id = i + 1, Verified = true, Line = bp.Line }).ToArray());
    }

    #endregion

    #region When

    private void WhenExecuting() => _response = _testee.ExecuteInternal(_args!);

    #endregion

    #region Then

    private void ThenPendingBreakpointsCountIs(int expected) => Assert.Equal(expected, _session.PendingBreakpoints.Count);
    private void ThenBreakpointResponseCountIs(int expected) => Assert.Equal(expected, _response!.Breakpoints.Length);
    private void ThenBreakpointAtIndexIsVerified(int index, bool expected) => Assert.Equal(expected, _response!.Breakpoints[index].Verified);
    private void ThenNativeDebuggerSetBreakpointsWasCalled() =>
        _engine.Received().SetBreakpoints(Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(), Arg.Any<SourceBreakpoint[]>());

    #endregion

    #region Misc

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly SetBreakpointsRequestHandlerService _testee;
    private SetBreakpointsArguments? _args;
    private SetBreakpointsResponseBody? _response;

    public SetBreakpointsRequestHandlerServiceTests()
    {
        _testee = new SetBreakpointsRequestHandlerService(_engine, _session);
    }

    #endregion
}
