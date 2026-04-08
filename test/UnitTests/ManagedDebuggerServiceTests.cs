using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MixDbg.Tests;

public sealed class ManagedDebuggerServiceTests : IDisposable
{
    // ── IsInitialized ──────────────────────────────────────

    [Fact]
    public void IsInitialized_WhenNotInitialized_ReturnsFalse()
    {
        GivenManagedNotInitialized();

        WhenCheckingIsInitialized();

        ThenIsInitializedIs(false);
    }

    [Fact]
    public void IsInitialized_WhenInitialized_ReturnsTrue()
    {
        GivenManagedInitialized();

        WhenCheckingIsInitialized();

        ThenIsInitializedIs(true);
    }

    // ── InitializeRuntime ──────────────────────────────────

    [Fact]
    public void InitializeRuntime_WhenAlreadyInitialized_ReturnsTrueWithoutCallingCorDebug()
    {
        GivenManagedInitialized();

        WhenInitializingRuntime();

        ThenRuntimeResultIs(true);
        ThenCorDebugCreateModelWasNotCalled();
    }

    [Fact]
    public void InitializeRuntime_WhenCoreClrPathIsNull_ReturnsFalse()
    {
        GivenCoreClrPath(null);
        GivenCoreClrBaseAddress(0x7FF00000UL);

        WhenInitializingRuntime();

        ThenRuntimeResultIs(false);
    }

    [Fact]
    public void InitializeRuntime_WhenCoreClrPathIsEmpty_ReturnsFalse()
    {
        GivenCoreClrPath("");
        GivenCoreClrBaseAddress(0x7FF00000UL);

        WhenInitializingRuntime();

        ThenRuntimeResultIs(false);
    }

    [Fact]
    public void InitializeRuntime_WhenCoreClrBaseAddressIsZero_ReturnsFalse()
    {
        GivenCoreClrPath(@"C:\coreclr.dll");
        GivenCoreClrBaseAddress(0);

        WhenInitializingRuntime();

        ThenRuntimeResultIs(false);
    }

    [Fact]
    public void InitializeRuntime_WhenInitializeProcessFails_ReturnsFalse()
    {
        GivenCoreClrPath(@"C:\coreclr.dll");
        GivenCoreClrBaseAddress(0x7FF00000UL);
        GivenCorDebugCreateModelReturnsNewModel();
        GivenInitializeProcessReturns(false);

        WhenInitializingRuntime();

        ThenRuntimeResultIs(false);
        ThenModelIsNotManagedInitialized();
    }

    [Fact]
    public void InitializeRuntime_WhenSucceeds_ReturnsTrueAndInitializesModel()
    {
        GivenCoreClrPath(@"C:\coreclr.dll");
        GivenCoreClrBaseAddress(0x7FF00000UL);
        GivenCorDebugCreateModelReturnsNewModel();
        GivenInitializeProcessReturns(true);

        WhenInitializingRuntime();

        ThenRuntimeResultIs(true);
        ThenModelIsManagedInitialized();
        ThenFlushProcessStateWasCalled();
        ThenRefreshModulesWasCalled();
        ThenInitializeDacWasCalled();
    }

    [Fact]
    public void InitializeRuntime_WhenDacInitThrows_StillReturnsTrueAndLogsWarning()
    {
        GivenCoreClrPath(@"C:\coreclr.dll");
        GivenCoreClrBaseAddress(0x7FF00000UL);
        GivenCorDebugCreateModelReturnsNewModel();
        GivenInitializeProcessReturns(true);
        GivenDacInitThrows(new InvalidOperationException("DAC load failed"));

        WhenInitializingRuntime();

        ThenRuntimeResultIs(true);
        ThenModelIsManagedInitialized();
        ThenWarningWasLogged("DAC init failed");
    }

    // ── TryInitializeManaged ───────────────────────────────

    [Fact]
    public void TryInitializeManaged_WhenRuntimeInitFails_DoesNotApplyBreakpoints()
    {
        // CoreClrPath is null, so InitializeRuntime fails.
        GivenCoreClrPath(null);

        WhenTryingInitializeManaged();

        ThenSetManagedBreakpointsWasNotCalled();
    }

