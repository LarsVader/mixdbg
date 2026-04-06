using MixDbg.Services;

namespace MixDbg.Tests;

public sealed class SourceFileServiceTests : IDisposable
{
    [Theory]
    [InlineData("main.cpp")]
    [InlineData("main.c")]
    [InlineData("main.cc")]
    [InlineData("main.cxx")]
    [InlineData("header.h")]
    [InlineData("header.hpp")]
    public void IsNativeFile_WhenNativeExtension_ReturnsTrue(string fileName)
    {
        GivenAFileWithName(fileName);

        WhenCheckingIsNativeFile();

        ThenResultIsTrue();
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("readme.txt")]
    [InlineData("data.json")]
    [InlineData("script.py")]
    public void IsNativeFile_WhenNonNativeExtension_ReturnsFalse(string fileName)
    {
        GivenAFileWithName(fileName);

        WhenCheckingIsNativeFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsNativeFile_WhenCppCliProject_ReturnsFalse()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsNativeFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsNativeFile_WhenNativeVcxproj_ReturnsTrue()
    {
        GivenANativeVcxproj();
        GivenAFileWithName("native.cpp");

        WhenCheckingIsNativeFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsNativeFile_WhenNoVcxproj_ReturnsTrue()
    {
        GivenAFileWithName("standalone.cpp");

        WhenCheckingIsNativeFile();

        ThenResultIsTrue();
    }

    // ── IsManagedFile ───────────────────────────────────────

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("Service.cs")]
    public void IsManagedFile_WhenCsExtension_ReturnsTrue(string fileName)
    {
        GivenAFileWithName(fileName);

        WhenCheckingIsManagedFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsManagedFile_WhenCppCliProject_ReturnsTrue()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsManagedFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsManagedFile_WhenNativeCpp_ReturnsFalse()
    {
        GivenANativeVcxproj();
        GivenAFileWithName("native.cpp");

        WhenCheckingIsManagedFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsManagedFile_WhenNoCppNoCs_ReturnsFalse()
    {
        GivenAFileWithName("readme.txt");

        WhenCheckingIsManagedFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsManagedFile_WhenStandaloneCpp_ReturnsFalse()
    {
        GivenAFileWithName("standalone.cpp");

        WhenCheckingIsManagedFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsManagedFile_WhenHeaderInCliProject_ReturnsTrue()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("ManagedCalculator.h");

        WhenCheckingIsManagedFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsManagedFile_WhenHppInCliProject_ReturnsTrue()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("wrapper.hpp");

        WhenCheckingIsManagedFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsManagedFile_WhenHeaderInNativeProject_ReturnsFalse()
    {
        GivenANativeVcxproj();
        GivenAFileWithName("native.h");

        WhenCheckingIsManagedFile();

        ThenResultIsFalse();
    }

    // ── IsCliFile ───────────────────────────────────────

    [Fact]
    public void IsCliFile_WhenCppInCliProject_ReturnsTrue()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsCliFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsCliFile_WhenHeaderInCliProject_ReturnsTrue()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("ManagedCalculator.h");

        WhenCheckingIsCliFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsCliFile_WhenCppInNativeProject_ReturnsFalse()
    {
        GivenANativeVcxproj();
        GivenAFileWithName("native.cpp");

        WhenCheckingIsCliFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsCliFile_WhenStandaloneCpp_ReturnsFalse()
    {
        GivenAFileWithName("standalone.cpp");

        WhenCheckingIsCliFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsCliFile_WhenCsFile_ReturnsFalse()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("Program.cs");

        WhenCheckingIsCliFile();

        ThenResultIsFalse();
    }

    #region Given

    private void GivenAFileWithName(string fileName)
    {
        _filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(_filePath, "// source");
    }

    private void GivenACppCliVcxproj() => File.WriteAllText(
            Path.Combine(_tempDir, "project.vcxproj"),
            "<Project><PropertyGroup><CLRSupport>true</CLRSupport></PropertyGroup></Project>");

    private void GivenANativeVcxproj() => File.WriteAllText(
            Path.Combine(_tempDir, "project.vcxproj"),
            "<Project><PropertyGroup><RuntimeLibrary>MultiThreadedDLL</RuntimeLibrary></PropertyGroup></Project>");

    #endregion

    #region When

    private void WhenCheckingIsNativeFile() => _result = _testee.IsNativeFile(_filePath);

    private void WhenCheckingIsManagedFile() => _result = _testee.IsManagedFile(_filePath);

    private void WhenCheckingIsCliFile() => _result = _testee.IsCliFile(_filePath);

    #endregion

    #region Then

    private void ThenResultIsTrue() => Assert.True(_result);

    private void ThenResultIsFalse() => Assert.False(_result);

    #endregion

    #region Misc

    private readonly SourceFileService _testee = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mixdbg_test_{Guid.NewGuid()}");
    private string _filePath = "";
    private bool _result;

    public SourceFileServiceTests() => _ = Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    #endregion
}