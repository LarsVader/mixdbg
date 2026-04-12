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
}