    [Fact]
    public void TryInitializeManaged_WhenSucceeds_AppliesPendingBreakpointsAndSendsEvents()
    {
        GivenRuntimeWillInitialize();
        GivenPendingManagedBreakpoints(
            new SetBreakpointsArguments
            {
                Source = new Source { Path = @"C:\src\Program.cs" },
                Breakpoints = [new SourceBreakpoint { Line = 10 }],
            });
        GivenSetManagedBreakpointsReturns(@"C:\src\Program.cs",
            [new Breakpoint { Id = 1, Verified = true, Line = 10 }]);

        WhenTryingInitializeManaged();

        ThenSetManagedBreakpointsWasCalled(@"C:\src\Program.cs");
        ThenBreakpointEventWasSent();
        ThenPendingManagedBreakpointsAreCleared();
    }

    [Fact]
    public void TryInitializeManaged_WhenDeferredBPsExistAndNoProfilerPipe_StartsPoller()
    {
        GivenRuntimeWillInitialize();
        GivenDeferredManagedBreakpoints(
            new DeferredManagedBreakpoint("file.cs", 10, 0x06000001, 0, 1, "Asm"));
        GivenNoProfilerPipe();

        WhenTryingInitializeManaged();

        ThenStartDeferredBreakpointPollerWasCalled();
    }

    [Fact]
    public void TryInitializeManaged_WhenDeferredBPsExistAndProfilerPipeIsSet_DoesNotStartPoller()
    {
        GivenRuntimeWillInitialize();
        GivenDeferredManagedBreakpoints(
            new DeferredManagedBreakpoint("file.cs", 10, 0x06000001, 0, 1, "Asm"));
        GivenProfilerPipeIsSet();

        WhenTryingInitializeManaged();

        ThenStartDeferredBreakpointPollerWasNotCalled();
    }

    [Fact]
    public void TryInitializeManaged_WhenNoDeferredBPs_DoesNotStartPoller()
    {
        GivenRuntimeWillInitialize();
        GivenNoProfilerPipe();

        WhenTryingInitializeManaged();

        ThenStartDeferredBreakpointPollerWasNotCalled();
    }

    // ── GetManagedStackFrames ──────────────────────────────

    [Fact]
    public void GetManagedStackFrames_WhenNotInitialized_ReturnsEmpty()
    {
        GivenManagedNotInitialized();

        WhenGettingManagedStackFrames();

        ThenStackFramesAreEmpty();
    }

