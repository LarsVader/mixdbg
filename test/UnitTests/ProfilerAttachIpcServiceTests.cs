using System.Buffers.Binary;
using System.IO.Pipes;

using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

/// <summary>
/// Drives the real <see cref="ProfilerAttachIpcService"/> against a local
/// <see cref="NamedPipeServerStream"/> standing in for <c>dotnet-diagnostic-{pid}</c>.
/// Asserts the bytes on the wire so the framing matches the runtime's expectations
/// (dotnet/diagnostics IPC protocol).
/// </summary>
public sealed class ProfilerAttachIpcServiceTests : IDisposable
{
    private const int FakePid = 99999;

    [Fact]
    public void AttachProfiler_SendsCorrectHeaderAndPayload()
    {
        GivenServerWillRespond(hresult: 0);
        Guid clsid = new("{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}");

        WhenAttaching(clsid, profilerPath: @"C:\test\MixDbgProfiler.dll",
            clientData: [0xAA, 0xBB, 0xCC, 0xDD], attachTimeout: 5000);

        ThenServerReceivedMagic();
        ThenServerReceivedCommand(set: 0x03, id: 0x01);
        ThenServerReceivedAttachTimeout(5000);
        ThenServerReceivedClsid(clsid);
        ThenServerReceivedProfilerPath(@"C:\test\MixDbgProfiler.dll");
        ThenServerReceivedClientData([0xAA, 0xBB, 0xCC, 0xDD]);
    }

    [Fact]
    public void AttachProfiler_OnSOk_ReturnsSuccessfully()
    {
        GivenServerWillRespond(hresult: 0);

        Exception? ex = Record.Exception(() => WhenAttaching(Guid.NewGuid(), "p.dll", []));

        Assert.Null(ex);
    }

    [Fact]
    public void AttachProfiler_OnProfilerAlreadyActive_ThrowsWithSpecificMessage()
    {
        GivenServerWillRespond(hresult: unchecked((int)0x80131367));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => WhenAttaching(Guid.NewGuid(), "p.dll", []));

