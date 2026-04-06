using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Inspection;
using NSubstitute;

namespace MixDbg.Tests.Handlers.Inspection;

public sealed class StackTraceRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenNoEngine_ReturnsEmpty()
    {
        var result = _testee.ExecuteInternal(new StackTraceArguments { Levels = 20 });

        Assert.Empty(result.StackFrames);
    }

    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenNativeDebuggerReturnsFrames(3);

        var result = _testee.ExecuteInternal(new StackTraceArguments { Levels = 20 });

        Assert.Equal(3, result.StackFrames.Length);
        Assert.Equal(3, result.TotalFrames);
    }

    [Fact]
    public void Execute_WhenZeroLevels_DefaultsTo50()
    {
        GivenAnEngineIsRunning();
        GivenNativeDebuggerReturnsFrames(2);

        _testee.ExecuteInternal(new StackTraceArguments { Levels = 0 });

        _engine.Received(1).GetStackTrace(Arg.Any<NativeDebuggerModel>(), 50);
    }

    private void GivenAnEngineIsRunning() => _session.Engine = _engineModel;

    private void GivenNativeDebuggerReturnsFrames(int count)
    {
        _engine.GetStackTrace(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns(Enumerable.Range(1, count).Select(i => new StackFrame { Id = i, Name = $"frame{i}" }).ToArray());
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly StackTraceRequestHandlerService _testee;

    public StackTraceRequestHandlerServiceTests()
    {
        _testee = new StackTraceRequestHandlerService(_engine, _session);
    }
}

public sealed class ThreadsRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenNoEngine_ReturnsDefaultThread()
    {
        var result = _testee.ExecuteInternal(new EmptyArguments());

        Assert.Single(result.Threads);
        Assert.Equal("Main Thread", result.Threads[0].Name);
    }

    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;
        _engine.GetThreads(Arg.Any<NativeDebuggerModel>())
            .Returns(Enumerable.Range(1, 2).Select(i => new DapThread { Id = i, Name = $"Thread {i}" }).ToArray());

        var result = _testee.ExecuteInternal(new EmptyArguments());

        Assert.Equal(2, result.Threads.Length);
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly MixDbg.Services.Handlers.Lifecycle.ThreadsRequestHandlerService _testee;

    public ThreadsRequestHandlerServiceTests()
    {
        _testee = new MixDbg.Services.Handlers.Lifecycle.ThreadsRequestHandlerService(_engine, _session);
    }
}

public sealed class ScopesRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenNoEngine_ReturnsEmpty()
    {
        var result = _testee.ExecuteInternal(new ScopesArguments { FrameId = 1 });

        Assert.Empty(result.Scopes);
    }

    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;
        _engine.GetScopes(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns([new Scope { Name = "Locals", VariablesReference = 1 }]);

        var result = _testee.ExecuteInternal(new ScopesArguments { FrameId = 1 });

        Assert.Single(result.Scopes);
        _engine.Received(1).GetScopes(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>());
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly ScopesRequestHandlerService _testee;

    public ScopesRequestHandlerServiceTests()
    {
        _testee = new ScopesRequestHandlerService(_engine, _session);
    }
}

public sealed class VariablesRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenNoEngine_ReturnsEmpty()
    {
        var result = _testee.ExecuteInternal(new VariablesArguments { VariablesReference = 1 });

        Assert.Empty(result.Variables);
    }

    [Fact]
    public void Execute_WhenEngineExists_DelegatesToNativeDebugger()
    {
        _session.Engine = _engineModel;
        _engine.GetVariables(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns(Enumerable.Range(1, 3).Select(i => new Variable
            {
                Name = $"var{i}", Value = $"{i}", VariablesReference = 0,
            }).ToArray());

        var result = _testee.ExecuteInternal(new VariablesArguments { VariablesReference = 1 });

        Assert.Equal(3, result.Variables.Length);
        _engine.Received(1).GetVariables(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>());
    }

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly VariablesRequestHandlerService _testee;

    public VariablesRequestHandlerServiceTests()
    {
        _testee = new VariablesRequestHandlerService(_engine, _session);
    }
}
