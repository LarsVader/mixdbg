using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class BreakpointServiceTests : IDisposable
{
    // ── SetBreakpointsOnEngine (managed file) ──────────────

    [Fact]
    public void SetBreakpointsOnEngine_WhenManagedFile_ReturnsPendingVerifiedBreakpoints()
    {
        GivenSourceFileIsManaged(@"C:\src\Program.cs");
        GivenBreakpointRequest(@"C:\src\Program.cs", [10, 20]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(2);
        ThenAllBreakpointsAreVerified(true);
        ThenBreakpointsHaveMessage("Pending — managed debugger not yet initialized");
    }

    // ── SetBreakpointsOnEngine (native, offset resolved) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenOffsetResolved_CreatesDirectBreakpoint()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineSucceeds(@"C:\src\main.cpp", line: 42, offset: 0x1000);
        GivenAddCodeBreakpointSucceeds(bpId: 5);
        GivenGetLineByOffsetSucceeds(offset: 0x1000, resolvedLine: 42);
        GivenBreakpointRequest(@"C:\src\main.cpp", [42]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, true);
        ThenBreakpointAtIndexHasLine(0, 42);
        ThenBreakpointAtIndexHasId(0, 5);
        ThenUserBreakpointIdsContains(5);
    }

    // ── SetBreakpointsOnEngine (native, deferred via bu) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenOffsetFails_UsesDeferredBreakpoint()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineFails(@"C:\src\main.cpp", line: 99);
        GivenAddDeferredBreakpointSucceeds(deferredBpId: 7);
        GivenBreakpointRequest(@"C:\src\main.cpp", [99]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, true);
        ThenBreakpointAtIndexHasId(0, 7);
        ThenUserBreakpointIdsContains(7);
    }

    [Fact]
    public void SetBreakpointsOnEngine_WhenDeferredFails_ReturnsUnverified()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineFails(@"C:\src\main.cpp", line: 99);
        GivenAddDeferredBreakpointFails();
        GivenBreakpointRequest(@"C:\src\main.cpp", [99]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, false);
        ThenBreakpointsHaveMessage("Could not resolve source line");
    }

    // ── SetBreakpointsOnEngine (removes old breakpoints) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenCalledAgain_RemovesOldBreakpoints()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenExistingBreakpointForFile(@"C:\src\main.cpp", line: 10, bpId: 3);
        GivenGetOffsetByLineSucceeds(@"C:\src\main.cpp", line: 20, offset: 0x3000);
        GivenAddCodeBreakpointSucceeds(bpId: 8);
        GivenGetLineByOffsetSucceeds(offset: 0x3000, resolvedLine: 20);
        GivenBreakpointRequest(@"C:\src\main.cpp", [20]);

        WhenSettingBreakpointsOnEngine();

        ThenRemoveBreakpointWasCalled(3);
        ThenUserBreakpointIdsDoesNotContain(3);
        ThenUserBreakpointIdsContains(8);
    }

    // ── SetBreakpointsOnEngine (multiple breakpoints) ───────

    [Fact]
    public void SetBreakpointsOnEngine_WhenMultipleLines_ReturnsAllResults()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineSucceedsForAll(@"C:\src\main.cpp",
            [(10, 0x1000), (20, 0x2000), (30, 0x3000)]);
        GivenAddCodeBreakpointSucceedsMultiple([1, 2, 3]);
        GivenGetLineByOffsetSucceedsForAll(
            [(0x1000, 10), (0x2000, 20), (0x3000, 30)]);
        GivenBreakpointRequest(@"C:\src\main.cpp", [10, 20, 30]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(3);
        ThenAllBreakpointsAreVerified(true);
    }

    #region Given

    private void GivenSourceFileIsNative(string path) => _ = _sourceFiles.IsNativeFile(path).Returns(true);

    private void GivenSourceFileIsManaged(string path) => _ = _sourceFiles.IsNativeFile(path).Returns(false);

    private void GivenBreakpointRequest(string filePath, int[] lines)
    {
        _bpFilePath = filePath;
        _bpRequested = [.. lines.Select(l => new SourceBreakpoint { Line = l })];
    }

    private void GivenGetOffsetByLineSucceeds(string file, int line, ulong offset) => _ = _wrapper.GetOffsetByLine(_model.Wrapper, (uint)line, file)
            .Returns((offset, true));

    private void GivenGetOffsetByLineSucceedsForAll(string file, (int line, ulong offset)[] mappings) => _ = _wrapper.GetOffsetByLine(_model.Wrapper, Arg.Any<uint>(), file)
            .Returns(ci =>
            {
                int reqLine = (int)(uint)ci[1];
                (int line, ulong offset) match = mappings.FirstOrDefault(m => m.line == reqLine);
                return match != default ? (match.offset, true) : (0UL, false);
            });

    private void GivenGetOffsetByLineFails(string file, int line) => _ = _wrapper.GetOffsetByLine(_model.Wrapper, (uint)line, file)
            .Returns((0UL, false));

    private void GivenAddCodeBreakpointSucceeds(uint bpId) => _ = _wrapper.AddCodeBreakpoint(_model.Wrapper, Arg.Any<ulong>())
            .Returns((bpId, true));

    private void GivenAddCodeBreakpointSucceedsMultiple(uint[] bpIds)
    {
        int idx = 0;
        _ = _wrapper.AddCodeBreakpoint(_model.Wrapper, Arg.Any<ulong>())
            .Returns(_ => (bpIds[idx++], true));
    }

    private void GivenGetLineByOffsetSucceeds(ulong offset, int resolvedLine) => _ = _wrapper.GetLineByOffset(_model.Wrapper, offset)
            .Returns(((uint)resolvedLine, ""));

    private void GivenGetLineByOffsetSucceedsForAll((ulong offset, int line)[] mappings) => _ = _wrapper.GetLineByOffset(_model.Wrapper, Arg.Any<ulong>())
            .Returns(ci =>
            {
                ulong reqOffset = (ulong)ci[1];
                (ulong offset, int line) match = mappings.FirstOrDefault(m => m.offset == reqOffset);
                return match != default ? ((uint Line, string File)?)(((uint)match.line, "")) : null;
            });

    private void GivenAddDeferredBreakpointSucceeds(uint deferredBpId) => _ = _wrapper.AddDeferredBreakpoint(_model.Wrapper, Arg.Any<string>(), Arg.Any<int>())
            .Returns((deferredBpId, true));

    private void GivenAddDeferredBreakpointFails() => _ = _wrapper.AddDeferredBreakpoint(_model.Wrapper, Arg.Any<string>(), Arg.Any<int>())
            .Returns((0u, false));

    private void GivenExistingBreakpointForFile(string filePath, int line, uint bpId)
    {
        _model.BreakpointIds[$"{filePath}:{line}"] = bpId;
        _ = _model.UserBreakpointIds.Add(bpId);
    }

    #endregion

    #region When

    private void WhenSettingBreakpointsOnEngine() => _bpResults = _testee.SetBreakpointsOnEngine(_model, _bpFilePath!, _bpRequested!);

    #endregion

    #region Then

    private void ThenBreakpointResultCountIs(int expected) => Assert.Equal(expected, _bpResults!.Length);

    private void ThenAllBreakpointsAreVerified(bool expected) => Assert.All(_bpResults!, bp => Assert.Equal(expected, bp.Verified));

    private void ThenBreakpointsHaveMessage(string expected) => Assert.All(_bpResults!, bp => Assert.Equal(expected, bp.Message));

    private void ThenBreakpointAtIndexIsVerified(int index, bool expected) => Assert.Equal(expected, _bpResults![index].Verified);

    private void ThenBreakpointAtIndexHasLine(int index, int expected) => Assert.Equal(expected, _bpResults![index].Line);

    private void ThenBreakpointAtIndexHasId(int index, int expected) => Assert.Equal(expected, _bpResults![index].Id);

    private void ThenUserBreakpointIdsContains(uint id) => Assert.Contains(id, _model.UserBreakpointIds);

    private void ThenUserBreakpointIdsDoesNotContain(uint id) => Assert.DoesNotContain(id, _model.UserBreakpointIds);

    private void ThenRemoveBreakpointWasCalled(uint bpId) => _ = _wrapper.Received(1).RemoveBreakpoint(_model.Wrapper, bpId);

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly ISourceFileService _sourceFiles = Substitute.For<ISourceFileService>();
    private readonly IManagedBreakpointService _managedBp = Substitute.For<IManagedBreakpointService>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly BreakpointService _testee;

    private string? _bpFilePath;
    private SourceBreakpoint[]? _bpRequested;
    private Breakpoint[]? _bpResults;

    public BreakpointServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new BreakpointService(
            _server, _transport, _log, _logStore, _sourceFiles,
            _managedBp, _wrapper);
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
