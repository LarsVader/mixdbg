using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Execution;
using NSubstitute;

namespace MixDbg.Tests.Handlers.Execution;

public sealed class ContinueRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new ContinueArguments());

        _engine.Received().Continue(Arg.Any<NativeDebuggerModel>());
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public void Execute_WhenNoEngine_DoesNothing()
    {
        _testee.ExecuteInternal(new ContinueArguments());

        _engine.DidNotReceive().Continue(Arg.Any<NativeDebuggerModel>());
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly ContinueRequestHandlerService _testee;

    public ContinueRequestHandlerServiceTests()
    {
        _testee = new ContinueRequestHandlerService(_engine, _session);
    }
}

public sealed class NextRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new StepArguments());

        _engine.Received(1).StepOver(Arg.Any<NativeDebuggerModel>());
        Assert.Equal(SessionState.Running, _session.State);
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly NextRequestHandlerService _testee;

    public NextRequestHandlerServiceTests()
    {
        _testee = new NextRequestHandlerService(_engine, _session);
    }
}

public sealed class StepInRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new StepArguments());

        _engine.Received(1).StepInto(Arg.Any<NativeDebuggerModel>());
        Assert.Equal(SessionState.Running, _session.State);
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly StepInRequestHandlerService _testee;

    public StepInRequestHandlerServiceTests()
    {
        _testee = new StepInRequestHandlerService(_engine, _session);
    }
}

public sealed class StepOutRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new StepArguments());

        _engine.Received(1).StepOut(Arg.Any<NativeDebuggerModel>());
        Assert.Equal(SessionState.Running, _session.State);
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly StepOutRequestHandlerService _testee;

    public StepOutRequestHandlerServiceTests()
    {
        _testee = new StepOutRequestHandlerService(_engine, _session);
    }
}

public sealed class PauseRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new EmptyArguments());

        _engine.Received(1).Break(Arg.Any<NativeDebuggerModel>());
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly PauseRequestHandlerService _testee;

    public PauseRequestHandlerServiceTests()
    {
        _testee = new PauseRequestHandlerService(_engine, _session);
    }
}
