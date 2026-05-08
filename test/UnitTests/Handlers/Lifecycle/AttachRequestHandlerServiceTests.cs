using MixDbg.Models;
using MixDbg.Models.DapMessages.Lifecycle;
using MixDbg.Services;
using MixDbg.Services.Handlers.Lifecycle;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests.Handlers.Lifecycle;

public sealed class AttachRequestHandlerServiceTests
{
    [Fact]
    public void Execute_WhenPidProvided_AttachesToProcess()
    {
        GivenAttachArgs(pid: 1234);

        WhenExecuting();

        ThenEngineWasCreated();
        ThenStartEngineThreadWasCalled();
        Assert.True(_engineModel.IsAttach);
        Assert.Equal(1234u, _engineModel.AttachPid);
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Execute_WhenSymbolPathProvided_JoinsPathsWithSemicolon()
    {
        GivenAttachArgs(pid: 1234);
        _attachArgs!.SymbolPath = ["C:\\sym1", "C:\\sym2"];
        _attachArgs.Program = "test.exe";

        WhenExecuting();

        Assert.Equal("C:\\sym1;C:\\sym2", _engineModel.SymbolPath);
    }

    [Fact]
    public void Execute_WhenSymbolPathOmitted_LeavesSymbolPathNull()
    {
        GivenAttachArgs(pid: 1234);

        WhenExecuting();

        Assert.Null(_engineModel.SymbolPath);
    }

    [Fact]
    public void Execute_WhenSymbolPathEmptyArray_LeavesSymbolPathNull()
    {
        GivenAttachArgs(pid: 1234);
        _attachArgs!.SymbolPath = [];

        WhenExecuting();

        Assert.Null(_engineModel.SymbolPath);
    }

    [Fact]
    public void Execute_WhenEngineInitError_ThrowsException()
    {
        _engine.When(e => e.StartEngineThread(Arg.Any<NativeDebuggerModel>()))
            .Do(ci =>
            {
                NativeDebuggerModel m = ci.ArgAt<NativeDebuggerModel>(0);
                m.EngineInitError = new InvalidOperationException("init failed");
                m.EngineReady.Set();
            });
        GivenAttachArgs(pid: 5678);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(WhenExecuting);
        Assert.Equal("init failed", ex.Message);
    }

    [Fact]
    public void Execute_WhenNoPid_ThrowsInvalidOperation()
    {
        GivenAttachArgs(pid: null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(WhenExecuting);
        Assert.Contains("PID", ex.Message);
    }

    #region Given

    private void GivenAttachArgs(int? pid) => _attachArgs = new AttachRequestArguments { Pid = pid };

    #endregion

    #region When

    private void WhenExecuting() => _testee.ExecuteInternal(_attachArgs!);

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected) => Assert.Equal(expected, _session.State);
    private void ThenEngineWasCreated() => _engine.Received(1).CreateModel();
    private void ThenStartEngineThreadWasCalled() =>
        _engine.Received(1).StartEngineThread(Arg.Any<NativeDebuggerModel>());

    #endregion

    #region Misc

    private readonly IEngineLifecycleService _engine = Substitute.For<IEngineLifecycleService>();
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly AttachRequestHandlerService _testee;
    private AttachRequestArguments? _attachArgs;

    public AttachRequestHandlerServiceTests()
    {
        _ = _engine.CreateModel().Returns(_engineModel);
        _engine.When(e => e.StartEngineThread(Arg.Any<NativeDebuggerModel>()))
            .Do(ci => ci.ArgAt<NativeDebuggerModel>(0).EngineReady.Set());
        _testee = new AttachRequestHandlerService(_engine, _session);
    }

    #endregion
}