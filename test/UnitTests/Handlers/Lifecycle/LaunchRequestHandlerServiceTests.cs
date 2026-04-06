using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Handlers.Lifecycle;
using NSubstitute;

namespace MixDbg.Tests.Handlers.Lifecycle;

public sealed class LaunchRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenCalled_CreatesEngineAndLaunches()
    {
        GivenLaunchArgs("C:/app/test.exe", cwd: "C:/app");

        WhenExecuting();

        ThenEngineWasCreated();
        ThenNativeDebuggerLaunchWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Execute_WhenNoExplicitCwd_UsesProgDir()
    {
        GivenLaunchArgs("C:/app/bin/test.exe", cwd: null);

        WhenExecuting();

        ThenNativeDebuggerLaunchWasCalledWithCwd("C:/app/bin");
    }

    [Fact]
    public void Execute_WhenSymbolPathProvided_JoinsAndPasses()
    {
        GivenLaunchArgsWithSymbolPath("C:/app/test.exe", ["C:/symbols", "C:/other"]);

        WhenExecuting();

        ThenNativeDebuggerLaunchWasCalledWithSymbolPathContaining("C:/symbols");
        ThenNativeDebuggerLaunchWasCalledWithSymbolPathContaining("C:/other");
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

    private void ThenNativeDebuggerLaunchWasCalled()
    {
        _engine.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string[]?>());
    }

    private void ThenNativeDebuggerLaunchWasCalledWithCwd(string expected)
    {
        _engine.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(),
            Arg.Is<string?>(s => s != null && Path.GetFullPath(s) == Path.GetFullPath(expected)),
            Arg.Any<string?>(), Arg.Any<string[]?>());
    }

    private void ThenNativeDebuggerLaunchWasCalledWithSymbolPathContaining(string expected)
    {
        _engine.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(s => s != null && s.Contains(expected)),
            Arg.Any<string[]?>());
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
        _testee = new LaunchRequestHandlerService(_engine, _session);
    }

    #endregion
}