        Assert.Contains("already attached", ex.Message);
    }

    [Fact]
    public void AttachProfiler_OnAccessDenied_ThrowsWithSpecificMessage()
    {
        GivenServerWillRespond(hresult: unchecked((int)0x80070005));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => WhenAttaching(Guid.NewGuid(), "p.dll", []));

        Assert.Contains("access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachProfiler_OnGenericFailure_IncludesHResult()
    {
        GivenServerWillRespond(hresult: unchecked((int)0x80004005));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => WhenAttaching(Guid.NewGuid(), "p.dll", []));

        Assert.Contains("0x80004005", ex.Message);
    }

    [Fact]
    public void AttachProfiler_WhenServerNotPresent_ThrowsConnectionError()
    {
        // No server started — connect will time out.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _testee.AttachProfiler(
                pid: FakePid + 1,            // different PID, no server
                profilerClsid: Guid.NewGuid(),
                profilerPath: "p.dll",
                clientData: [],
                attachTimeoutMs: 1000,
                ipcTimeoutMs: 500));

        Assert.Contains("diagnostic pipe", ex.Message);
    }

    [Fact]
    public void AttachProfiler_WhenServerStallsAfterAccept_TimesOutInsteadOfHanging()
    {
        // Server that accepts the connection but never writes a response.
        // This exercises ReadExact's CancellationTokenSource timeout —
        // a naive "check deadline after Read returns" loop hangs forever
        // here because NamedPipeClientStream.Read blocks indefinitely.
        _server = new NamedPipeServerStream(
            $"dotnet-diagnostic-{FakePid}",
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _serverTask = Task.Run(() =>
        {
            try
            {
                _server.WaitForConnection();
                // Read the request to drain the pipe, then never reply.
                byte[] header = new byte[20];
                ReadExact(_server, header);
                ushort total = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
                byte[] rest = new byte[total - 20];
                ReadExact(_server, rest);
                // Intentionally hold the connection open without writing a response.
                Thread.Sleep(5_000);
            }
            catch { /* test will end before this matters */ }
        });

        TimeoutException ex = Assert.Throws<TimeoutException>(() =>
            _testee.AttachProfiler(
                pid: FakePid,
                profilerClsid: Guid.NewGuid(),
                profilerPath: "p.dll",
                clientData: [],
                attachTimeoutMs: 1000,
                ipcTimeoutMs: 500));

        Assert.Contains("timed out", ex.Message);
    }

    #region Given

    private void GivenServerWillRespond(int hresult)
    {
        _server = new NamedPipeServerStream(
            $"dotnet-diagnostic-{FakePid}",
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _hresult = hresult;
        _serverTask = Task.Run(RunServer);
    }

    #endregion

    #region When

    private void WhenAttaching(Guid clsid, string profilerPath, byte[] clientData,
        uint attachTimeout = 10_000)
    {
        _testee.AttachProfiler(FakePid, clsid, profilerPath, clientData, attachTimeout, ipcTimeoutMs: 5_000);
        // Wait for server to finish reading + writing so subsequent assertions see _received.
        _ = _serverTask?.Wait(TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Then

    private void ThenServerReceivedMagic()
        => Assert.Equal("DOTNET_IPC_V1\0"u8.ToArray(), _received![..14]);

    private void ThenServerReceivedCommand(byte set, byte id)
    {
        Assert.Equal(set, _received![16]);
        Assert.Equal(id, _received![17]);
    }

    private void ThenServerReceivedAttachTimeout(uint expected)
        => Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(_received.AsSpan(20, 4)));

    private void ThenServerReceivedClsid(Guid expected)
    {
        Span<byte> actual = _received.AsSpan(24, 16);
        byte[] expectedBytes = new byte[16];
        Assert.True(expected.TryWriteBytes(expectedBytes));
        Assert.True(actual.SequenceEqual(expectedBytes));
    }

    private void ThenServerReceivedProfilerPath(string expected)
    {
        // After 4 (timeout) + 16 (clsid) = pos 20+20 = 40, profiler_path length+chars
        int pos = 40;
        uint pathLen = BinaryPrimitives.ReadUInt32LittleEndian(_received.AsSpan(pos, 4));
        pos += 4;
        Assert.Equal((uint)(expected.Length + 1), pathLen);
        for (int i = 0; i < expected.Length; i++)
        {
            ushort c = BinaryPrimitives.ReadUInt16LittleEndian(_received.AsSpan(pos, 2));
            Assert.Equal(expected[i], (char)c);
            pos += 2;
        }
        // Null terminator
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(_received.AsSpan(pos, 2)));
    }

    private void ThenServerReceivedClientData(byte[] expected)
    {
        // Locate end of profiler_path (which we read in ThenServerReceivedProfilerPath if called).
        // Recompute from scratch to avoid coupling test order.
        int pos = 40;
        uint pathChars = BinaryPrimitives.ReadUInt32LittleEndian(_received.AsSpan(pos, 4));
        pos += 4 + (int)pathChars * 2;

        uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(_received.AsSpan(pos, 4));
        pos += 4;
        Assert.Equal((uint)expected.Length, dataLen);
        Assert.True(_received.AsSpan(pos, expected.Length).SequenceEqual(expected));
    }

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore = new(Path.Combine(Path.GetTempPath(), "test-attach-ipc.log"));
    private readonly ProfilerAttachIpcService _testee;

    private NamedPipeServerStream? _server;
    private Task? _serverTask;
    private byte[]? _received;
    private int _hresult;

    public ProfilerAttachIpcServiceTests() => _testee = new ProfilerAttachIpcService(_log, _logStore);

    private void RunServer()
    {
        try
        {
            _server!.WaitForConnection();

            // Read the request header to learn the total size.
            byte[] header = new byte[20];
            ReadExact(_server, header);
            ushort total = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));

            // Read the rest of the request.
            byte[] rest = new byte[total - 20];
            ReadExact(_server, rest);

            _received = new byte[total];
            header.CopyTo(_received, 0);
            rest.CopyTo(_received, 20);

            // Build response: header (server OK) + int32 HRESULT
            byte[] response = new byte[24];
            "DOTNET_IPC_V1\0"u8.CopyTo(response);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(14, 2), 24);
            response[16] = 0xFF; // server OK command_set
            response[17] = 0x00; // server OK command_id
            // reserved already zero
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(20, 4), _hresult);

            _server.Write(response);
            _server.Flush();
        }
        catch
        {
            // Server-side errors don't interest the tests; the testee will surface
            // its own connection/timeout error.
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int now = stream.Read(buffer, read, buffer.Length - read);
            if (now == 0)
                throw new EndOfStreamException();
            read += now;
        }
    }

    public void Dispose()
    {
        try { _server?.Dispose(); } catch { }
        _ = _serverTask?.Wait(TimeSpan.FromSeconds(2));
    }

    #endregion
}
