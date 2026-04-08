using MixDbg.Models;
using MixDbg.Models.DapMessages.Lifecycle;
using MixDbg.Services;
using MixDbg.Services.Handlers.Lifecycle;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests.Handlers.Lifecycle;

public sealed class LaunchRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenCalled_CreatesEngineAndStartsThread()
    {
        GivenLaunchArgs("C:/app/test.exe", cwd: "C:/app");

        WhenExecuting();

        ThenEngineWasCreated();
        ThenStartEngineThreadWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Execute_WhenNoExplicitCwd_UsesProgDir()
    {
        GivenLaunchArgs("C:/app/bin/test.exe", cwd: null);

        WhenExecuting();

        Assert.Equal("C:\\app\\bin", _engineModel.LaunchCwd);
    }

    [Fact]
    public void Execute_WhenSymbolPathProvided_JoinsAndSetsOnModel()
    {
        GivenLaunchArgsWithSymbolPath("C:/app/test.exe", ["C:/symbols", "C:/other"]);

        WhenExecuting();

        Assert.NotNull(_engineModel.SymbolPath);
        Assert.Contains("C:/symbols", _engineModel.SymbolPath);
        Assert.Contains("C:/other", _engineModel.SymbolPath);
    }

    [Fact]
    public void Execute_WhenPendingBreakpoints_CopiesHintsToProfilerBreakpointHints()
    {
        _session.PendingBreakpoints.Add(new MixDbg.Models.DapMessages.Breakpoints.SetBreakpointsArguments
        {
            Source = new MixDbg.Models.DapMessages.Protocol.Source { Path = @"C:\src\Program.cs" },
            Breakpoints = [new MixDbg.Models.DapMessages.Breakpoints.SourceBreakpoint { Line = 10 }],
        });
        GivenLaunchArgs("C:/app/test.exe", cwd: "C:/app");

        WhenExecuting();

        _ = Assert.Single(_engineModel.ProfilerBreakpointHints);
        Assert.Equal(@"C:\src\Program.cs", _engineModel.ProfilerBreakpointHints[0].FilePath);
        Assert.Equal(10, _engineModel.ProfilerBreakpointHints[0].Line);
    }

    [Fact]
    public void Execute_WhenEngineInitFails_ThrowsException()
    {
        _engine.When(e => e.StartEngineThread(Arg.Any<NativeDebuggerModel>()))
            .Do(ci =>
            {
                NativeDebuggerModel m = ci.ArgAt<NativeDebuggerModel>(0);
                m.EngineInitError = new InvalidOperationException("launch failed");
                m.EngineReady.Set();
            });
        GivenLaunchArgs("C:/app/test.exe", cwd: "C:/app");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(WhenExecuting);
        Assert.Equal("launch failed", ex.Message);
    }

    [Fact]
    public void Execute_WhenArgsProvided_SetsLaunchArgs()
    {
        _launchArgs = new LaunchRequestArguments
        {
            Program = "C:/app/test.exe",
            Args = ["--flag", "value"],
        };

        WhenExecuting();

        Assert.Equal(["--flag", "value"], _engineModel.LaunchArgs!);
    }

    #region Given

    private void GivenLaunchArgs(string program, string? cwd) => _launchArgs = new LaunchRequestArguments { Program = program, Cwd = cwd };

    private void GivenLaunchArgsWithSymbolPath(string program, string[] symbolPath) => _launchArgs = new LaunchRequestArguments { Program = program, SymbolPath = symbolPath };

    #endregion

    #region When

    private void WhenExecuting() => _testee.ExecuteInternal(_launchArgs!);

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected) => Assert.Equal(expected, _session.State);

    private void ThenEngineWasCreated() => _ = _engine.Received(1).CreateModel();

    private void ThenStartEngineThreadWasCalled() => _engine.Received(1).StartEngineThread(Arg.Any<NativeDebuggerModel>());

    #endregion

    #region Misc

    private readonly IEngineLifecycleService _engine = Substitute.For<IEngineLifecycleService>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly LaunchRequestHandlerService _testee;
    private LaunchRequestArguments? _launchArgs;

    public LaunchRequestHandlerServiceTests()
    {
        _ = _engine.CreateModel().Returns(_engineModel);
        _engine.When(e => e.StartEngineThread(Arg.Any<NativeDebuggerModel>()))
            .Do(ci => ci.ArgAt<NativeDebuggerModel>(0).EngineReady.Set());
        _testee = new LaunchRequestHandlerService(_engine, _session);
    }

    #endregion
}