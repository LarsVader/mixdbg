using System.Buffers.Binary;
using System.IO.Pipes;

using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Diagnostic IPC client that drives <c>AttachProfiler</c> against a running
/// .NET process. Wire format documented at
/// dotnet/diagnostics/documentation/design-docs/ipc-protocol.md.
/// </summary>
internal sealed class ProfilerAttachIpcService(ILoggingService log, LogStore logStore)
    : IProfilerAttachIpcService
{
    private const int HeaderSize = 20;
    private const byte ProfilerCommandSet = 0x03;
    private const byte AttachProfilerCommandId = 0x01;
    private const byte ServerOkCommandSet = 0xFF;
    private const byte ServerOkCommandId = 0x00;
    private const byte ServerErrorCommandSet = 0xFF;
    private const byte ServerErrorCommandId = 0xFF;

    private const int HrSOk = 0;
    private const int HrEAccessDenied = unchecked((int)0x80070005);
    private const int HrCorprofProfilerAlreadyActive = unchecked((int)0x80131367);

    private static ReadOnlySpan<byte> Magic => "DOTNET_IPC_V1\0"u8;

    public void AttachProfiler(
        int pid,
        Guid profilerClsid,
        string profilerPath,
        byte[] clientData,
        uint attachTimeoutMs = 10_000,
        int ipcTimeoutMs = 30_000)
    {
        ArgumentNullException.ThrowIfNull(profilerPath);
        ArgumentNullException.ThrowIfNull(clientData);

        byte[] message = BuildMessage(attachTimeoutMs, profilerClsid, profilerPath, clientData);

        string pipeName = $"dotnet-diagnostic-{pid}";
        log.LogInfo(logStore, $"ProfilerAttachIpc: connecting to {pipeName}, profiler={profilerPath}, clientData={clientData.Length} bytes");

        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(ipcTimeoutMs);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Timed out connecting to .NET diagnostic pipe '{pipeName}' — is the target a .NET (CoreCLR) process and running as the same user?");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Access denied opening diagnostic pipe '{pipeName}'. Run as the same user as the target, or elevate.");
        }

        pipe.Write(message, 0, message.Length);
        pipe.Flush();

        int hr = ReadResponseHResult(pipe, ipcTimeoutMs);
        if (hr == HrSOk)
        {
            log.LogInfo(logStore, "ProfilerAttachIpc: AttachProfiler succeeded");
            return;
        }

        string detail = hr switch
        {
            HrCorprofProfilerAlreadyActive
                => "another profiler is already attached (CORPROF_E_PROFILER_ALREADY_ACTIVE)",
            HrEAccessDenied
                => "access denied (E_ACCESSDENIED) — verify same-user / elevation",
            _ => $"HRESULT 0x{hr:X8}",
        };
        throw new InvalidOperationException($"AttachProfiler failed: {detail}");
    }

    private static byte[] BuildMessage(uint attachTimeoutMs, Guid profilerClsid,
        string profilerPath, byte[] clientData)
    {
        // Strings on the wire: uint32 char count (incl. null terminator), then
        // UTF-16 LE chars including the null terminator.
        int profilerPathBytes = (profilerPath.Length + 1) * 2;
        int payloadSize = sizeof(uint)                         // attach timeout
                        + 16                                   // CLSID
                        + sizeof(uint) + profilerPathBytes     // profiler_path
                        + sizeof(uint) + clientData.Length;    // client_data

        int total = HeaderSize + payloadSize;
        if (total > ushort.MaxValue)
            throw new InvalidOperationException($"AttachProfiler message too large: {total} bytes");

        byte[] buffer = new byte[total];
        Span<byte> span = buffer;

        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)total);
        span[16] = ProfilerCommandSet;
        span[17] = AttachProfilerCommandId;
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], 0);

        int pos = HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], attachTimeoutMs);
        pos += sizeof(uint);

        if (!profilerClsid.TryWriteBytes(span.Slice(pos, 16)))
            throw new InvalidOperationException("Failed to serialize profiler CLSID");
        pos += 16;

        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], (uint)(profilerPath.Length + 1));
        pos += sizeof(uint);
        for (int i = 0; i < profilerPath.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], profilerPath[i]);
            pos += sizeof(ushort);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], 0); // null terminator
        pos += sizeof(ushort);

        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], (uint)clientData.Length);
        pos += sizeof(uint);
        clientData.CopyTo(span[pos..]);
        pos += clientData.Length;

        return pos != total
            ? throw new InvalidOperationException($"ProfilerAttachIpc: wrote {pos} bytes, expected {total}")
            : buffer;
    }

    private static int ReadResponseHResult(Stream pipe, int timeoutMs)
    {
        byte[] header = ReadExact(pipe, HeaderSize, timeoutMs);

        if (!new ReadOnlySpan<byte>(header, 0, 14).SequenceEqual(Magic))
            throw new InvalidOperationException("AttachProfiler response: invalid magic");

        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
        byte commandSet = header[16];
        byte commandId = header[17];

        if (size < HeaderSize)
            throw new InvalidOperationException($"AttachProfiler response: bogus size {size}");

        // Both OK and Error responses carry an int32 HRESULT payload; an Error
        // response sometimes omits it, in which case we synthesize E_FAIL.
        int payloadLen = size - HeaderSize;
        if (payloadLen < sizeof(int))
        {
            // Drain whatever payload is present (defensive) — typically 0 bytes.
            if (payloadLen > 0)
                _ = ReadExact(pipe, payloadLen, timeoutMs);
            return commandSet == ServerOkCommandSet && commandId == ServerOkCommandId
                ? HrSOk
                : unchecked((int)0x80004005); // E_FAIL
        }

        byte[] payload = ReadExact(pipe, payloadLen, timeoutMs);
        return commandSet == ServerErrorCommandSet && commandId == ServerErrorCommandId
            ? BinaryPrimitives.ReadInt32LittleEndian(payload)
            : commandSet == ServerOkCommandSet && commandId == ServerOkCommandId
                ? BinaryPrimitives.ReadInt32LittleEndian(payload)
                : throw new InvalidOperationException(
                    $"AttachProfiler response: unexpected command 0x{commandSet:X2}/0x{commandId:X2}");
    }

    /// <summary>
    /// Reads <paramref name="count"/> bytes with a real wall-clock timeout.
    /// NamedPipeClientStream.Read is unconditionally blocking and ignores any
    /// stream ReadTimeout, so the only way to bound the wait is to drive the
    /// read async and cancel the surrounding CancellationTokenSource. A naive
    /// "check deadline after Read returns" loop hangs indefinitely if the
    /// other end never writes a byte.
    /// </summary>
    private static byte[] ReadExact(Stream stream, int count, int timeoutMs)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        using CancellationTokenSource cts = new(timeoutMs);
        try
        {
            while (read < count)
            {
                int now = stream.ReadAsync(buffer.AsMemory(read, count - read), cts.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                if (now == 0)
                {
                    throw new EndOfStreamException(
                        $"AttachProfiler: pipe closed after {read}/{count} bytes");
                }
                read += now;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"AttachProfiler: timed out after reading {read}/{count} bytes");
        }
        return buffer;
    }

}
