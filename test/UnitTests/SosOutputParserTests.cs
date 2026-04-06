using MixDbg.Engine.Sos;

namespace MixDbg.Tests;

public sealed class SosOutputParserTests
{
    // ── ParseBpmdOutput (breakpoint set) ────────────────────

    [Fact]
    public void ParseBpmdOutput_WhenBreakpointSet_ReturnsSuccessWithId()
    {
        GivenBpmdOutput("Found 1 methods in module...\nMethodDesc = 00007FFA12345678\nBreakpoint 3 set.");

        WhenParsingBpmdOutput();

        ThenBpmdSucceeded();
        ThenBpmdIdIs(3u);
        ThenBpmdMessageIsNull();
    }

    [Fact]
    public void ParseBpmdOutput_WhenSettingBreakpointFormat_ReturnsSuccessWithId()
    {
        GivenBpmdOutput("Setting breakpoint: bp 5 [00007FFA12345678]");

        WhenParsingBpmdOutput();

        ThenBpmdSucceeded();
        ThenBpmdIdIs(5u);
    }

    // ── ParseBpmdOutput (pending) ───────────────────────────

    [Fact]
    public void ParseBpmdOutput_WhenPending_ReturnsSuccessWithoutId()
    {
        GivenBpmdOutput("Adding pending breakpoints...");

        WhenParsingBpmdOutput();

        ThenBpmdSucceeded();
        ThenBpmdIdIsNull();
        ThenBpmdMessageContains("Pending");
    }

    // ── ParseBpmdOutput (error) ─────────────────────────────

    [Fact]
    public void ParseBpmdOutput_WhenError_ReturnsFailure()
    {
        GivenBpmdOutput("Error: could not find method");

        WhenParsingBpmdOutput();

        ThenBpmdFailed();
    }

    [Fact]
    public void ParseBpmdOutput_WhenNotFound_ReturnsFailure()
    {
        GivenBpmdOutput("Method not found in assembly");

        WhenParsingBpmdOutput();

        ThenBpmdFailed();
    }

    [Fact]
    public void ParseBpmdOutput_WhenEmpty_ReturnsFailure()
    {
        GivenBpmdOutput("");

        WhenParsingBpmdOutput();

        ThenBpmdFailed();
    }

    [Fact]
    public void ParseBpmdOutput_WhenNull_ReturnsFailure()
    {
        GivenBpmdOutput(null!);

        WhenParsingBpmdOutput();

        ThenBpmdFailed();
    }

    // ── ParseBpmdOutput (unknown output) ────────────────────

    [Fact]
    public void ParseBpmdOutput_WhenUnknownOutput_ReturnsTrueWithMessage()
    {
        GivenBpmdOutput("Some unknown SOS output we haven't seen before");

        WhenParsingBpmdOutput();

        ThenBpmdSucceeded();
        ThenBpmdIdIsNull();
    }

    [Fact]
    public void ParseBpmdOutput_WhenMultipleBreakpointsSet_ReturnsFirstId()
    {
        GivenBpmdOutput("Breakpoint 7 set.\nBreakpoint 8 set.");

        WhenParsingBpmdOutput();

        ThenBpmdSucceeded();
        ThenBpmdIdIs(7u);
    }

    [Fact]
    public void ParseBpmdOutput_WhenCaseInsensitive_MatchesBreakpointSet()
    {
        GivenBpmdOutput("breakpoint 12 SET.");

        WhenParsingBpmdOutput();

        ThenBpmdSucceeded();
        ThenBpmdIdIs(12u);
    }

    [Fact]
    public void ParseBpmdOutput_WhenFailed_ReturnsFailure()
    {
        GivenBpmdOutput("Failed to resolve method");

        WhenParsingBpmdOutput();

        ThenBpmdFailed();
    }

    #region Given

    private string _bpmdOutput = "";

    private void GivenBpmdOutput(string output) => _bpmdOutput = output;

    #endregion

    #region When

    private (bool Success, uint? BpId, string? Message) _bpmdResult;

    private void WhenParsingBpmdOutput() => _bpmdResult = SosOutputParser.ParseBpmdOutput(_bpmdOutput);

    #endregion

    #region Then

    private void ThenBpmdSucceeded() => Assert.True(_bpmdResult.Success);
    private void ThenBpmdFailed() => Assert.False(_bpmdResult.Success);
    private void ThenBpmdIdIs(uint expected) => Assert.Equal(expected, _bpmdResult.BpId);
    private void ThenBpmdIdIsNull() => Assert.Null(_bpmdResult.BpId);
    private void ThenBpmdMessageIsNull() => Assert.Null(_bpmdResult.Message);

    private void ThenBpmdMessageContains(string substring)
        => Assert.Contains(substring, _bpmdResult.Message, StringComparison.OrdinalIgnoreCase);

    #endregion
}