    [Fact]
    public void GetManagedStackFrames_WhenNoRawFrames_ReturnsEmpty()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234, []);

        WhenGettingManagedStackFrames();

        ThenStackFramesAreEmpty();
    }

    [Fact]
    public void GetManagedStackFrames_WithSourceResolution_ReturnsMappedFrames()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234,
        [
            new RawManagedFrame(0x06000001, @"C:\app\MyLib.dll", 5, "MyClass.MyMethod"),
        ]);
        GivenPdbSourceLocation(@"C:\app\MyLib.dll", 0x06000001, 5, ("Program.cs", 42));

        WhenGettingManagedStackFrames();

        ThenStackFrameCountIs(1);
        ThenStackFrameAt(0, hasName: "MyClass.MyMethod", hasSource: "Program.cs", hasLine: 42);
    }

    [Fact]
    public void GetManagedStackFrames_WithZeroILOffset_PassesOneToMapper()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234,
        [
            new RawManagedFrame(0x06000001, @"C:\app\MyLib.dll", 0, "Method"),
        ]);
        // IL offset 0 should be passed as 1 to the PDB mapper.
        GivenPdbSourceLocation(@"C:\app\MyLib.dll", 0x06000001, 1, ("File.cs", 10));

        WhenGettingManagedStackFrames();

        ThenStackFrameCountIs(1);
        ThenStackFrameAt(0, hasName: "Method", hasSource: "File.cs", hasLine: 10);
    }

    [Fact]
    public void GetManagedStackFrames_WithNegativeILOffset_PassesOneToMapper()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234,
        [
            new RawManagedFrame(0x06000001, @"C:\app\MyLib.dll", -1, "Method"),
        ]);
        GivenPdbSourceLocation(@"C:\app\MyLib.dll", 0x06000001, 1, ("File.cs", 10));

        WhenGettingManagedStackFrames();

        ThenStackFrameCountIs(1);
        ThenStackFrameAt(0, hasName: "Method", hasSource: "File.cs", hasLine: 10);
    }

    [Fact]
    public void GetManagedStackFrames_WhenModulePathIsNull_ReturnsFrameWithoutSource()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234,
        [
            new RawManagedFrame(0x06000001, null, 5, "UnknownMethod"),
        ]);

        WhenGettingManagedStackFrames();

        ThenStackFrameCountIs(1);
        ThenStackFrameAt(0, hasName: "UnknownMethod", hasSource: null, hasLine: 0);
    }

    [Fact]
    public void GetManagedStackFrames_WhenPdbReturnsNull_ReturnsFrameWithoutSource()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234,
        [
            new RawManagedFrame(0x06000001, @"C:\app\MyLib.dll", 5, "SomeMethod"),
        ]);
        GivenPdbSourceLocationReturnsNull(@"C:\app\MyLib.dll", 0x06000001, 5);

        WhenGettingManagedStackFrames();

        ThenStackFrameCountIs(1);
        ThenStackFrameAt(0, hasName: "SomeMethod", hasSource: null, hasLine: 0);
    }

    [Fact]
    public void GetManagedStackFrames_MultipleFrames_AssignsIncrementingIds()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1234);
        GivenRawManagedFrames(1234,
        [
            new RawManagedFrame(0x06000001, null, 0, "Frame1"),
            new RawManagedFrame(0x06000002, null, 0, "Frame2"),
            new RawManagedFrame(0x06000003, null, 0, "Frame3"),
        ]);

        WhenGettingManagedStackFrames();

        ThenStackFrameCountIs(3);
        ThenStackFrameIdAt(0, 1);
        ThenStackFrameIdAt(1, 2);
        ThenStackFrameIdAt(2, 3);
    }

    // ── ResolveFrameFromProfilerData ───────────────────────

    [Fact]
    public void ResolveFrameFromProfilerData_WhenJitMapEmpty_ReturnsNull()
    {
        WhenResolvingFrameFromProfilerData(0x1000);

        ThenResolvedFrameIsNull();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenIPNotInAnyMethod_ReturnsNull()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));

        WhenResolvingFrameFromProfilerData(0x2000);

        ThenResolvedFrameIsNull();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenIPBelowAllMethods_ReturnsNull()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));

        WhenResolvingFrameFromProfilerData(0x0500);

        ThenResolvedFrameIsNull();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenIPExactlyAtMethodEnd_ReturnsNull()
    {
        // Method starts at 0x1000, size 100 = 0x64, so end is 0x1064. IP at 0x1064 is past the method.
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));

        WhenResolvingFrameFromProfilerData(0x1064);

        ThenResolvedFrameIsNull();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenIPInsideMethod_ResolvesFromPdb()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocation(@"C:\app\TestAsm.dll", 0x06000001, 0, ("MyFile.cs", 25));
        GivenPdbMethodName(@"C:\app\TestAsm.dll", 0x06000001, "MyNamespace.MyClass.Run");

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameHasName("MyNamespace.MyClass.Run");
        ThenResolvedFrameHasSource("MyFile.cs", 25);
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenIPAtMethodStart_ResolvesMethod()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocation(@"C:\app\TestAsm.dll", 0x06000001, 0, ("MyFile.cs", 1));
        GivenPdbMethodName(@"C:\app\TestAsm.dll", 0x06000001, "Entry");

        WhenResolvingFrameFromProfilerData(0x1000);

        ThenResolvedFrameHasName("Entry");
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenCorWrapperIsNull_ReturnsDefaultName()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        _model.CorWrapper = null!;

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameIsNotNull();
        ThenResolvedFrameHasName("[TestAsm] 0x06000001");
        ThenResolvedFrameHasNoSource();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenModuleNotFound_SearchesFallbackPaths()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", null);
        GivenCorDebugGetModulesReturns([]);

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameIsNotNull();
        ThenResolvedFrameHasName("[TestAsm] 0x06000001");
        ThenResolvedFrameHasNoSource();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenModuleNotFoundButFallbackDllExists_ResolvesFromFallback()
    {
        // Create a temp DLL so File.Exists returns true in the fallback path.
        string tempDir = Path.Combine(Path.GetTempPath(), $"mixdbg_fallback_{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(tempDir);
        string fakeDll = Path.Combine(tempDir, "FallbackAsm.dll");
        File.WriteAllText(fakeDll, "fake");
        try
        {
            // Module named "OtherMod" lives in tempDir; we're looking for "FallbackAsm".
            string otherPath = Path.Combine(tempDir, "OtherMod.dll");
            File.WriteAllText(otherPath, "fake");

            GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "FallbackAsm"));
            GivenCorWrapperInitialized();
            GivenCorDebugFindModuleByName("FallbackAsm", null);
            GivenCorDebugGetModulesReturns([new ManagedModuleInfo(otherPath, null, 0)]);
            GivenPdbSourceLocation(fakeDll, 0x06000001, 0, ("Fallback.cs", 99));
            GivenPdbMethodName(fakeDll, 0x06000001, "FallbackMethod");

            WhenResolvingFrameFromProfilerData(0x1010);

            ThenResolvedFrameHasName("FallbackMethod");
            ThenResolvedFrameHasSource("Fallback.cs", 99);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenModulePathIsNull_SkipsFallbackEntry()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", null);
        // Module with null path should be skipped in fallback search.
        GivenCorDebugGetModulesReturns([new ManagedModuleInfo(null, null, 0)]);

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameIsNotNull();
        ThenResolvedFrameHasNoSource();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenPdbMethodNameIsNull_UsesDefaultName()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocation(@"C:\app\TestAsm.dll", 0x06000001, 0, ("MyFile.cs", 25));
        GivenPdbMethodNameReturnsNull(@"C:\app\TestAsm.dll", 0x06000001);

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameHasName("[TestAsm] 0x06000001");
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenPdbSourceIsNull_FallsBackToBreakpointSourceByMethodStart()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocationReturnsNull(@"C:\app\TestAsm.dll", 0x06000001, 0);
        GivenPdbMethodName(@"C:\app\TestAsm.dll", 0x06000001, "CliWrapper.Run");
        GivenManagedBreakpointSource(0x1000UL, (@"C:\src\Wrapper.cpp", 15));

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameHasName("CliWrapper.Run");
        ThenResolvedFrameHasSource("Wrapper.cpp", 15);
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenPdbSourceIsNull_FallsBackToBreakpointSourceByIP()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocationReturnsNull(@"C:\app\TestAsm.dll", 0x06000001, 0);
        GivenPdbMethodNameReturnsNull(@"C:\app\TestAsm.dll", 0x06000001);
        GivenManagedBreakpointSource(0x1020UL, (@"C:\src\Wrapper.cpp", 20));

        WhenResolvingFrameFromProfilerData(0x1020);

        ThenResolvedFrameHasSource("Wrapper.cpp", 20);
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenPdbSourceIsNull_AndNoBreakpointSource_ReturnsNoSource()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocationReturnsNull(@"C:\app\TestAsm.dll", 0x06000001, 0);
        GivenPdbMethodNameReturnsNull(@"C:\app\TestAsm.dll", 0x06000001);

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameIsNotNull();
        ThenResolvedFrameHasNoSource();
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WhenExceptionInPdbResolution_ReturnsGracefully()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenPdbSourceLocationThrows(@"C:\app\TestAsm.dll", 0x06000001, new InvalidOperationException("PDB corrupt"));

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameIsNotNull();
        ThenResolvedFrameHasName("[TestAsm] 0x06000001");
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WithILToNativeMapping_ComputesCorrectILOffset()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 200, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        GivenJitMethodMapping("TestAsm", 0x06000001, 0x1000UL,
        [
            (0, 0),    // IL 0  -> native 0
            (10, 20),  // IL 10 -> native 20
            (25, 50),  // IL 25 -> native 50
        ]);
        // IP is at 0x1000 + 30 = offset 30 into the method.
        // Largest mapping entry with native start <= 30 is (10, 20).
        GivenPdbSourceLocation(@"C:\app\TestAsm.dll", 0x06000001, 10, ("Mapped.cs", 77));
        GivenPdbMethodName(@"C:\app\TestAsm.dll", 0x06000001, "MappedMethod");

        WhenResolvingFrameFromProfilerData(0x1000 + 30);

        ThenResolvedFrameHasName("MappedMethod");
        ThenResolvedFrameHasSource("Mapped.cs", 77);
    }

    [Fact]
    public void ResolveFrameFromProfilerData_WithILToNativeMapping_NoMatchingEntry_ReturnsILOffsetZero()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 200, "TestAsm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("TestAsm", new ManagedModuleInfo(@"C:\app\TestAsm.dll", null, 0));
        // Mapping has no entries — IL offset stays 0.
        GivenJitMethodMapping("TestAsm", 0x06000001, 0x1000UL, []);
        GivenPdbSourceLocation(@"C:\app\TestAsm.dll", 0x06000001, 0, ("Zero.cs", 1));
        GivenPdbMethodName(@"C:\app\TestAsm.dll", 0x06000001, "ZeroMethod");

        WhenResolvingFrameFromProfilerData(0x1010);

        ThenResolvedFrameHasName("ZeroMethod");
        ThenResolvedFrameHasSource("Zero.cs", 1);
    }

    [Fact]
    public void ResolveFrameFromProfilerData_BinarySearch_FindsCorrectMethodAmongMultiple()
    {
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 50, "Asm"));
        GivenJitMethodMapEntry(0x2000UL, new JitMethodInfo(0x06000002, 0x2000, 100, "Asm"));
        GivenJitMethodMapEntry(0x3000UL, new JitMethodInfo(0x06000003, 0x3000, 200, "Asm"));
        GivenCorWrapperInitialized();
        GivenCorDebugFindModuleByName("Asm", new ManagedModuleInfo(@"C:\app\Asm.dll", null, 0));
        GivenPdbMethodName(@"C:\app\Asm.dll", 0x06000002, "MiddleMethod");
        GivenPdbSourceLocation(@"C:\app\Asm.dll", 0x06000002, 0, ("Middle.cs", 50));

        WhenResolvingFrameFromProfilerData(0x2020);

        ThenResolvedFrameHasName("MiddleMethod");
    }

    [Fact]
    public void ResolveFrameFromProfilerData_BinarySearch_IPBetweenMethods_ReturnsNull()
    {
        // Method1: 0x1000..0x1032 (size 50), Method2: 0x2000..0x2064 (size 100).
        // IP at 0x1100 is between them — not in either method.
        GivenJitMethodMapEntry(0x1000UL, new JitMethodInfo(0x06000001, 0x1000, 50, "Asm"));
        GivenJitMethodMapEntry(0x2000UL, new JitMethodInfo(0x06000002, 0x2000, 100, "Asm"));

        WhenResolvingFrameFromProfilerData(0x1100);

        ThenResolvedFrameIsNull();
    }

    // ── MergeManagedFrames ─────────────────────────────────

    [Fact]
    public void MergeManagedFrames_WhenNoManagedFrames_DoesNothing()
    {
        GivenManagedNotInitialized();
        StackFrame[] nativeFrames = [MakeNativeFrame(1, "main")];

        WhenMergingManagedFrames(nativeFrames);

        ThenFrameNameIs(nativeFrames, 0, "main");
    }

    [Fact]
    public void MergeManagedFrames_OverlaysOntoHexFrames()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "ManagedMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            MakeNativeFrame(1, "0x00007FF12345"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        ThenFrameNameIs(nativeFrames, 0, "ManagedMethod");
    }

    [Fact]
    public void MergeManagedFrames_OverlaysOntoCoreclrFrames()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "ManagedMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            MakeNativeFrame(1, "coreclr!CallDescrWorkerInternal"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        ThenFrameNameIs(nativeFrames, 0, "ManagedMethod");
    }

    [Fact]
    public void MergeManagedFrames_OverlaysOntoClrjitFrames()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "JittedMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            MakeNativeFrame(1, "clrjit!CILJit::compileMethod"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        ThenFrameNameIs(nativeFrames, 0, "JittedMethod");
    }

    [Fact]
    public void MergeManagedFrames_OverlaysOntoClrFrames()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "ClrMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            MakeNativeFrame(1, "clr!ThePreStub"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        ThenFrameNameIs(nativeFrames, 0, "ClrMethod");
    }

    [Fact]
    public void MergeManagedFrames_SkipsFramesWithSourceAlreadyResolved()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "ManagedMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            new StackFrame
            {
                Id = 1,
                Name = "NativeFunc",
                Source = new Source { Name = "native.cpp", Path = @"C:\native.cpp" },
                Line = 10,
            },
            MakeNativeFrame(2, "0x00007FF12345"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        // The first frame has source, so it is skipped. Managed frame maps to the second.
        ThenFrameNameIs(nativeFrames, 0, "NativeFunc");
        ThenFrameNameIs(nativeFrames, 1, "ManagedMethod");
    }

    [Fact]
    public void MergeManagedFrames_SkipsNativeFramesThatDontLookManaged()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "ManagedMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            MakeNativeFrame(1, "ntdll!RtlUserThreadStart"),
            MakeNativeFrame(2, "0x00007FF12345"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        // First frame doesn't look managed. Managed frame maps to the second.
        ThenFrameNameIs(nativeFrames, 0, "ntdll!RtlUserThreadStart");
        ThenFrameNameIs(nativeFrames, 1, "ManagedMethod");
    }

    [Fact]
    public void MergeManagedFrames_StopsWhenManagedFramesExhausted()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "OnlyManaged"),
        ]);
        StackFrame[] nativeFrames =
        [
            MakeNativeFrame(1, "0xAAAA"),
            MakeNativeFrame(2, "0xBBBB"),
        ];

        WhenMergingManagedFrames(nativeFrames);

        // Only one managed frame, so only the first matching native frame is overlaid.
        ThenFrameNameIs(nativeFrames, 0, "OnlyManaged");
        ThenFrameNameIs(nativeFrames, 1, "0xBBBB");
    }

    [Fact]
    public void MergeManagedFrames_HandlesNullFrameName()
    {
        GivenManagedInitialized();
        GivenCurrentThreadSystemId(1);
        GivenRawManagedFrames(1,
        [
            new RawManagedFrame(0x06000001, null, 0, "ManagedMethod"),
        ]);
        StackFrame[] nativeFrames =
        [
            new StackFrame { Id = 1, Name = null! },
        ];

        WhenMergingManagedFrames(nativeFrames);

        // null name doesn't start with "0x" or contain "coreclr!" — skipped.
        ThenFrameNameIs(nativeFrames, 0, null!);
    }

    #region Given

    private void GivenManagedNotInitialized() => _model.ManagedInitialized = false;

    private void GivenManagedInitialized() => _model.ManagedInitialized = true;

    private void GivenCoreClrPath(string? path) => _model.CoreClrPath = path;

    private void GivenCoreClrBaseAddress(ulong addr) => _model.CoreClrBaseAddress = addr;

    private void GivenCorDebugCreateModelReturnsNewModel()
        => _corDebug.CreateModel().Returns(new CorDebugWrapperModel());

    private void GivenInitializeProcessReturns(bool result)
        => _corDebug.InitializeProcess(
            Arg.Any<CorDebugWrapperModel>(), Arg.Any<DbgEngWrapperModel>(),
            Arg.Any<string>(), Arg.Any<ulong>()).Returns(result);

    private void GivenDacInitThrows(Exception ex)
        => _corDebug.InitializeDac(
            Arg.Any<CorDebugWrapperModel>(), Arg.Any<DbgEngWrapperModel>(),
            Arg.Any<string>(), Arg.Any<ulong>()).Throws(ex);

    private void GivenRuntimeWillInitialize()
    {
        GivenCoreClrPath(@"C:\coreclr.dll");
        GivenCoreClrBaseAddress(0x7FF00000UL);
        GivenCorDebugCreateModelReturnsNewModel();
        GivenInitializeProcessReturns(true);
    }

    private void GivenPendingManagedBreakpoints(params SetBreakpointsArguments[] pending)
    {
        foreach (SetBreakpointsArguments p in pending)
            _model.PendingManagedBreakpoints.Add(p);
    }

    private void GivenSetManagedBreakpointsReturns(string filePath, Breakpoint[] breakpoints)
        => _bpService.SetManagedBreakpoints(_model, filePath, Arg.Any<SourceBreakpoint[]>())
            .Returns(breakpoints);

    private void GivenDeferredManagedBreakpoints(params DeferredManagedBreakpoint[] deferred)
    {
        foreach (DeferredManagedBreakpoint d in deferred)
            _model.DeferredManagedBreakpoints.Add(d);
    }

    private void GivenNoProfilerPipe() => _model.ProfilerPipe = null;

    private void GivenProfilerPipeIsSet()
        => _model.ProfilerPipe = new System.IO.Pipes.NamedPipeServerStream(
            $"MixDbgTest-{Guid.NewGuid():N}", System.IO.Pipes.PipeDirection.In);

    private void GivenCurrentThreadSystemId(uint osId)
        => _dbgEng.GetCurrentThreadSystemId(_model.Wrapper).Returns(osId);

    private void GivenRawManagedFrames(uint osId, RawManagedFrame[] frames)
        => _corDebug.GetRawManagedFrames(Arg.Any<CorDebugWrapperModel>(), osId).Returns(frames);

    private void GivenPdbSourceLocation(string path, int token, int ilOffset, (string File, int Line) result)
        => _pdbMapper.GetSourceLocation(path, token, ilOffset).Returns(result);

    private void GivenPdbSourceLocationReturnsNull(string path, int token, int ilOffset)
        => _pdbMapper.GetSourceLocation(path, token, ilOffset).Returns((ValueTuple<string, int>?)null);

    private void GivenPdbSourceLocationThrows(string path, int token, Exception ex)
        => _pdbMapper.GetSourceLocation(path, token, Arg.Any<int>()).Throws(ex);

    private void GivenPdbMethodName(string path, int token, string name)
        => _pdbMapper.GetMethodName(path, token).Returns(name);

    private void GivenPdbMethodNameReturnsNull(string path, int token)
        => _pdbMapper.GetMethodName(path, token).Returns((string?)null);

    private void GivenJitMethodMapEntry(ulong address, JitMethodInfo info)
    {
        lock (_model.JitMethodMap)
            _model.JitMethodMap[address] = info;
    }

    private void GivenCorWrapperInitialized()
        => _model.CorWrapper = new CorDebugWrapperModel();

    private void GivenCorDebugFindModuleByName(string name, ManagedModuleInfo? result)
        => _corDebug.FindModuleByName(Arg.Any<CorDebugWrapperModel>(), name).Returns(result);

    private void GivenCorDebugGetModulesReturns(ManagedModuleInfo[] modules)
        => _corDebug.GetModules(Arg.Any<CorDebugWrapperModel>()).Returns(modules);

    private void GivenManagedBreakpointSource(ulong address, (string File, int Line) source)
        => _model.ManagedBreakpointSources[address] = source;

    private void GivenJitMethodMapping(string assemblyName, int token, ulong codeStart,
        List<(int ILOffset, int NativeOffset)> map)
    {
        string key = $"{assemblyName}:{token:X8}";
        _model.JitMethodMappings[key] = new JitMethodMapping
        {
            CodeStart = codeStart,
            ILToNativeMap = map,
        };
    }

    #endregion

    #region When

    private void WhenCheckingIsInitialized() => _isInitializedResult = _testee.IsInitialized(_model);

    private void WhenInitializingRuntime() => _runtimeResult = _testee.InitializeRuntime(_model);

    private void WhenTryingInitializeManaged() => _testee.TryInitializeManaged(_model);

    private void WhenGettingManagedStackFrames() => _stackFrames = _testee.GetManagedStackFrames(_model);

    private void WhenResolvingFrameFromProfilerData(ulong ip)
        => _resolvedFrame = _testee.ResolveFrameFromProfilerData(_model, ip);

    private void WhenMergingManagedFrames(StackFrame[] frames) => _testee.MergeManagedFrames(_model, frames);

    #endregion

    #region Then

    private void ThenIsInitializedIs(bool expected) => Assert.Equal(expected, _isInitializedResult);

    private void ThenRuntimeResultIs(bool expected) => Assert.Equal(expected, _runtimeResult);

    private void ThenModelIsManagedInitialized() => Assert.True(_model.ManagedInitialized);

    private void ThenModelIsNotManagedInitialized() => Assert.False(_model.ManagedInitialized);

    private void ThenCorDebugCreateModelWasNotCalled() => _corDebug.DidNotReceive().CreateModel();

    private void ThenFlushProcessStateWasCalled()
        => _corDebug.Received(1).FlushProcessState(Arg.Any<CorDebugWrapperModel>());

    private void ThenRefreshModulesWasCalled()
        => _corDebug.Received(1).RefreshModules(Arg.Any<CorDebugWrapperModel>());

    private void ThenInitializeDacWasCalled()
        => _corDebug.Received(1).InitializeDac(
            Arg.Any<CorDebugWrapperModel>(), Arg.Any<DbgEngWrapperModel>(),
            Arg.Any<string>(), Arg.Any<ulong>());

    private void ThenWarningWasLogged(string substring)
        => _log.Received().LogWarning(Arg.Any<LogStore>(), Arg.Is<string>(s => s.Contains(substring)),
            Arg.Any<string>());

    private void ThenSetManagedBreakpointsWasNotCalled()
        => _bpService.DidNotReceive().SetManagedBreakpoints(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<string>(), Arg.Any<SourceBreakpoint[]>());

    private void ThenSetManagedBreakpointsWasCalled(string filePath)
        => _bpService.Received(1).SetManagedBreakpoints(_model, filePath, Arg.Any<SourceBreakpoint[]>());

    private void ThenBreakpointEventWasSent()
        => _server.Received().SendEvent(_transport, "breakpoint", Arg.Any<BreakpointEventBody>());

    private void ThenPendingManagedBreakpointsAreCleared()
        => Assert.Empty(_model.PendingManagedBreakpoints);

    private void ThenStartDeferredBreakpointPollerWasCalled()
        => _bpResolver.Received(1).StartDeferredBreakpointPoller(_model);

    private void ThenStartDeferredBreakpointPollerWasNotCalled()
        => _bpResolver.DidNotReceive().StartDeferredBreakpointPoller(Arg.Any<NativeDebuggerModel>());

    private void ThenStackFramesAreEmpty() => Assert.Empty(_stackFrames!);

    private void ThenStackFrameCountIs(int count) => Assert.Equal(count, _stackFrames!.Length);

    private void ThenStackFrameAt(int index, string hasName, string? hasSource, int hasLine)
    {
        StackFrame frame = _stackFrames![index];
        Assert.Equal(hasName, frame.Name);
        if (hasSource != null)
        {
            Assert.NotNull(frame.Source);
            Assert.Equal(hasSource, frame.Source!.Name);
        }
        else
        {
            Assert.Null(frame.Source);
        }
        Assert.Equal(hasLine, frame.Line);
    }

    private void ThenStackFrameIdAt(int index, int expectedId)
        => Assert.Equal(expectedId, _stackFrames![index].Id);

    private void ThenResolvedFrameIsNull() => Assert.Null(_resolvedFrame);

    private void ThenResolvedFrameIsNotNull() => Assert.NotNull(_resolvedFrame);

    private void ThenResolvedFrameHasName(string expected)
        => Assert.Equal(expected, _resolvedFrame!.Value.Name);

    private void ThenResolvedFrameHasSource(string expectedName, int expectedLine)
    {
        Assert.NotNull(_resolvedFrame!.Value.Source);
        Assert.Equal(expectedName, _resolvedFrame.Value.Source!.Name);
        Assert.Equal(expectedLine, _resolvedFrame.Value.Line);
    }

    private void ThenResolvedFrameHasNoSource()
        => Assert.Null(_resolvedFrame!.Value.Source);

    private static void ThenFrameNameIs(StackFrame[] frames, int index, string expected)
        => Assert.Equal(expected, frames[index].Name);

    #endregion

    #region Helpers

    private static StackFrame MakeNativeFrame(int id, string name)
        => new() { Id = id, Name = name, Source = null, Line = 0, Column = 0 };

    #endregion

    #region Fields

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore = new(Path.Combine(Path.GetTempPath(), "test.log"));
    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly DapServerModel _transport = new(Stream.Null, Stream.Null);
    private readonly IDbgEngWrapper _dbgEng = Substitute.For<IDbgEngWrapper>();
    private readonly ICorDebugWrapper _corDebug = Substitute.For<ICorDebugWrapper>();
    private readonly IPdbSourceMapper _pdbMapper = Substitute.For<IPdbSourceMapper>();
    private readonly IManagedBreakpointService _bpService = Substitute.For<IManagedBreakpointService>();
    private readonly IManagedBreakpointResolver _bpResolver = Substitute.For<IManagedBreakpointResolver>();
    private readonly NativeDebuggerModel _model;
    private readonly ManagedDebuggerService _testee;

    private bool _isInitializedResult;
    private bool _runtimeResult;
    private StackFrame[]? _stackFrames;
    private (string Name, Source? Source, int Line)? _resolvedFrame;

    public ManagedDebuggerServiceTests()
    {
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
        _testee = new ManagedDebuggerService(
            _log, _logStore, _server, _transport,
            _dbgEng, _corDebug, _pdbMapper, _bpService, _bpResolver);
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
