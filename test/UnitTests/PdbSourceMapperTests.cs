using MixDbg.Engine.Sos;

namespace MixDbg.Tests;

/// <summary>
/// Tests for <see cref="PdbSourceMapper"/> focusing on path normalization
/// and source line resolution.
/// </summary>
public sealed class PdbSourceMapperTests
{
    private static readonly string _repoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string _wpfAppDll = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "bin", "x64", "Debug", "net10.0-windows", "WpfApp.dll");

    private static readonly string _sourceFile = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "MainWindow.xaml.cs");

    [Fact]
    public void FindMethodAtLine_WhenPathHasForwardSlashes_StillResolvesMethod()
    {
        if (!File.Exists(_wpfAppDll)) return; // WpfApp not built — skip

        // nvim-dap on Windows sends paths with mixed slashes: D:\foo\bar/baz/file.cs
        string mixedSlashPath = _sourceFile.Replace("\\test\\TestApp\\", "/test/TestApp/");

        using PdbSourceMapperService mapper = new();
        // Line 65 = first code line inside OnAddClick (the if statement).
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = mapper.FindMethodAtLine(_wpfAppDll, mixedSlashPath, 65);

        _ = Assert.NotNull(result);
        Assert.Equal("WpfApp", result.Value.AssemblyName);
        Assert.Contains("OnAddClick", result.Value.MethodName);
    }

    [Fact]
    public void FindMethodAtLine_WhenPathHasBackslashes_ResolvesMethod()
    {
        if (!File.Exists(_wpfAppDll)) return; // WpfApp not built — skip

        using PdbSourceMapperService mapper = new();
        // Line 65 = first code line inside OnAddClick (the if statement).
        // Line 63 is the method signature which has no sequence point.
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);

        _ = Assert.NotNull(result);
        Assert.Equal("WpfApp", result.Value.AssemblyName);
        Assert.Contains("OnAddClick", result.Value.MethodName);
    }

    [Fact]
    public void GetLocalVariableNames_WhenMethodHasLocals_ReturnsNameIndexPairs()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        // Find OnAddClick to get its token and IL offset.
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);
        if (method == null) return;

        (string Name, int Index)[] locals = mapper.GetLocalVariableNames(
            _wpfAppDll, method.Value.MethodToken, method.Value.ILOffset);

        Assert.NotEmpty(locals);
        // All names should be non-empty, indices non-negative.
        Assert.All(locals, l =>
        {
            Assert.False(string.IsNullOrEmpty(l.Name));
            Assert.True(l.Index >= 0);
        });
    }

    [Fact]
    public void GetLocalVariableNames_WhenAssemblyDoesNotExist_ReturnsEmpty()
    {
        using PdbSourceMapperService mapper = new();

        (string Name, int Index)[] locals = mapper.GetLocalVariableNames(
            @"C:\nonexistent.dll", 0x06000001, 0);

        Assert.Empty(locals);
    }

    [Fact]
    public void GetParameterNames_WhenMethodHasParameters_ReturnsNames()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        // OnAddClick(object sender, RoutedEventArgs e) has 2 params.
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);
        if (method == null) return;

        string[] paramNames = mapper.GetParameterNames(_wpfAppDll, method.Value.MethodToken);

        Assert.NotEmpty(paramNames);
        Assert.Contains("sender", paramNames);
        Assert.Contains("e", paramNames);
    }

    [Fact]
    public void GetParameterNames_WhenAssemblyDoesNotExist_ReturnsEmpty()
    {
        using PdbSourceMapperService mapper = new();

        string[] paramNames = mapper.GetParameterNames(@"C:\nonexistent.dll", 0x06000001);

        Assert.Empty(paramNames);
    }

    // ── GetParameterTypes ───────────────────────────────

    [Fact]
    public void GetParameterTypes_WhenMethodHasParameters_ReturnsTypeNames()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);
        if (method == null) return;

        string[] paramTypes = mapper.GetParameterTypes(_wpfAppDll, method.Value.MethodToken);

        // OnAddClick(object sender, RoutedEventArgs e)
        Assert.NotEmpty(paramTypes);
        Assert.Equal("object", paramTypes[0]);
        Assert.Equal("RoutedEventArgs", paramTypes[1]);
    }

    [Fact]
    public void GetParameterTypes_WhenAssemblyDoesNotExist_ReturnsEmpty()
    {
        using PdbSourceMapperService mapper = new();

        string[] paramTypes = mapper.GetParameterTypes(@"C:\nonexistent.dll", 0x06000001);

        Assert.Empty(paramTypes);
    }

    // ── GetLocalVariableTypes ───────────────────────────

    [Fact]
    public void GetLocalVariableTypes_WhenMethodHasLocals_ReturnsTypeNames()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);
        if (method == null) return;

        string[] localTypes = mapper.GetLocalVariableTypes(_wpfAppDll, method.Value.MethodToken);

        // OnAddClick has locals like int a, int b, int result, etc.
        Assert.NotEmpty(localTypes);
        Assert.Contains("int", localTypes);
    }

    [Fact]
    public void GetLocalVariableTypes_WhenAssemblyDoesNotExist_ReturnsEmpty()
    {
        using PdbSourceMapperService mapper = new();

        string[] localTypes = mapper.GetLocalVariableTypes(@"C:\nonexistent.dll", 0x06000001);

        Assert.Empty(localTypes);
    }

    // ── GetMethodSequencePoints ─────────────────────────

    [Fact]
    public void GetMethodSequencePoints_WhenMethodHasSequencePoints_ReturnsNonEmpty()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);
        if (method == null) return;

        (int ILOffset, string File, int Line)[] seqPoints =
            mapper.GetMethodSequencePoints(_wpfAppDll, method.Value.MethodToken);

        Assert.NotEmpty(seqPoints);
        // All IL offsets should be non-negative.
        Assert.All(seqPoints, sp => Assert.True(sp.ILOffset >= 0));
        // All files should be non-empty.
        Assert.All(seqPoints, sp => Assert.False(string.IsNullOrEmpty(sp.File)));
        // All lines should be positive.
        Assert.All(seqPoints, sp => Assert.True(sp.Line > 0));
    }

    [Fact]
    public void GetMethodSequencePoints_WhenCalled_ReturnsSortedByILOffset()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 65);
        if (method == null) return;

        (int ILOffset, string File, int Line)[] seqPoints =
            mapper.GetMethodSequencePoints(_wpfAppDll, method.Value.MethodToken);

        for (int i = 1; i < seqPoints.Length; i++)
        {
            Assert.True(seqPoints[i].ILOffset >= seqPoints[i - 1].ILOffset,
                $"Sequence points not sorted: IL 0x{seqPoints[i - 1].ILOffset:X} > 0x{seqPoints[i].ILOffset:X}");
        }
    }

    [Fact]
    public void GetMethodSequencePoints_WhenAssemblyDoesNotExist_ReturnsEmpty()
    {
        using PdbSourceMapperService mapper = new();

        (int ILOffset, string File, int Line)[] seqPoints =
            mapper.GetMethodSequencePoints(@"C:\nonexistent.dll", 0x06000001);

        Assert.Empty(seqPoints);
    }

    // ── GetCallTargetAtOffset ───────────────────────────

    [Fact]
    public void GetCallTargetAtOffset_WhenAtCallSite_ReturnsTargetMethodInfo()
    {
        if (!File.Exists(_wpfAppDll)) return;

        using PdbSourceMapperService mapper = new();
        // Find OnAddClick which calls ManagedCalculator.Add at line 67.
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? method =
            mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 67);
        if (method == null) return;

        // The IL at line 67's offset should contain a call to ManagedCalculator.Add.
        (int TargetToken, string? TargetAssembly, string? TargetMethodName, int CallILOffset)? callTarget =
            mapper.GetCallTargetAtOffset(_wpfAppDll, method.Value.MethodToken, method.Value.ILOffset);

        _ = Assert.NotNull(callTarget);
        Assert.NotNull(callTarget.Value.TargetMethodName);
        Assert.Contains("Add", callTarget.Value.TargetMethodName!);
    }

    [Fact]
    public void GetCallTargetAtOffset_WhenAssemblyDoesNotExist_ReturnsNull()
    {
        using PdbSourceMapperService mapper = new();

        (int TargetToken, string? TargetAssembly, string? TargetMethodName, int CallILOffset)? result =
            mapper.GetCallTargetAtOffset(@"C:\nonexistent.dll", 0x06000001, 0);

        Assert.Null(result);
    }

    [Fact]
    public void FindMethodAtLine_WhenLineIsInsideLambdaInGetter_ResolvesLambdaNotGetter()
    {
        // Regression: CalculatorViewModel.cs has
        //     public ICommand AddCommand { get => field ??= new RelayCommand(_ =>
        //     {
        //         int sum = CliWrapper.ManagedCalculator.Add(InputA, InputB);   // line 20
        //         ...
        //     }); } = null;
        // The compiler emits two sequence points covering line 20:
        //   • get_AddCommand           SP=L18-L23 ILOffset=0x0
        //   • <get_AddCommand>b__18_0  SP=L20-L20 ILOffset=0x1
        // FindMethodAtLine must return the lambda body, not the getter — otherwise
        // the BP fires on every property read (XAML binding) instead of on Execute.
        if (!File.Exists(_wpfAppDll)) return;

        string calcVm = Path.Combine(_repoRoot, "test", "TestApp", "WpfApp", "ViewModels", "CalculatorViewModel.cs");
        if (!File.Exists(calcVm)) return;

        using PdbSourceMapperService mapper = new();
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result =
            mapper.FindMethodAtLine(_wpfAppDll, calcVm, 20);

        _ = Assert.NotNull(result);
        Assert.Equal("WpfApp", result.Value.AssemblyName);
        Assert.Contains("<get_AddCommand>b__", result.Value.MethodName);
    }
}
