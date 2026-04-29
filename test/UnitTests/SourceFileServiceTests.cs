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

    // ── CLR detection variants ────────────────────────────────

    [Fact]
    public void IsCliFile_WhenVcxprojHasClrImageType_ReturnsTrue()
    {
        GivenVcxprojWithContent("<Project><PropertyGroup><CLRImageType>ForceIJWImage</CLRImageType></PropertyGroup></Project>");
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsCliFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsCliFile_WhenVcxprojHasCompileAsManaged_ReturnsTrue()
    {
        GivenVcxprojWithContent("<Project><ItemDefinitionGroup><ClCompile><CompileAsManaged>true</CompileAsManaged></ClCompile></ItemDefinitionGroup></Project>");
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsCliFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsCliFile_WhenVcxprojHasSlashClrFlag_ReturnsTrue()
    {
        GivenVcxprojWithContent("<Project><ItemDefinitionGroup><ClCompile><AdditionalOptions>/clr %(AdditionalOptions)</AdditionalOptions></ClCompile></ItemDefinitionGroup></Project>");
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsCliFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsNativeFile_WhenVcxprojHasClrImageType_ReturnsFalse()
    {
        GivenVcxprojWithContent("<Project><PropertyGroup><CLRImageType>ForceIJWImage</CLRImageType></PropertyGroup></Project>");
        GivenAFileWithName("wrapper.cpp");

        WhenCheckingIsNativeFile();

        ThenResultIsFalse();
    }

    // ── HasClrIndicator ───────────────────────────────────────

    [Theory]
    [InlineData("<CLRSupport>true</CLRSupport>")]
    [InlineData("<CLRImageType>ForceIJWImage</CLRImageType>")]
    [InlineData("<CompileAsManaged>true</CompileAsManaged>")]
    [InlineData("<AdditionalOptions>/clr %(AdditionalOptions)</AdditionalOptions>")]
    public void HasClrIndicator_WhenClrContent_ReturnsTrue(string content)
        => Assert.True(_testee.HasClrIndicator($"<Project>{content}</Project>"));

    [Theory]
    [InlineData("<RuntimeLibrary>MultiThreadedDLL</RuntimeLibrary>")]
    [InlineData("")]
    [InlineData("<Project></Project>")]
    public void HasClrIndicator_WhenNoClrContent_ReturnsFalse(string content)
        => Assert.False(_testee.HasClrIndicator(content));

    // ── ResolveCliAssemblyName ─────────────────────────────────

    [Fact]
    public void ResolveCliAssemblyName_WhenCppCliVcxproj_ReturnsProjectName()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("wrapper.cpp");

        string? result = _testee.ResolveCliAssemblyName(_filePath);

        Assert.Equal("project", result);
    }

    [Fact]
    public void ResolveCliAssemblyName_WhenNativeVcxproj_ReturnsNull()
    {
        GivenANativeVcxproj();
        GivenAFileWithName("native.cpp");

        string? result = _testee.ResolveCliAssemblyName(_filePath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCliAssemblyName_WhenNoVcxproj_ReturnsNull()
    {
        GivenAFileWithName("standalone.cpp");

        string? result = _testee.ResolveCliAssemblyName(_filePath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCliAssemblyName_WhenSlashClrInAdditionalOptions_ReturnsProjectName()
    {
        GivenVcxprojWithContent("<Project><ItemDefinitionGroup><ClCompile><AdditionalOptions>/clr %(AdditionalOptions)</AdditionalOptions></ClCompile></ItemDefinitionGroup></Project>");
        GivenAFileWithName("wrapper.cpp");

        string? result = _testee.ResolveCliAssemblyName(_filePath);

        Assert.Equal("project", result);
    }

    [Fact]
    public void ResolveCliAssemblyName_WhenVcxprojInParentDir_ReturnsProjectName()
    {
        GivenACppCliVcxproj();
        string subDir = Path.Combine(_tempDir, "subfolder");
        _ = Directory.CreateDirectory(subDir);
        _filePath = Path.Combine(subDir, "deep.cpp");
        File.WriteAllText(_filePath, "// source");

        string? result = _testee.ResolveCliAssemblyName(_filePath);

        Assert.Equal("project", result);
    }

    [Fact]
    public void ResolveCliAssemblyName_StopsAtSlnBoundary()
    {
        GivenASlnFile();
        GivenAFileWithName("file.cpp");
        // vcxproj is above the sln — should not be found.
        string parentDir = Path.GetDirectoryName(_tempDir)!;
        File.WriteAllText(Path.Combine(parentDir, "Parent.vcxproj"),
            "<Project><PropertyGroup><CLRSupport>true</CLRSupport></PropertyGroup></Project>");

        string? result = _testee.ResolveCliAssemblyName(_filePath);

        Assert.Null(result);
        // Clean up parent vcxproj.
        File.Delete(Path.Combine(parentDir, "Parent.vcxproj"));
    }

    [Fact]
    public void ResolveCliAssemblyName_CachesResult()
    {
        GivenACppCliVcxproj();
        GivenAFileWithName("a.cpp");

        string? first = _testee.ResolveCliAssemblyName(_filePath);
        // Delete the vcxproj — cached result should still return.
        File.Delete(Path.Combine(_tempDir, "project.vcxproj"));
        string? second = _testee.ResolveCliAssemblyName(Path.Combine(_tempDir, "b.cpp"));

        Assert.Equal("project", first);
        Assert.Equal("project", second);
    }

    // ── Catch blocks (IO errors) ─────────────────────────────

    [Fact]
    public void IsNativeFile_WhenDirectoryAccessThrows_ReturnsTrue()
    {
        // Use a path with an invalid directory that will cause Directory.GetFiles to throw.
        string invalidDirPath = Path.Combine(_tempDir, "nonexistent_subdir", "file.cpp");

        _filePath = invalidDirPath;
        WhenCheckingIsNativeFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsManagedFile_WhenDirectoryAccessThrows_ReturnsFalse()
    {
        string invalidDirPath = Path.Combine(_tempDir, "nonexistent_subdir", "file.cpp");

        _filePath = invalidDirPath;
        WhenCheckingIsManagedFile();

        ThenResultIsFalse();
    }

    [Fact]
    public void IsCliFile_WhenDirectoryAccessThrows_ReturnsFalse()
    {
        string invalidDirPath = Path.Combine(_tempDir, "nonexistent_subdir", "file.cpp");

        _filePath = invalidDirPath;
        WhenCheckingIsCliFile();

        ThenResultIsFalse();
    }

    // ── Boundary check order (sln + vcxproj in same dir) ────

    [Fact]
    public void IsCliFile_WhenVcxprojAndSlnInSameDirectory_ReturnsTrue()
    {
        GivenAFileWithName("Wrapper.cpp");
        GivenACppCliVcxproj();
        GivenASlnFile();

        WhenCheckingIsCliFile();

        ThenResultIsTrue();
    }

    [Fact]
    public void IsNativeFile_WhenVcxprojAndSlnInSameDirectory_ReturnsFalse()
    {
        GivenAFileWithName("Wrapper.cpp");
        GivenACppCliVcxproj();
        GivenASlnFile();

        WhenCheckingIsNativeFile();

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

    private void GivenVcxprojWithContent(string content) => File.WriteAllText(
            Path.Combine(_tempDir, "project.vcxproj"), content);

    private void GivenANativeVcxproj() => File.WriteAllText(
            Path.Combine(_tempDir, "project.vcxproj"),
            "<Project><PropertyGroup><RuntimeLibrary>MultiThreadedDLL</RuntimeLibrary></PropertyGroup></Project>");

    private void GivenASlnFile() => File.WriteAllText(
            Path.Combine(_tempDir, "Project.sln"), "");

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

    private readonly SourceFileService _testee = new(
        new MixDbg.Models.VcxprojCache(),
        NSubstitute.Substitute.For<MixDbg.Services.Interfaces.ILoggingService>(),
        new MixDbg.Models.LogStore(Path.Combine(Path.GetTempPath(), "test.log")));
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