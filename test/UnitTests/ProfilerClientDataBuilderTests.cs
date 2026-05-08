using System.Buffers.Binary;
using System.Text;

using MixDbg.Services;

namespace MixDbg.Tests;

public sealed class ProfilerClientDataBuilderTests
{
    [Fact]
    public void Build_RoundTrip_ReturnsOriginalValues()
    {
        GivenInputs(
            pipeName: @"\\.\pipe\MixDbgProfiler-1234-abc",
            ackEventName: "MixDbgProfilerAck-test",
            cmdPipeName: @"\\.\pipe\MixDbgProfilerCmd-test",
            watchTokens: [("WpfApp", 0x06000001), ("CliWrapper", 0x06000005)]);

        WhenBuilding();
        WhenParsing();

        ThenParsedPipeNameMatches();
        ThenParsedAckEventNameMatches();
        ThenParsedCmdPipeNameMatches();
        ThenParsedWatchTokensMatch();
    }

    [Fact]
    public void Build_WithEmptyWatchList_RoundTrips()
    {
        GivenInputs(
            pipeName: "p",
            ackEventName: "a",
            cmdPipeName: "c",
            watchTokens: []);

        WhenBuilding();
        WhenParsing();

        ThenParsedWatchTokensMatch();
    }

    [Fact]
    public void Build_WithUnicodeNames_RoundTripsCorrectly()
    {
        GivenInputs(
            pipeName: @"\\.\pipe\Müßig-✓",
            ackEventName: "Acknowledgement-✓",
            cmdPipeName: @"\\.\pipe\Cmd-✓",
            watchTokens: [("Bär.dll", 0x06ABCDEF)]);

        WhenBuilding();
        WhenParsing();

        ThenParsedPipeNameMatches();
        ThenParsedAckEventNameMatches();
        ThenParsedCmdPipeNameMatches();
        ThenParsedWatchTokensMatch();
    }

    [Fact]
    public void Build_BlobStartsWithVersion1LittleEndian()
    {
        GivenInputs("p", "a", "c", []);

        WhenBuilding();

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(_blob));
    }

    [Fact]
    public void Build_AssemblyNameEncodedAsUtf8()
    {
        // "ä" is 0xC3 0xA4 in UTF-8. We can locate it by scanning past the
        // 4-byte version + 3 length-prefixed UTF-16 names + 4-byte watchCount.
        GivenInputs(
            pipeName: "",
            ackEventName: "",
            cmdPipeName: "",
            watchTokens: [("ä", 0x06000001)]);

        WhenBuilding();

        // version(4) + 3*(uint16 len + 0 chars) + uint32 watchCount(4) = 14
        // then asmLen uint16 = 2 bytes, then UTF-8 "ä" = 2 bytes.
        ReadOnlySpan<byte> span = _blob!;
        int pos = 4 + 3 * sizeof(ushort) + 4;
        ushort asmLen = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
        Assert.Equal(2, asmLen);
        Assert.Equal(0xC3, _blob![pos + 2]);
        Assert.Equal(0xA4, _blob[pos + 3]);
    }

    [Fact]
    public void Parse_WithUnsupportedVersion_Throws()
    {
        byte[] bad = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(bad, 99);

        _ = Assert.Throws<InvalidOperationException>(() => ProfilerClientDataBuilder.Parse(bad));
    }

    [Fact]
    public void Build_WithNullPipeName_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ProfilerClientDataBuilder.Build(null!, "a", "c", []));

    [Fact]
    public void Build_TokensRoundTripPreservesInt32SignedRepresentation()
    {
        // Method tokens are uint32 on the wire but stored as int in mixdbg.
        // High-bit-set tokens (e.g. 0x80000001) must round-trip.
        GivenInputs(
            pipeName: "p",
            ackEventName: "a",
            cmdPipeName: "c",
            watchTokens: [("Asm", unchecked((int)0x80000001))]);

        WhenBuilding();
        WhenParsing();

        Assert.Equal(unchecked((int)0x80000001), _parsed!.Value.WatchTokens[0].Token);
    }

    #region Given

    private void GivenInputs(string pipeName, string ackEventName, string cmdPipeName,
        IReadOnlyList<(string Assembly, int Token)> watchTokens)
    {
        _pipeName = pipeName;
        _ackEventName = ackEventName;
        _cmdPipeName = cmdPipeName;
        _watchTokens = watchTokens;
    }

    #endregion

    #region When

    private void WhenBuilding() => _blob = ProfilerClientDataBuilder.Build(
        _pipeName!, _ackEventName!, _cmdPipeName!, _watchTokens!);

    private void WhenParsing() => _parsed = ProfilerClientDataBuilder.Parse(_blob!);

    #endregion

    #region Then

    private void ThenParsedPipeNameMatches() => Assert.Equal(_pipeName, _parsed!.Value.PipeName);
    private void ThenParsedAckEventNameMatches() => Assert.Equal(_ackEventName, _parsed!.Value.AckEventName);
    private void ThenParsedCmdPipeNameMatches() => Assert.Equal(_cmdPipeName, _parsed!.Value.CmdPipeName);
    private void ThenParsedWatchTokensMatch()
    {
        Assert.Equal(_watchTokens!.Count, _parsed!.Value.WatchTokens.Count);
        for (int i = 0; i < _watchTokens.Count; i++)
        {
            Assert.Equal(_watchTokens[i].Assembly, _parsed.Value.WatchTokens[i].Assembly);
            Assert.Equal(_watchTokens[i].Token, _parsed.Value.WatchTokens[i].Token);
        }
    }

    #endregion

    #region Misc

    private string? _pipeName;
    private string? _ackEventName;
    private string? _cmdPipeName;
    private IReadOnlyList<(string Assembly, int Token)>? _watchTokens;
    private byte[]? _blob;
    private (string PipeName, string AckEventName, string CmdPipeName,
             IReadOnlyList<(string Assembly, int Token)> WatchTokens)? _parsed;

    #endregion
}
