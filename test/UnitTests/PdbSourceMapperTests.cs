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
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = mapper.FindMethodAtLine(_wpfAppDll, mixedSlashPath, 63);

        _ = Assert.NotNull(result);
        Assert.Equal("WpfApp", result.Value.AssemblyName);
        Assert.Contains("OnAddClick", result.Value.MethodName);
    }

    [Fact]
    public void FindMethodAtLine_WhenPathHasBackslashes_ResolvesMethod()
    {
        if (!File.Exists(_wpfAppDll)) return; // WpfApp not built — skip

        using PdbSourceMapperService mapper = new();
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = mapper.FindMethodAtLine(_wpfAppDll, _sourceFile, 63);

        _ = Assert.NotNull(result);
        Assert.Equal("WpfApp", result.Value.AssemblyName);
        Assert.Contains("OnAddClick", result.Value.MethodName);
    }
}