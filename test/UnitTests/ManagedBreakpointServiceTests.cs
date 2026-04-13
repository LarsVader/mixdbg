using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MixDbg.Tests;

public sealed class ManagedBreakpointServiceTests : IDisposable
{
    // ── SetManagedBreakpoints ────────────────────────────────

    [Fact]
    public void SetManagedBreakpoints_WhenBindSucceeds_ReturnsVerifiedBreakpoints()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0xABCD);
        GivenAddHardwareBreakpointSucceeds(0xABCD, bpId: 1);

        WhenSettingManagedBreakpoints(@"C:\src\Program.cs", [10]);

        ThenResultCountIs(1);
        ThenResultAtIndexIsVerified(0, true);
        ThenResultAtIndexHasLine(0, 10);
        ThenResultAtIndexHasNoMessage(0);
    }

    [Fact]
    public void SetManagedBreakpoints_WhenBindFails_ReturnsPendingBreakpoint()
    {
        string src = GivenSourceFileOnDisk();
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        WhenSettingManagedBreakpoints(src, [15]);

        ThenResultCountIs(1);
        ThenResultAtIndexIsVerified(0, true);
        ThenResultAtIndexHasMessage(0, "Pending — module not yet loaded");
        ThenPendingILBreakpointCountIs(1);
    }

    [Fact]
    public void SetManagedBreakpoints_WhenMultipleLines_ReturnsAllResults()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLineForAny(@"C:\out\MyApp.dll",
            ("MyApp", "MethodA", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0x1000);
        GivenAddHardwareBreakpointSucceedsSequential([1, 2, 3]);

        WhenSettingManagedBreakpoints(@"C:\src\Program.cs", [10, 20, 30]);

        ThenResultCountIs(3);
        ThenAllResultsAreVerified(true);
    }

    [Fact]
    public void SetManagedBreakpoints_WhenCalledAgain_ClearsExistingBreakpointsForFile()
    {
        string src = GivenSourceFileOnDisk();
        GivenExistingManagedBreakpointForFile(src, line: 10, hwBpId: 5, bpId: 1);
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        WhenSettingManagedBreakpoints(src, [20]);

        ThenRemoveBreakpointWasCalled(5);
        ThenDeactivateLegacyBreakpointWasCalled(1);
    }

    [Fact]
    public void SetManagedBreakpoints_ClearsPendingAndDeferredForFile()
    {
        string src = GivenSourceFileOnDisk();
        GivenPendingILBreakpoint(src, 10, 1);
        GivenDeferredManagedBreakpoint(src, 20, 0x06000001, 2);
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        WhenSettingManagedBreakpoints(src, [30]);

        // Old pending (line 10) and deferred (line 20) are removed.
        // A new pending (line 30) is added because bind fails.
        ThenPendingILBreakpointsDoNotContainLine(src, 10);
        ThenDeferredBreakpointsDoNotContainFile(src);
    }

    // ── TryBindBreakpoint ────────────────────────────────────

    [Fact]
    public void TryBindBreakpoint_WhenLoadedModuleResolves_AndMethodIsJitd_SetsHardwareBp()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0x5000);
        GivenAddHardwareBreakpointSucceeds(0x5000, bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 100);

        Assert.True(result);
        ThenManagedBreakpointIdsContains(42);
        ThenUserBreakpointIdsContains(42);
        ThenManagedBreakpointAddressesContains(0x5000);
        ThenBreakpointSourcesContains(0x5000, @"C:\src\Program.cs", 10);
    }

    /// <summary>
    /// Reproduces bug: GetOffsetByLine returns bogus IL stub addresses for C# managed
    /// methods (e.g. 0x21AF62D026E instead of JIT'd code at 0x7FF7B6C8xxxx).
    /// When the method IS already in JitMethodMap (profiler reported correct address),
    /// BindResolvedMethod must use the JitMethodMap address with IL-to-native mapping,
    /// NOT the bogus GetOffsetByLine address.
    /// </summary>
    [Fact]
    public void TryBindBreakpoint_WhenMethodInJitMethodMap_UsesJitAddressNotGetOffsetByLine()
    {
        // Method resolved from loaded module PDB → token 0x06000010, IL offset 0x20.
        GivenLoadedModule(@"C:\out\WpfApp.dll", @"C:\out\WpfApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\WpfApp.dll", @"C:\src\MainWindow.xaml.cs", 65,
            ("WpfApp", "OnAddClick", 0x06000010, 0x20));

        // GetOffsetByLine returns a bogus IL stub address (the bug we're fixing).
        GivenGetOffsetByLineSucceeds(@"C:\src\MainWindow.xaml.cs", 65, 0x21AF62D026E);

        // Method is already JIT'd — profiler reported the correct native address.
        ulong correctJitStart = 0x7FF7B6C82EC0;
        lock (_model.JitMethodMap)
            _model.JitMethodMap[correctJitStart] = new JitMethodInfo(0x06000010, correctJitStart, 0x200, "WpfApp");
        _model.JitMethodMappings[(0x06000010, "WpfApp")] = new JitMethodMapping(
            correctJitStart, [(0x00, 0x00), (0x10, 0x30), (0x20, 0x70), (0x30, 0xA0)]);

        // Accept any address for the HW BP.
        GivenAddHardwareBreakpointSucceedsForAnyAddress(bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\MainWindow.xaml.cs", 65, bpId: 100);

        Assert.True(result);
        // BP must be at JIT'd code start + native offset for IL 0x20, NOT the bogus stub address.
        ulong expectedAddress = correctJitStart + 0x70; // IL 0x20 → native offset 0x70
        ThenAddHardwareBreakpointWasCalledWith(expectedAddress);
        ThenManagedBreakpointAddressesContains(expectedAddress);
    }

    [Fact]
    public void TryBindBreakpoint_WhenLoadedModuleResolves_ButNotJitd_StoresDeferred()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 5));
        GivenGetOffsetByLineFails(@"C:\src\Program.cs", 10);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 200);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
        ThenDeferredBreakpointHasToken(0, 0x06000001);
        ThenDeferredBreakpointHasILOffset(0, 5);
    }

    [Fact]
    public void TryBindBreakpoint_WhenDiskPdbResolves_StoresDeferred()
    {
        GivenNoLoadedModules();
        GivenDiskPdbResolvesMethod(10);

        bool result = WhenTryingToBindBreakpoint(_diskSourceFile!, 10, bpId: 300);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
    }

    [Fact]
    public void TryBindBreakpoint_WhenCliFileResolves_StoresDeferred()
    {
        GivenNoLoadedModules();
        GivenCliProjectExists(); // Sets up temp vcxproj, _cliSourcePath, _cliDllPath
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenFindTokenByRvaReturns(_cliDllPath!, (int)(0x7000 - 0x1000), 0x06000099);

        bool result = WhenTryingToBindBreakpoint(_cliSourcePath!, 25, bpId: 400);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
        ThenDeferredBreakpointIsCliMethod(0, true);
    }

    [Fact]
    public void TryBindBreakpoint_WhenLoadedModuleHasNullPdbPath_SkipsModule()
    {
        // Module with null PdbPath is skipped (line 59 continue).
        // Second module matches.
        GivenLoadedModules(
            new ManagedModuleInfo(null, null, 0),
            new ManagedModuleInfo(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb", 0));
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenGetOffsetByLineFails(@"C:\src\Program.cs", 10);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 150);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
    }

    [Fact]
    public void TryBindBreakpoint_WhenLoadedModuleDoesNotMatch_FallsThrough()
    {
        // Module has PDB but FindMethodAtLine returns null — falls through to disk PDB.
        string src = GivenSourceFileOnDisk();
        GivenLoadedModule(@"C:\out\Other.dll", @"C:\out\Other.pdb");
        _ = _pdbMapper.FindMethodAtLine(@"C:\out\Other.dll", Arg.Any<string>(), Arg.Any<int>())
            .Returns(((string, string, int, int)?)null);
        GivenSourceFileIsNotCli(src);

        bool result = WhenTryingToBindBreakpoint(src, 10, bpId: 160);

        Assert.False(result);
    }

    [Fact]
    public void TryBindBreakpoint_WhenAllStrategiesFail_ReturnsFalse()
    {
        string src = GivenSourceFileOnDisk();
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        bool result = WhenTryingToBindBreakpoint(src, 10, bpId: 500);

        Assert.False(result);
    }

    [Fact]
    public void TryBindBreakpoint_WhenCliFile_ButGetOffsetFails_ReturnsFalse()
    {
        string src = GivenSourceFileOnDisk("Wrapper.cpp");
        GivenNoLoadedModules();
        GivenSourceFileIsCli(src);
        GivenGetOffsetByLineFails(src, 25);

        bool result = WhenTryingToBindBreakpoint(src, 25, bpId: 600);

        Assert.False(result);
    }

    [Fact]
    public void TryBindBreakpoint_WhenCliFile_ButModuleByOffsetFails_ReturnsFalse()
    {
        string src = GivenSourceFileOnDisk("Wrapper.cpp");
        GivenNoLoadedModules();
        GivenSourceFileIsCli(src);
        GivenGetOffsetByLineSucceeds(src, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, null);

        bool result = WhenTryingToBindBreakpoint(src, 25, bpId: 700);

        Assert.False(result);
    }

    // ── SetManagedCodeBreakpoint ─────────────────────────────

    [Fact]
    public void SetManagedCodeBreakpoint_WhenSuccess_TracksBreakpoint()
    {
        GivenAddHardwareBreakpointSucceeds(0x9000, bpId: 10);

        uint? result = WhenSettingManagedCodeBreakpoint(0x9000, @"C:\src\File.cs", 42);

        Assert.Equal(10u, result);
        ThenBreakpointIdIsTracked(@"C:\src\File.cs:42", 10);
        ThenUserBreakpointIdsContains(10);
        ThenManagedBreakpointIdsContains(10);
        ThenManagedBreakpointAddressesContains(0x9000);
        ThenBreakpointSourcesContains(0x9000, @"C:\src\File.cs", 42);
    }

    [Fact]
    public void SetManagedCodeBreakpoint_WhenFails_ReturnsNull()
    {
        GivenAddHardwareBreakpointFails(0x9000);

        uint? result = WhenSettingManagedCodeBreakpoint(0x9000, @"C:\src\File.cs", 42);

        Assert.Null(result);
        ThenManagedBreakpointIdsIsEmpty();
    }

    // ── SetTransientBreakpoint ───────────────────────────────

    [Fact]
    public void SetTransientBreakpoint_DelegatesToSetManagedCodeBreakpoint()
    {
        GivenAddHardwareBreakpointSucceeds(0xBEEF, bpId: 77);

        WhenSettingTransientBreakpoint(0xBEEF, @"C:\src\File.cs", 100);

        ThenAddHardwareBreakpointWasCalledWith(0xBEEF);
        ThenManagedBreakpointIdsContains(77);
    }

    [Fact]
    public void SetTransientBreakpoint_WhenBpSet_ClearsLastContinuedBpId()
    {
        GivenAddHardwareBreakpointSucceeds(0xBEEF, bpId: 77);
        _model.LastContinuedBpId = 5;

        WhenSettingTransientBreakpoint(0xBEEF, @"C:\src\File.cs", 100);

        Assert.Equal(uint.MaxValue, _model.LastContinuedBpId);
    }

    // ── RemoveTransientManagedBreakpoints ────────────────────

    [Fact]
    public void RemoveTransientManagedBreakpoints_WhenHooksNotActive_DoesNothing()
    {
        _model.ProfilerHooksActive = false;
        GivenExistingManagedBreakpointId(10);

        WhenRemovingTransientBreakpoints();

        ThenRemoveBreakpointWasNotCalled();
        ThenManagedBreakpointIdsContains(10);
    }

    [Fact]
    public void RemoveTransientManagedBreakpoints_WhenNoManagedBps_DoesNothing()
    {
        _model.ProfilerHooksActive = true;

        WhenRemovingTransientBreakpoints();

        ThenRemoveBreakpointWasNotCalled();
    }

    [Fact]
    public void RemoveTransientManagedBreakpoints_WhenManagedBpHit_RemovesOnlyHitBp()
    {
        _model.ProfilerHooksActive = true;
        GivenExistingManagedBreakpointId(10);
        GivenExistingManagedBreakpointId(20);
        _ = _model.UserBreakpointIds.Add(10);
        _ = _model.UserBreakpointIds.Add(20);
        _ = _model.ManagedBreakpointAddresses.Add(0x1000);
        _ = _model.ManagedBreakpointAddresses.Add(0x2000);
        _model.BreakpointIds[@"C:\src\File.cs:10"] = 10;
        _model.BreakpointIds[@"C:\src\File.cs:20"] = 20;
        _model.LastHitBpId = 10;
        GivenRemoveBreakpointSucceeds(10);

        WhenRemovingTransientBreakpoints();

        ThenRemoveBreakpointWasCalled(10);
        ThenManagedBreakpointIdsContains(20);
        ThenUserBreakpointIdsDoesNotContain(10);
        Assert.Contains(20u, _model.UserBreakpointIds);
        ThenBreakpointIdKeyIsRemoved(@"C:\src\File.cs:10");
        Assert.True(_model.BreakpointIds.ContainsKey(@"C:\src\File.cs:20"));
    }

    [Fact]
    public void RemoveTransientManagedBreakpoints_WhenNativeBpHit_LeavesAllManagedBpsIntact()
    {
        _model.ProfilerHooksActive = true;
        GivenExistingManagedBreakpointId(10);
        GivenExistingManagedBreakpointId(20);
        _ = _model.UserBreakpointIds.Add(10);
        _ = _model.UserBreakpointIds.Add(20);
        _model.BreakpointIds[@"C:\src\File.cs:10"] = 10;
        _model.BreakpointIds[@"C:\src\File.cs:20"] = 20;
        _model.LastHitBpId = 99; // native BP, not in ManagedBreakpointIds

        WhenRemovingTransientBreakpoints();

        ThenRemoveBreakpointWasNotCalled();
        ThenManagedBreakpointIdsContains(10);
        ThenManagedBreakpointIdsContains(20);
    }

    // ── Permanent BP tracking (JitMethodMap path) ─────────────

    [Fact]
    public void TryBindBreakpoint_WhenMethodInJitMethodMap_MarksBpAsPermanent()
    {
        GivenLoadedModule(@"C:\out\WpfApp.dll", @"C:\out\WpfApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\WpfApp.dll", @"C:\src\Program.cs", 65,
            ("WpfApp", "OnAddClick", 0x06000010, 0));
        GivenMethodInJitMethodMap("WpfApp", 0x06000010, 0x7000);
        GivenAddHardwareBreakpointSucceeds(0x7000, bpId: 42);

        _ = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 65, bpId: 100);

        Assert.Contains(42u, _model.PermanentManagedBreakpointIds);
    }

    [Fact]
    public void RemoveTransientManagedBreakpoints_WhenPermanentBpHit_SkipsRemoval()
    {
        _model.ProfilerHooksActive = true;
        GivenExistingManagedBreakpointId(42);
        _ = _model.UserBreakpointIds.Add(42);
        _ = _model.PermanentManagedBreakpointIds.Add(42);
        _model.BreakpointIds[@"C:\src\File.cs:65"] = 42;
        _model.LastHitBpId = 42;

        WhenRemovingTransientBreakpoints();

        // Permanent BP must NOT be removed.
        ThenRemoveBreakpointWasNotCalled();
        ThenManagedBreakpointIdsContains(42);
        ThenUserBreakpointIdsContains(42);
    }

    [Fact]
    public void ClearManagedBreakpointsForFile_ClearsPermanentBreakpointIds()
    {
        string src = GivenSourceFileOnDisk();
        GivenExistingManagedBreakpointForFile(src, 65, hwBpId: 42, bpId: 1);
        _ = _model.PermanentManagedBreakpointIds.Add(42);
        GivenRemoveBreakpointSucceeds(42);
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        WhenSettingManagedBreakpoints(src, [70]); // triggers ClearManagedBreakpointsForFile

        Assert.DoesNotContain(42u, _model.PermanentManagedBreakpointIds);
    }

    // ── ResolveTokensFromBreakpoints ─────────────────────────

    [Fact]
    public void ResolveTokensFromBreakpoints_WhenNoMatches_ReturnsEmpty()
    {
        // No csproj on disk, so FindMethodFromDiskPdb returns null.
        (string FilePath, int Line)[] breakpoints = [(@"C:\nonexistent\File.cs", 10)];

        List<(string Assembly, int Token)> result = WhenResolvingTokens(breakpoints);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveTokensFromBreakpoints_WhenDiskPdbMatches_ReturnsTokens()
    {
        GivenDiskProjectWithPdb(out string sourceFile);
        _ = _pdbMapper.FindMethodAtLine(Arg.Any<string>(), sourceFile, 10)
            .Returns(("TestAssembly", "TestMethod", 0x06000042, 0));

        (string FilePath, int Line)[] breakpoints = [(sourceFile, 10)];

        List<(string Assembly, int Token)> result = WhenResolvingTokens(breakpoints);

        _ = Assert.Single(result);
        Assert.Equal("TestAssembly", result[0].Assembly);
        Assert.Equal(0x06000042, result[0].Token);
    }

    [Fact]
    public void ResolveTokensFromBreakpoints_WhenExceptionThrown_SwallowsAndContinues()
    {
        GivenDiskProjectWithPdb(out string sourceFile);
        _ = _pdbMapper.FindMethodAtLine(Arg.Any<string>(), sourceFile, 10)
            .Throws(new IOException("PDB locked"));

        (string FilePath, int Line)[] breakpoints = [(sourceFile, 10)];

        List<(string Assembly, int Token)> result = WhenResolvingTokens(breakpoints);

        Assert.Empty(result);
    }

    // ── ResolveWatchAssemblies ────────────────────────────────

    [Fact]
    public void ResolveWatchAssemblies_WhenNoCliFiles_ReturnsEmpty()
    {
        GivenSourceFileIsNotCli(@"C:\src\Program.cs");

        (string FilePath, int Line)[] breakpoints = [(@"C:\src\Program.cs", 10)];

        List<string> result = WhenResolvingWatchAssemblies(breakpoints);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveWatchAssemblies_WhenCliFileWithVcxproj_ReturnsAssemblyName()
    {
        GivenCliProjectForWatchAssembly(out string sourceFile, "CliWrapper");

        (string FilePath, int Line)[] breakpoints = [(sourceFile, 10)];

        List<string> result = WhenResolvingWatchAssemblies(breakpoints);

        _ = Assert.Single(result);
        Assert.Equal("CliWrapper", result[0]);
    }

    [Fact]
    public void ResolveWatchAssemblies_WhenDuplicateCliFiles_ReturnsDistinct()
    {
        GivenCliProjectForWatchAssembly(out string sourceFile, "CliWrapper");

        (string FilePath, int Line)[] breakpoints = [(sourceFile, 10), (sourceFile, 20)];

        List<string> result = WhenResolvingWatchAssemblies(breakpoints);

        _ = Assert.Single(result);
    }

    // ── ClearManagedBreakpointsForFile (CorWrapper deactivation) ──

    [Fact]
    public void SetManagedBreakpoints_WhenCorWrapperIsNull_SkipsDeactivation()
    {
        string src = GivenSourceFileOnDisk();
        GivenExistingManagedBreakpointForFile(src, line: 10, hwBpId: 5, bpId: 1);
        _model.CorWrapper = null!;
        GivenNoLoadedModulesForNullCorWrapper();
        GivenSourceFileIsNotCli(src);

        // Should not throw even though CorWrapper is null.
        WhenSettingManagedBreakpoints(src, [20]);

        ThenPendingILBreakpointCountIs(1);
    }

    // ── FindMethodFromDiskPdb (search in obj/ directory) ──

    [Fact]
    public void TryBindBreakpoint_WhenPdbFoundInObjDir_StoresDeferred()
    {
        GivenNoLoadedModules();
        GivenDiskProjectWithPdbInObj(out string sourceFile);
        _ = _pdbMapper.FindMethodAtLine(Arg.Any<string>(), sourceFile, 10)
            .Returns(("ObjAssembly", "ObjMethod", 0x06000055, 3));

        bool result = WhenTryingToBindBreakpoint(sourceFile, 10, bpId: 800);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
        ThenDeferredBreakpointHasToken(0, 0x06000055);
    }

    // ── FindMethodFromDiskPdb (PDB exists but no matching DLL) ──

    [Fact]
    public void TryBindBreakpoint_WhenPdbExistsButNoDll_ReturnsFalse()
    {
        GivenNoLoadedModules();
        GivenDiskProjectWithPdbButNoDll(out string sourceFile);
        GivenSourceFileIsNotCli(sourceFile);

        bool result = WhenTryingToBindBreakpoint(sourceFile, 10, bpId: 900);

        Assert.False(result);
    }

    // ── ResolveCliAssemblyName (no vcxproj found) ──

    [Fact]
    public void ResolveWatchAssemblies_WhenNoVcxprojFound_ReturnsEmpty()
    {
        string src = GivenSourceFileOnDisk("Wrapper.cpp");
        _ = _sourceFiles.IsCliFile(src).Returns(true);

        (string FilePath, int Line)[] breakpoints = [(src, 10)];

        List<string> result = WhenResolvingWatchAssemblies(breakpoints);

        Assert.Empty(result);
    }

    // ── FindCliAssemblyDll (assembly null) ──

    [Fact]
    public void TryBindBreakpoint_WhenCliFile_ButNoVcxprojForDll_ReturnsFalse()
    {
        // Source is in a temp dir with no vcxproj, so assemblyName resolves to null,
        // which means FindCliAssemblyDll returns null.
        string src = GivenSourceFileOnDisk("Wrapper.cpp");
        GivenNoLoadedModules();
        GivenSourceFileIsCli(src);
        GivenGetOffsetByLineSucceeds(src, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);

        bool result = WhenTryingToBindBreakpoint(src, 25, bpId: 950);

        Assert.False(result);
    }

    // ── FindCliAssemblyDll (search in sibling directories) ──

    [Fact]
    public void TryBindBreakpoint_WhenCliDllInSiblingBin_Resolves()
    {
        GivenNoLoadedModules();
        GivenCliProjectWithSiblingDll(); // Sets _cliSourcePath, _cliDllPath
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenFindTokenByRvaReturns(_cliDllPath!, (int)(0x7000 - 0x1000), 0x06000077);

        bool result = WhenTryingToBindBreakpoint(_cliSourcePath!, 25, bpId: 960);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
    }

    // ── SetOneManagedBreakpoint (TryBindBreakpoint fails) ──

    [Fact]
    public void SetManagedBreakpoints_WhenBindFails_ReturnsPendingWithTrackingForFile()
    {
        string src = GivenSourceFileOnDisk();
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        WhenSettingManagedBreakpoints(src, [15]);

        ThenResultCountIs(1);
        ThenResultAtIndexIsVerified(0, true);
        ThenResultAtIndexHasMessage(0, "Pending — module not yet loaded");
        // Verify that the breakpoint is tracked in ManagedFileBreakpointIds.
        Assert.True(_model.ManagedFileBreakpointIds.ContainsKey(src));
    }

    // ── BindResolvedMethod (hardware BP fails) ──

    [Fact]
    public void TryBindBreakpoint_WhenHwBpLimitReached_ReturnsFalse()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0xABCD);
        GivenAddHardwareBreakpointFails(0xABCD);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 970);

        Assert.False(result);
    }

    // ── FindCliAssemblyDll (FindTokenByRva returns null) ──

    [Fact]
    public void TryBindBreakpoint_WhenCliTokenNotFound_ReturnsFalse()
    {
        GivenNoLoadedModules();
        GivenCliProjectExists();
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        _ = _pdbMapper.FindTokenByRva(_cliDllPath!, (int)(0x7000 - 0x1000))
            .Returns((int?)null);

        bool result = WhenTryingToBindBreakpoint(_cliSourcePath!, 25, bpId: 980);

        Assert.False(result);
    }

    #region Given

    private void GivenLoadedModule(string path, string pdbPath)
        => _corDebug.GetModules(_model.CorWrapper)
            .Returns([new ManagedModuleInfo(path, pdbPath, 0)]);

    private void GivenLoadedModules(params ManagedModuleInfo[] modules)
        => _corDebug.GetModules(_model.CorWrapper)
            .Returns(modules);

    private void GivenNoLoadedModules()
        => _ = _corDebug.GetModules(_model.CorWrapper)
            .Returns([]);

    private void GivenPdbResolvesMethodAtLine(
        string assemblyPath, string sourceFile, int line,
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset) result)
        => _pdbMapper.FindMethodAtLine(assemblyPath, sourceFile, line)
            .Returns(result);

    private void GivenPdbResolvesMethodAtLineForAny(
        string assemblyPath,
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset) result)
        => _pdbMapper.FindMethodAtLine(assemblyPath, Arg.Any<string>(), Arg.Any<int>())
            .Returns(result);

    private void GivenGetOffsetByLineSucceeds(string file, int line, ulong offset)
        => _dbgEng.GetOffsetByLine(_model.Wrapper, (uint)line, file)
            .Returns((offset, true));

    private void GivenGetOffsetByLineSucceedsForAny(ulong offset)
        => _dbgEng.GetOffsetByLine(_model.Wrapper, Arg.Any<uint>(), Arg.Any<string>())
            .Returns((offset, true));

    private void GivenGetOffsetByLineFails(string file, int line)
        => _dbgEng.GetOffsetByLine(_model.Wrapper, (uint)line, file)
            .Returns((0UL, false));

    private void GivenAddHardwareBreakpointSucceeds(ulong address, uint bpId)
        => _dbgEng.AddHardwareBreakpoint(_model.Wrapper, address, 1)
            .Returns((bpId, true));

    private void GivenAddHardwareBreakpointSucceedsSequential(uint[] bpIds)
    {
        int idx = 0;
        _ = _dbgEng.AddHardwareBreakpoint(_model.Wrapper, Arg.Any<ulong>(), 1)
            .Returns(_ => (bpIds[idx++], true));
    }

    private void GivenMethodInJitMethodMap(string assembly, int token, ulong startAddress)
    {
        lock (_model.JitMethodMap)
            _model.JitMethodMap[startAddress] = new JitMethodInfo(token, startAddress, 0x200, assembly);
    }

    private void GivenAddHardwareBreakpointSucceedsForAnyAddress(uint bpId)
        => _dbgEng.AddHardwareBreakpoint(_model.Wrapper, Arg.Any<ulong>(), 1)
            .Returns((bpId, true));

    private void GivenAddHardwareBreakpointFails(ulong address)
        => _dbgEng.AddHardwareBreakpoint(_model.Wrapper, address, 1)
            .Returns((0u, false));

    private void GivenRemoveBreakpointSucceeds(uint bpId)
        => _dbgEng.RemoveBreakpoint(_model.Wrapper, bpId).Returns(true);

    private void GivenGetModuleByOffsetReturns(ulong offset, ulong? moduleBase)
        => _dbgEng.GetModuleByOffset(_model.Wrapper, offset).Returns(moduleBase);

    private void GivenFindTokenByRvaReturns(string dllPath, int rva, int token)
        => _pdbMapper.FindTokenByRva(dllPath, rva).Returns(token);

    private void GivenSourceFileIsCli(string path)
        => _sourceFiles.IsCliFile(path).Returns(true);

    private void GivenSourceFileIsNotCli(string path)
        => _sourceFiles.IsCliFile(path).Returns(false);

    private void GivenExistingManagedBreakpointForFile(string filePath, int line, uint hwBpId, int bpId)
    {
        string key = $"{filePath}:{line}";
        _model.BreakpointIds[key] = hwBpId;
        _ = _model.UserBreakpointIds.Add(hwBpId);
        _ = _model.ManagedBreakpointIds.Add(hwBpId);
        _model.ManagedFileBreakpointIds[filePath] = [bpId];
    }

    private void GivenExistingManagedBreakpointId(uint bpId)
        => _ = _model.ManagedBreakpointIds.Add(bpId);

    private string GivenSourceFileOnDisk(string name = "Program.cs")
    {
        // Create a source file in a unique temp subdir with no .csproj nearby,
        // so FindMethodFromDiskPdb safely walks up and finds nothing.
        string dir = Path.Combine(_tempDir, $"nosrc_{_nextDiskFileId++}", "sub1", "sub2");
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "// fake source");
        return path;
    }

    private void GivenPendingILBreakpoint(string filePath, int line, int bpId)
        => _model.PendingILBreakpoints.Add(new PendingManagedBreakpoint(filePath, line, bpId));

    private void GivenDeferredManagedBreakpoint(string filePath, int line, int token, int bpId)
    {
        _model.DeferredManagedBreakpoints.Add(
            new DeferredManagedBreakpoint(filePath, line, token, 0, bpId, "SomeAssembly"));
        _model.RebuildDeferredBreakpointIndex();
    }

    private void GivenCliProjectExists()
    {
        // Create a temp directory with a vcxproj for CLI resolution.
        string dir = Path.Combine(_tempDir, "CliProject");
        _ = Directory.CreateDirectory(dir);
        string vcxproj = Path.Combine(dir, "CliWrapper.vcxproj");
        File.WriteAllText(vcxproj, "<Project><CLRSupport>true</CLRSupport></Project>");

        // Create bin directory with a DLL.
        string binDir = Path.Combine(dir, "bin", "Debug");
        _ = Directory.CreateDirectory(binDir);
        string dllPath = Path.Combine(binDir, "CliWrapper.dll");
        File.WriteAllText(dllPath, "fake-dll");
        _cliDllPath = dllPath;

        // Source file must be in the same dir as the vcxproj.
        _cliSourcePath = Path.Combine(dir, "Wrapper.cpp");
        File.WriteAllText(_cliSourcePath, "// fake source");

        _ = _sourceFiles.IsCliFile(_cliSourcePath).Returns(true);
    }

    private void GivenDiskProjectWithPdb(out string sourceFile)
    {
        // Create temp csproj + bin/PDB + DLL.
        string projDir = Path.Combine(_tempDir, "TestProject");
        _ = Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "TestProject.csproj"), "<Project></Project>");

        string binDir = Path.Combine(projDir, "bin", "Debug");
        _ = Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "TestProject.pdb"), "fake-pdb");
        File.WriteAllText(Path.Combine(binDir, "TestProject.dll"), "fake-dll");

        sourceFile = Path.Combine(projDir, "Program.cs");
        File.WriteAllText(sourceFile, "// fake source");
    }

    private void GivenDiskPdbResolvesMethod(int line)
    {
        // This requires a temp csproj + bin/PDB on disk for FindMethodFromDiskPdb.
        GivenDiskProjectWithPdb(out string diskSource);
        // Since FindMethodFromDiskPdb walks from the sourceFile directory, we need
        // the actual test to use diskSource. Mock the PDB mapper to succeed.
        _ = _pdbMapper.FindMethodAtLine(Arg.Any<string>(), Arg.Any<string>(), line)
            .Returns(("DiskAssembly", "DiskMethod", 0x06000099, 0));
        _diskSourceFile = diskSource;
    }

    private void GivenNoLoadedModulesForNullCorWrapper()
        => _ = _corDebug.GetModules(Arg.Any<CorDebugWrapperModel>())
            .Returns([]);

    private void GivenDiskProjectWithPdbInObj(out string sourceFile)
    {
        string projDir = Path.Combine(_tempDir, "ObjProject");
        _ = Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "ObjProject.csproj"), "<Project></Project>");

        string objDir = Path.Combine(projDir, "obj", "Debug");
        _ = Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "ObjProject.pdb"), "fake-pdb");
        File.WriteAllText(Path.Combine(objDir, "ObjProject.dll"), "fake-dll");

        sourceFile = Path.Combine(projDir, "Program.cs");
        File.WriteAllText(sourceFile, "// fake source");
    }

    private void GivenDiskProjectWithPdbButNoDll(out string sourceFile)
    {
        string projDir = Path.Combine(_tempDir, $"NoDllProject_{_nextDiskFileId++}");
        _ = Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "NoDllProject.csproj"), "<Project></Project>");

        string binDir = Path.Combine(projDir, "bin", "Debug");
        _ = Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "NoDllProject.pdb"), "fake-pdb");
        // No DLL alongside — FindMethodFromDiskPdb will skip this PDB.

        sourceFile = Path.Combine(projDir, "Program.cs");
        File.WriteAllText(sourceFile, "// fake source");
    }

    private void GivenCliProjectWithSiblingDll()
    {
        // Create parent directory with two subdirs: CliWrapper/ and WpfApp/
        string parent = Path.Combine(_tempDir, "SiblingTestProject");
        _ = Directory.CreateDirectory(parent);

        string cliDir = Path.Combine(parent, "CliSibling");
        _ = Directory.CreateDirectory(cliDir);
        string vcxproj = Path.Combine(cliDir, "CliSibling.vcxproj");
        File.WriteAllText(vcxproj, "<Project><CLRSupport>true</CLRSupport></Project>");

        // The DLL is in a sibling's bin directory.
        string siblingDir = Path.Combine(parent, "SiblingApp");
        string siblingBin = Path.Combine(siblingDir, "bin", "Debug");
        _ = Directory.CreateDirectory(siblingBin);
        string dllPath = Path.Combine(siblingBin, "CliSibling.dll");
        File.WriteAllText(dllPath, "fake-dll");
        _cliDllPath = dllPath;

        _cliSourcePath = Path.Combine(cliDir, "Wrapper.cpp");
        File.WriteAllText(_cliSourcePath, "// fake source");
        _ = _sourceFiles.IsCliFile(_cliSourcePath).Returns(true);
    }

    private void GivenCliProjectForWatchAssembly(out string sourceFile, string assemblyName)
    {
        string dir = Path.Combine(_tempDir, assemblyName);
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{assemblyName}.vcxproj"),
            "<Project><CLRSupport>true</CLRSupport></Project>");

        sourceFile = Path.Combine(dir, "Source.cpp");
        File.WriteAllText(sourceFile, "// fake source");
        _ = _sourceFiles.IsCliFile(sourceFile).Returns(true);
    }

    #endregion

    #region When

    private void WhenSettingManagedBreakpoints(string filePath, int[] lines)
    {
        SourceBreakpoint[] requested = [.. lines.Select(l => new SourceBreakpoint { Line = l })];
        _results = _testee.SetManagedBreakpoints(_model, filePath, requested);
    }

    private bool WhenTryingToBindBreakpoint(string filePath, int line, int bpId)
        => _testee.TryBindBreakpoint(_model, filePath, line, bpId);

    private uint? WhenSettingManagedCodeBreakpoint(ulong address, string filePath, int line)
        => _testee.SetManagedCodeBreakpoint(_model, address, filePath, line);

    private void WhenSettingTransientBreakpoint(ulong address, string filePath, int line)
        => _testee.SetTransientBreakpoint(_model, address, filePath, line);

    private void WhenRemovingTransientBreakpoints()
        => _testee.RemoveTransientManagedBreakpoints(_model);

    private List<(string Assembly, int Token)> WhenResolvingTokens(
        IEnumerable<(string FilePath, int Line)> breakpoints)
        => _testee.ResolveTokensFromBreakpoints(breakpoints);

    private List<string> WhenResolvingWatchAssemblies(
        IEnumerable<(string FilePath, int Line)> breakpoints)
        => _testee.ResolveWatchAssemblies(breakpoints);

    #endregion

    #region Then

    private void ThenResultCountIs(int expected)
        => Assert.Equal(expected, _results!.Length);

    private void ThenAllResultsAreVerified(bool expected)
        => Assert.All(_results!, bp => Assert.Equal(expected, bp.Verified));

    private void ThenResultAtIndexIsVerified(int index, bool expected)
        => Assert.Equal(expected, _results![index].Verified);

    private void ThenResultAtIndexHasLine(int index, int expected)
        => Assert.Equal(expected, _results![index].Line);

    private void ThenResultAtIndexHasMessage(int index, string expected)
        => Assert.Equal(expected, _results![index].Message);

    private void ThenResultAtIndexHasNoMessage(int index)
        => Assert.Null(_results![index].Message);

    private void ThenManagedBreakpointIdsContains(uint id)
        => Assert.Contains(id, _model.ManagedBreakpointIds);

    private void ThenManagedBreakpointIdsIsEmpty()
        => Assert.Empty(_model.ManagedBreakpointIds);

    private void ThenManagedBreakpointAddressesContains(ulong addr)
        => Assert.Contains(addr, _model.ManagedBreakpointAddresses);

    private void ThenManagedBreakpointAddressesIsEmpty()
        => Assert.Empty(_model.ManagedBreakpointAddresses);

    private void ThenUserBreakpointIdsContains(uint id)
        => Assert.Contains(id, _model.UserBreakpointIds);

    private void ThenUserBreakpointIdsDoesNotContain(uint id)
        => Assert.DoesNotContain(id, _model.UserBreakpointIds);

    private void ThenBreakpointSourcesContains(ulong addr, string file, int line)
    {
        Assert.True(_model.ManagedBreakpointSources.ContainsKey(addr));
        Assert.Equal((file, line), _model.ManagedBreakpointSources[addr]);
    }

    private void ThenBreakpointIdIsTracked(string key, uint id)
    {
        Assert.True(_model.BreakpointIds.ContainsKey(key));
        Assert.Equal(id, _model.BreakpointIds[key]);
    }

    private void ThenBreakpointIdKeyIsRemoved(string key)
        => Assert.False(_model.BreakpointIds.ContainsKey(key));

    private void ThenRemoveBreakpointWasCalled(uint bpId)
        => _dbgEng.Received(1).RemoveBreakpoint(_model.Wrapper, bpId);

    private void ThenRemoveBreakpointWasNotCalled()
        => _dbgEng.DidNotReceive().RemoveBreakpoint(Arg.Any<DbgEngWrapperModel>(), Arg.Any<uint>());

    private void ThenDeactivateLegacyBreakpointWasCalled(int bpId)
        => _corDebug.Received(1).DeactivateLegacyBreakpoint(_model.CorWrapper, bpId);

    private void ThenAddHardwareBreakpointWasCalledWith(ulong address)
        => _dbgEng.Received(1).AddHardwareBreakpoint(_model.Wrapper, address, 1);

    private void ThenPendingILBreakpointCountIs(int expected)
        => Assert.Equal(expected, _model.PendingILBreakpoints.Count);

    private void ThenPendingILBreakpointsDoNotContainFile(string filePath)
        => Assert.DoesNotContain(_model.PendingILBreakpoints,
            p => p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    private void ThenPendingILBreakpointsDoNotContainLine(string filePath, int line)
        => Assert.DoesNotContain(_model.PendingILBreakpoints,
            p => p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) && p.Line == line);

    private void ThenDeferredBreakpointsDoNotContainFile(string filePath)
        => Assert.DoesNotContain(_model.DeferredManagedBreakpoints,
            d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    private void ThenDeferredBreakpointCountIs(int expected)
        => Assert.Equal(expected, _model.DeferredManagedBreakpoints.Count);

    private void ThenDeferredBreakpointHasToken(int index, int token)
        => Assert.Equal(token, _model.DeferredManagedBreakpoints[index].MethodToken);

    private void ThenDeferredBreakpointHasILOffset(int index, int ilOffset)
        => Assert.Equal(ilOffset, _model.DeferredManagedBreakpoints[index].ILOffset);

    private void ThenDeferredBreakpointIsCliMethod(int index, bool expected)
        => Assert.Equal(expected, _model.DeferredManagedBreakpoints[index].IsCliMethod);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore;
    private readonly ISourceFileService _sourceFiles = Substitute.For<ISourceFileService>();
    private readonly IDbgEngWrapper _dbgEng = Substitute.For<IDbgEngWrapper>();
    private readonly ICorDebugWrapper _corDebug = Substitute.For<ICorDebugWrapper>();
    private readonly IPdbSourceMapper _pdbMapper = Substitute.For<IPdbSourceMapper>();
    private readonly NativeDebuggerModel _model;
    private readonly ManagedBreakpointService _testee;
    private readonly string _tempDir;

    private Breakpoint[]? _results;
    private string? _cliDllPath;
    private string? _cliSourcePath;
    private string? _diskSourceFile;
    private int _nextDiskFileId;

    public ManagedBreakpointServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ManagedBpTests_{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(_tempDir);

        _logStore = new LogStore(Path.Combine(_tempDir, "test.log"));
        _testee = new ManagedBreakpointService(
            _log, _logStore, _sourceFiles, _dbgEng, _corDebug, _pdbMapper);
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

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    #endregion
}
