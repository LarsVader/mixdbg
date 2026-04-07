using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class EngineLifecycleServiceTests : IDisposable
{
    // ── CreateModel ────────────────────────────────────────

    [Fact]
    public void CreateModel_WhenCalled_ReturnsModelWithDisposeAction()
    {
        WhenCreatingModel();

        ThenCreatedModelIsNotNull();
        ThenCreatedModelDisposeActionIsSet();
    }

    [Fact]
    public void CreateModel_WhenDisposed_SetsTerminated()
    {
        WhenCreatingModel();
        WhenDisposingCreatedModel();

        ThenCreatedModelIsTerminated();
    }

    // ── Break ──────────────────────────────────────────────

    [Fact]
    public void Break_WhenCalled_SetsPauseRequested()
    {
        WhenBreaking();

        ThenPauseRequestedIsTrue();
    }

    [Fact]
    public void Break_WhenCalled_CallsSetInterrupt()
    {
        WhenBreaking();

        ThenSetInterruptWasCalled();
    }

    // ── Terminate ──────────────────────────────────────────

    [Fact]
    public void Terminate_WhenCalled_SetsTerminated()
    {
        WhenTerminating();

        ThenModelIsTerminated();
    }

    [Fact]
    public void Terminate_WhenTargetNotExited_CallsTerminateSession()
    {
        WhenTerminating();

        ThenTerminateSessionWasCalled();
    }

    [Fact]
    public void Terminate_WhenCalled_QueuesWakeCommand()
    {
        WhenTerminating();

        ThenCommandWasQueued();
    }

    // ── Detach ─────────────────────────────────────────────

    [Fact]
    public void Detach_WhenCalled_SetsTerminated()
    {
        WhenDetaching();

        ThenModelIsTerminated();
    }

    [Fact]
    public void Detach_WhenCalled_CallsDetachSession()
    {
        WhenDetaching();

        ThenDetachSessionWasCalled();
    }

    #region When

    private void WhenCreatingModel() => _createdModel = _testee.CreateModel();

    private void WhenDisposingCreatedModel() => _createdModel!.Dispose();

    private void WhenBreaking() => _testee.Break(_model);

    private void WhenTerminating() => _testee.Terminate(_model);

    private void WhenDetaching() => _testee.Detach(_model);

    #endregion

    #region Then

    private void ThenCreatedModelIsNotNull() => Assert.NotNull(_createdModel);

    private void ThenCreatedModelDisposeActionIsSet() => Assert.NotNull(_createdModel!.DisposeAction);

    private void ThenCreatedModelIsTerminated() => Assert.True(_createdModel!.Terminated);

    private void ThenCommandWasQueued() => Assert.True(_model.Commands.Count > 0);

    private void ThenPauseRequestedIsTrue() => Assert.True(_model.PauseRequested);

    private void ThenSetInterruptWasCalled() => _wrapper.Received(1).SetInterrupt(_model.Wrapper);

    private void ThenModelIsTerminated() => Assert.True(_model.Terminated);

    private void ThenTerminateSessionWasCalled() => _wrapper.Received(1).TerminateSession(_model.Wrapper);

    private void ThenDetachSessionWasCalled() => _wrapper.Received(1).DetachSession(_model.Wrapper);

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly IManagedDebugger _managedDebugger = Substitute.For<IManagedDebugger>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly EngineLifecycleService _testee;

    private NativeDebuggerModel? _createdModel;

    public EngineLifecycleServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new EngineLifecycleService(
            _server, _transport, _log, _logStore,
            _managedDebugger, Substitute.For<IProfilerPipeService>(),
            Substitute.For<IBreakpointService>(), _wrapper);
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
    }

    public void Dispose()
    {
        _model.Commands.CompleteAdding();
        _model.Commands.Dispose();
        _model.Stopped.Dispose();
        _model.EngineReady.Dispose();
    }

    #endregion
}
