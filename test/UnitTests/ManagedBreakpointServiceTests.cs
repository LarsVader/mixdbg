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
    public void TryBindBreakpoint_WhenLoadedModuleResolves_AndMethodIsJitd_CreatesPlanSite()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0x5000);
        GivenAddHardwareBreakpointSucceedsForAnyAddress(bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 100);

        Assert.True(result);
        Assert.True(_model.ManagedBpPlans.ContainsKey((0x06000001, "MyApp")));
        // Already JIT'd with no active hooks → HW BP installed immediately.
        ThenManagedBreakpointIdsContains(42);
        ThenManagedBreakpointAddressesContains(0x5000);
    }

    [Fact]
    public void TryBindBreakpoint_WhenMethodHasActiveActivation_InstallsHwBpPiggybacked()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0x5000);
        _model.ActiveMethodBreakpoints[(0x06000001, "MyApp")] =
            new ActiveMethodBreakpoint { ActivationCount = 1 };
        GivenAddHardwareBreakpointSucceedsForAnyAddress(bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 100);

        Assert.True(result);
        ThenManagedBreakpointIdsContains(42);
        ThenUserBreakpointIdsContains(42);
        ThenManagedBreakpointAddressesContains(0x5000);
        ThenBreakpointSourcesContains(0x5000, @"C:\src\Program.cs", 10);
    }

    /// <summary>
    /// Regression: GetOffsetByLine returns bogus IL stub addresses for managed
    /// methods. When the method has an active activation on the stack, the piggyback
    /// install path must use the JitMethodMap+IL-to-native mapping, not the bogus
    /// GetOffsetByLine address.
    /// </summary>
    [Fact]
    public void TryBindBreakpoint_WhenActiveAndInJitMethodMap_UsesJitAddressForPiggybackBp()
    {
        GivenLoadedModule(@"C:\out\WpfApp.dll", @"C:\out\WpfApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\WpfApp.dll", @"C:\src\MainWindow.xaml.cs", 65,
            ("WpfApp", "OnAddClick", 0x06000010, 0x20));
        GivenGetOffsetByLineSucceeds(@"C:\src\MainWindow.xaml.cs", 65, 0x21AF62D026E);

        ulong correctJitStart = 0x7FF7B6C82EC0;
        JitMethodInfo jitInfo = new(0x06000010, correctJitStart, 0x200, "WpfApp");
        lock (_model.JitMethodMap)
        {
            _model.JitMethodMap[correctJitStart] = jitInfo;
            _model.JitMethodMapByToken[(0x06000010, "WpfApp")] = jitInfo;
        }
        _model.JitMethodMappings[(0x06000010, "WpfApp")] = new JitMethodMapping(
            correctJitStart, [(0x00, 0x00), (0x10, 0x30), (0x20, 0x70), (0x30, 0xA0)]);

        _model.ActiveMethodBreakpoints[(0x06000010, "WpfApp")] =
            new ActiveMethodBreakpoint { ActivationCount = 1 };
        GivenAddHardwareBreakpointSucceedsForAnyAddress(bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\MainWindow.xaml.cs", 65, bpId: 100);

        Assert.True(result);
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
    public void TryBindBreakpoint_WhenCliFileResolves_ViaVcxproj_StoresDeferred()
    {
        GivenNoLoadedModules();
        GivenCliProjectExists(); // Sets up temp vcxproj, _cliSourcePath, _cliDllPath
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenGetModuleImagePathReturns(0x1000, null); // dbgeng path unavailable, falls back to vcxproj
        GivenFindTokenByRvaReturns(_cliDllPath!, (int)(0x7000 - 0x1000), 0x06000099);

        bool result = WhenTryingToBindBreakpoint(_cliSourcePath!, 25, bpId: 400);

        Assert.True(result);
        ThenDeferredBreakpointCountIs(1);
    }

    [Fact]
    public void TryBindBreakpoint_WhenCliFileResolves_ViaDbgEngModuleInfo_StoresDeferred()
    {
        GivenCliSourceFileOnDisk();
        GivenNoLoadedModules();
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenGetModuleImagePathReturns(0x1000, @"C:\out\CliWrapper.dll");
        GivenFindTokenByRvaReturns(@"C:\out\CliWrapper.dll", (int)(0x7000 - 0x1000), 0x06000088);

        WhenBindingBreakpoint(_cliSourcePath!, 25, bpId: 401);

        ThenBindSucceeded();
        ThenDeferredBreakpointCountIs(1);
        ThenDeferredBreakpointHasAssembly(0, "CliWrapper");
    }

    [Fact]
    public void TryBindBreakpoint_WhenCliFileResolves_QueuesWatchCommand()
    {
        GivenCliSourceFileOnDisk();
        GivenNoLoadedModules();
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenGetModuleImagePathReturns(0x1000, @"C:\out\CliWrapper.dll");
        GivenFindTokenByRvaReturns(@"C:\out\CliWrapper.dll", (int)(0x7000 - 0x1000), 0x06000088);

        WhenBindingBreakpoint(_cliSourcePath!, 25, bpId: 402);

        ThenWatchCommandWasQueued("CliWrapper", 0x06000088);
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
        GivenGetOffsetByLineFails(src, 25);

        bool result = WhenTryingToBindBreakpoint(src, 25, bpId: 600);

        Assert.False(result);
    }

    [Fact]
    public void TryBindBreakpoint_WhenCliFile_ButModuleByOffsetFails_ReturnsFalse()
    {
        string src = GivenSourceFileOnDisk("Wrapper.cpp");
        GivenNoLoadedModules();
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

    // ── BindResolvedMethod plan creation ──────────────────────

    [Fact]
    public void BindResolvedMethod_CreatesMethodBreakpointPlan()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0x20));

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 101);

        Assert.True(result);
        Assert.True(_model.ManagedBpPlans.ContainsKey((0x06000001, "MyApp")));
        ManagedMethodBreakpointPlan plan = _model.ManagedBpPlans[(0x06000001, "MyApp")];
        _ = Assert.Single(plan.Sites);
        Assert.Equal(101, plan.Sites[0].BpId);
        Assert.Equal(0x20, plan.Sites[0].ILOffset);
        Assert.Equal(10, plan.Sites[0].Line);
        Assert.Equal(@"C:\src\Program.cs", plan.Sites[0].FilePath);
    }

    [Fact]
    public void BindResolvedMethod_WhenMethodHasActiveActivation_InstallsHwBpImmediately()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0x20));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0x1000);
        _model.JitMethodMappings[(0x06000001, "MyApp")] =
            new JitMethodMapping(0x1000, [(0x00, 0x00), (0x20, 0x50)]);
        // Simulate the method having an active activation on the stack.
        _model.ActiveMethodBreakpoints[(0x06000001, "MyApp")] =
            new ActiveMethodBreakpoint { ActivationCount = 1 };
        GivenAddHardwareBreakpointSucceedsForAnyAddress(bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 100);

        Assert.True(result);
        // BP installed at IL offset 0x20 → native 0x1050.
        ThenAddHardwareBreakpointWasCalledWith(0x1050);
        Assert.Contains(42u, _model.ActiveMethodBreakpoints[(0x06000001, "MyApp")].InstalledBpIds);
    }

    [Fact]
    public void BindResolvedMethod_WhenNoActiveActivation_InstallsHwBpImmediately()
    {
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0x1000);
        GivenAddHardwareBreakpointSucceedsForAnyAddress(bpId: 42);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 100);

        Assert.True(result);
        // No active entry but method is JIT'd → HW BP installed immediately
        // (FunctionIDMapper won't re-enable hooks for already-JIT'd methods).
        ThenManagedBreakpointIdsContains(42);
        ThenManagedBreakpointAddressesContains(0x1000);
    }

    [Fact]
    public void ClearManagedBreakpointsForFile_RemovesPlansAndActiveBps()
    {
        string src = GivenSourceFileOnDisk();
        GivenExistingManagedBreakpointForFile(src, 65, hwBpId: 42, bpId: 1);

        // Add a plan + active entry that should be cleared.
        _model.ManagedBpPlans[(0x06000001, "MyApp")] = new ManagedMethodBreakpointPlan
        {
            MethodToken = 0x06000001,
            AssemblyName = "MyApp",
            Sites =
            {
                new MethodBreakpointSite { BpId = 1, ILOffset = 0, FilePath = src, Line = 65 },
            },
        };
        ActiveMethodBreakpoint active = new() { ActivationCount = 1 };
        active.InstalledBpIds.Add(42);
        _model.ActiveMethodBreakpoints[(0x06000001, "MyApp")] = active;

        GivenRemoveBreakpointSucceeds(42);
        GivenNoLoadedModules();
        GivenSourceFileIsNotCli(src);

        WhenSettingManagedBreakpoints(src, [70]); // triggers ClearManagedBreakpointsForFile

        // Plan entry with all sites for this file is dropped.
        Assert.False(_model.ManagedBpPlans.ContainsKey((0x06000001, "MyApp")));
        // Active entry's HW BP ID is removed from its installed list.
        Assert.DoesNotContain(42u, _model.ActiveMethodBreakpoints[(0x06000001, "MyApp")].InstalledBpIds);
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

    // ── FindCliAssemblyDll (assembly null) ──

    [Fact]
    public void TryBindBreakpoint_WhenCliFile_ButNoVcxprojForDll_ReturnsFalse()
    {
        // Source is in a temp dir with no vcxproj and GetModuleImagePath returns null,
        // so both resolution paths fail.
        string src = GivenSourceFileOnDisk("Wrapper.cpp");
        GivenNoLoadedModules();
        GivenGetOffsetByLineSucceeds(src, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenGetModuleImagePathReturns(0x1000, null);

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
        GivenGetModuleImagePathReturns(0x1000, null); // dbgeng path unavailable, falls back to vcxproj
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

    // ── BindResolvedMethod still returns true when HW BP install is deferred ──

    [Fact]
    public void TryBindBreakpoint_WhenMethodJitdButNoActivation_ReturnsTrueWithoutHwBp()
    {
        // Method is JIT'd but not currently running — plan is created, HW BP
        // installation is deferred to the next FunctionEnter. Returns true regardless.
        GivenLoadedModule(@"C:\out\MyApp.dll", @"C:\out\MyApp.pdb");
        GivenPdbResolvesMethodAtLine(@"C:\out\MyApp.dll", @"C:\src\Program.cs", 10,
            ("MyApp", "Main", 0x06000001, 0));
        GivenMethodInJitMethodMap("MyApp", 0x06000001, 0xABCD);

        bool result = WhenTryingToBindBreakpoint(@"C:\src\Program.cs", 10, bpId: 970);

        Assert.True(result);
        Assert.True(_model.ManagedBpPlans.ContainsKey((0x06000001, "MyApp")));
    }

    // ── FindCliAssemblyDll (FindTokenByRva returns null) ──

    [Fact]
    public void TryBindBreakpoint_WhenCliTokenNotFound_ReturnsFalse()
    {
        GivenNoLoadedModules();
        GivenCliProjectExists();
        GivenGetOffsetByLineSucceeds(_cliSourcePath!, 25, 0x7000);
        GivenGetModuleByOffsetReturns(0x7000, 0x1000);
        GivenGetModuleImagePathReturns(0x1000, null);
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
        JitMethodInfo info = new(token, startAddress, 0x200, assembly);
        lock (_model.JitMethodMap)
        {
            _model.JitMethodMap[startAddress] = info;
            _model.JitMethodMapByToken[(token, assembly)] = info;
        }
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

    private void GivenGetModuleImagePathReturns(ulong moduleBase, string? imagePath)
        => _dbgEng.GetModuleImagePath(_model.Wrapper, moduleBase).Returns(imagePath);

    private void GivenFindTokenByRvaReturns(string dllPath, int rva, int token)
        => _pdbMapper.FindTokenByRva(dllPath, rva).Returns(token);

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

    private void GivenCliSourceFileOnDisk()
    {
        string dir = Path.Combine(_tempDir, $"cliSrc_{_nextDiskFileId++}");
        _ = Directory.CreateDirectory(dir);
        _cliSourcePath = Path.Combine(dir, "Wrapper.cpp");
        File.WriteAllText(_cliSourcePath, "// fake source");
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

    #endregion

    #region When

    private void WhenSettingManagedBreakpoints(string filePath, int[] lines)
    {
        SourceBreakpoint[] requested = [.. lines.Select(l => new SourceBreakpoint { Line = l })];
        _results = _testee.SetManagedBreakpoints(_model, filePath, requested);
    }

    private bool WhenTryingToBindBreakpoint(string filePath, int line, int bpId)
        => _testee.TryBindBreakpoint(_model, filePath, line, bpId);

    private void WhenBindingBreakpoint(string filePath, int line, int bpId)
        => _bindResult = _testee.TryBindBreakpoint(_model, filePath, line, bpId);

    private uint? WhenSettingManagedCodeBreakpoint(ulong address, string filePath, int line)
        => _testee.SetManagedCodeBreakpoint(_model, address, filePath, line);

    private List<(string Assembly, int Token)> WhenResolvingTokens(
        IEnumerable<(string FilePath, int Line)> breakpoints)
        => _testee.ResolveTokensFromBreakpoints(breakpoints);

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

    private void ThenWatchCommandWasQueued(string assembly, int token) =>
        Assert.Contains(_model.PendingWatchCommands, cmd => cmd == $"WATCH:{assembly}:{token:X8}");

    private void ThenDeferredBreakpointHasAssembly(int index, string expected)
        => Assert.Equal(expected, _model.DeferredManagedBreakpoints[index].AssemblyName);

    private void ThenBindSucceeded()
        => Assert.True(_bindResult);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore;
    private readonly ISourceFileService _sourceFiles = CreateSourceFilesMock();
    private readonly IDbgEngWrapper _dbgEng = Substitute.For<IDbgEngWrapper>();
    private readonly ICorDebugWrapper _corDebug = Substitute.For<ICorDebugWrapper>();
    private readonly IPdbSourceMapper _pdbMapper = Substitute.For<IPdbSourceMapper>();
    private readonly NativeDebuggerModel _model;
    private readonly ManagedBreakpointService _testee;
    private readonly string _tempDir;

    private Breakpoint[]? _results;
    private bool? _bindResult;
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

    private static ISourceFileService CreateSourceFilesMock()
    {
        ISourceFileService mock = Substitute.For<ISourceFileService>();
        // Delegate HasClrIndicator and ResolveCliAssemblyName to the real implementation
        // so tests that create vcxproj files with CLR indicators get correct results.
        SourceFileService real = new(
            new MixDbg.Models.VcxprojCache(),
            Substitute.For<ILoggingService>(),
            new MixDbg.Models.LogStore(Path.Combine(Path.GetTempPath(), "test.log")));
        _ = mock.HasClrIndicator(Arg.Any<string>()).Returns(ci => real.HasClrIndicator((string)ci[0]));
        _ = mock.ResolveCliAssemblyName(Arg.Any<string>()).Returns(ci => real.ResolveCliAssemblyName((string)ci[0]));
        return mock;
    }

    #endregion
}
