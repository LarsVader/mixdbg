using MixDbg.Engine.DbgEng;
using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Execution;
using NSubstitute;

namespace MixDbg.Tests.Handlers.Execution;

public sealed class ContinueRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_QueuesCommand()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new ContinueArguments());

        Assert.True(_engineModel.Commands.Count > 0);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public void Execute_WhenEngineExists_ClearsVariables()
    {
        _session.Engine = _engineModel;
        var group = Substitute.For<IDebugSymbolGroup2>();
        _engineModel.Variables.Allocate(group, 0, 1);

        _testee.ExecuteInternal(new ContinueArguments());

        Assert.Null(_engineModel.Variables.Get(1));
    }

    [Fact]
    public void Execute_WhenNoEngine_DoesNothing()
    {
        _testee.ExecuteInternal(new ContinueArguments());

        _engine.DidNotReceive().ExecuteContinueOnEngine(Arg.Any<NativeDebuggerModel>());
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly ContinueRequestHandlerService _testee;

    public ContinueRequestHandlerServiceTests()
    {
        _testee = new ContinueRequestHandlerService(
            Substitute.For<ILoggingService>(),
            new LogStore(Path.Combine(Path.GetTempPath(), "test.log")),
            _engine, _session);
    }
}

public sealed class NextRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenEngineExists_QueuesStepOverCommand()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new StepArguments());

        Assert.True(_engineModel.Stepping);
        Assert.True(_engineModel.Commands.Count > 0);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public void Execute_WhenEngineExists_ClearsVariables()
    {
        _session.Engine = _engineModel;
        var group = Substitute.For<IDebugSymbolGroup2>();
        _engineModel.Variables.Allocate(group, 0, 1);

        _testee.ExecuteInternal(new StepArguments());

        Assert.Null(_engineModel.Variables.Get(1));
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
    public void Execute_WhenEngineExists_QueuesStepIntoCommand()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new StepArguments());

        Assert.True(_engineModel.Stepping);
        Assert.True(_engineModel.Commands.Count > 0);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public void Execute_WhenEngineExists_ClearsVariables()
    {
        _session.Engine = _engineModel;
        var group = Substitute.For<IDebugSymbolGroup2>();
        _engineModel.Variables.Allocate(group, 0, 1);

        _testee.ExecuteInternal(new StepArguments());

        Assert.Null(_engineModel.Variables.Get(1));
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
    public void Execute_WhenEngineExists_QueuesCommand()
    {
        _session.Engine = _engineModel;

        _testee.ExecuteInternal(new StepArguments());

        Assert.True(_engineModel.Commands.Count > 0);
        Assert.Equal(SessionState.Running, _session.State);
    }

    [Fact]
    public void Execute_WhenEngineExists_ClearsVariables()
    {
        _session.Engine = _engineModel;
        var group = Substitute.For<IDebugSymbolGroup2>();
        _engineModel.Variables.Allocate(group, 0, 1);

        _testee.ExecuteInternal(new StepArguments());

        Assert.Null(_engineModel.Variables.Get(1));
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
