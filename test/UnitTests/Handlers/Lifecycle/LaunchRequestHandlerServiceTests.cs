using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Lifecycle;
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

    #region Given

    private void GivenLaunchArgs(string program, string? cwd)
    {
        _launchArgs = new LaunchRequestArguments { Program = program, Cwd = cwd };
    }

    private void GivenLaunchArgsWithSymbolPath(string program, string[] symbolPath)
    {
        _launchArgs = new LaunchRequestArguments { Program = program, SymbolPath = symbolPath };
    }

    #endregion

    #region When

    private void WhenExecuting()
    {
        _testee.ExecuteInternal(_launchArgs!);
    }

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected)
    {
        Assert.Equal(expected, _session.State);
    }

    private void ThenEngineWasCreated()
    {
        _engine.Received(1).CreateModel();
    }

    private void ThenStartEngineThreadWasCalled()
    {
        _engine.Received(1).StartEngineThread(Arg.Any<NativeDebuggerModel>());
    }

    #endregion

    #region Misc

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly LaunchRequestHandlerService _testee;
    private LaunchRequestArguments? _launchArgs;

    public LaunchRequestHandlerServiceTests()
    {
        _engine.CreateModel().Returns(_engineModel);
        _engine.When(e => e.StartEngineThread(Arg.Any<NativeDebuggerModel>()))
            .Do(ci => ci.ArgAt<NativeDebuggerModel>(0).EngineReady.Set());
        _testee = new LaunchRequestHandlerService(_engine, _session);
    }

    #endregion
}
