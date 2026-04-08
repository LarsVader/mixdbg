using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Lifecycle;

namespace MixDbg.Tests;

/// <summary>
/// Tests for small DAP message types with default property values.
/// </summary>
public sealed class DapMessageTests
{
    [Fact]
    public void OutputEventBody_DefaultValues_AreConsoleAndEmpty()
    {
        OutputEventBody body = new();

        Assert.Equal("console", body.Category);
        Assert.Equal("", body.Output);
    }

    [Fact]
    public void OutputEventBody_WhenSet_ReturnsNewValues()
    {
        OutputEventBody body = new()
        {
            Category = "stderr",
            Output = "error message",
        };

        Assert.Equal("stderr", body.Category);
        Assert.Equal("error message", body.Output);
    }

    [Fact]
    public void AttachRequestArguments_Properties_AreNullByDefault()
    {
        AttachRequestArguments args = new();

        Assert.Null(args.Pid);
        Assert.Null(args.Program);
        Assert.Null(args.SymbolPath);
    }

    [Fact]
    public void AttachRequestArguments_WhenSet_ReturnsValues()
    {
        AttachRequestArguments args = new()
        {
            Pid = 1234,
            Program = "test.exe",
            SymbolPath = ["/symbols", "/other"],
        };

        Assert.Equal(1234, args.Pid);
        Assert.Equal("test.exe", args.Program);
        Assert.Equal(2, args.SymbolPath!.Length);
    }
}
