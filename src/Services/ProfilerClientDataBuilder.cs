using System.Buffers.Binary;
using System.Text;

namespace MixDbg.Services;

/// <summary>
/// Builds the binary client-data payload passed to the CLR profiler via
/// <c>ICorProfilerCallback3::InitializeForAttach</c>. The launch path uses
/// environment variables for these values, but env vars cannot be retroactively
/// set on a running process — so attach passes the equivalent configuration
/// inline as a byte buffer.
///
/// Layout (all little-endian):
///   uint32 version           // currently 1
///   uint16 pipeNameLen       // UTF-16 char count, excluding terminator
///   wchar  pipeName[pipeNameLen]
///   uint16 ackEventLen
///   wchar  ackEventName[ackEventLen]
///   uint16 cmdPipeLen
///   wchar  cmdPipeName[cmdPipeLen]
///   uint32 watchCount
///   foreach watch:
///     uint16 asmLen          // UTF-8 byte count, excluding terminator
///     byte   asmName[asmLen]
///     uint32 token
/// </summary>
internal static class ProfilerClientDataBuilder
{
    public const uint Version = 1;

    public static byte[] Build(
        string pipeName,
        string ackEventName,
        string cmdPipeName,
        IReadOnlyList<(string Assembly, int Token)> watchTokens)
    {
        ArgumentNullException.ThrowIfNull(pipeName);
        ArgumentNullException.ThrowIfNull(ackEventName);
        ArgumentNullException.ThrowIfNull(cmdPipeName);
        ArgumentNullException.ThrowIfNull(watchTokens);

        int size = sizeof(uint)              // version
                 + sizeof(ushort) + pipeName.Length * 2
                 + sizeof(ushort) + ackEventName.Length * 2
                 + sizeof(ushort) + cmdPipeName.Length * 2
                 + sizeof(uint);             // watchCount

        byte[][] asmBytes = new byte[watchTokens.Count][];
        for (int i = 0; i < watchTokens.Count; i++)
        {
            asmBytes[i] = Encoding.UTF8.GetBytes(watchTokens[i].Assembly);
            size += sizeof(ushort) + asmBytes[i].Length + sizeof(uint);
        }

        byte[] buffer = new byte[size];
        Span<byte> span = buffer;
        int pos = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], Version);
        pos += sizeof(uint);

        pos = WriteUtf16String(span, pos, pipeName);
        pos = WriteUtf16String(span, pos, ackEventName);
        pos = WriteUtf16String(span, pos, cmdPipeName);

        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], (uint)watchTokens.Count);
        pos += sizeof(uint);

        for (int i = 0; i < watchTokens.Count; i++)
        {
            byte[] asm = asmBytes[i];
            BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], checked((ushort)asm.Length));
            pos += sizeof(ushort);
            asm.CopyTo(span[pos..]);
            pos += asm.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], (uint)watchTokens[i].Token);
            pos += sizeof(uint);
        }

        return pos != size
            ? throw new InvalidOperationException($"ProfilerClientDataBuilder: wrote {pos} bytes, expected {size}")
            : buffer;
    }

    /// <summary>
    /// Reverse of <see cref="Build"/>. Used by unit tests; the production reader
    /// is the C++ profiler.
    /// </summary>
    public static (string PipeName, string AckEventName, string CmdPipeName,
                   IReadOnlyList<(string Assembly, int Token)> WatchTokens)
        Parse(ReadOnlySpan<byte> blob)
    {
        int pos = 0;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
        pos += sizeof(uint);
        if (version != Version)
            throw new InvalidOperationException($"Unsupported client-data version {version}");

        (string pipeName, pos) = ReadUtf16String(blob, pos);
        (string ackEventName, pos) = ReadUtf16String(blob, pos);
        (string cmdPipeName, pos) = ReadUtf16String(blob, pos);

        uint watchCount = BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
        pos += sizeof(uint);

        List<(string Assembly, int Token)> tokens = new((int)watchCount);
        for (uint i = 0; i < watchCount; i++)
        {
            ushort asmLen = BinaryPrimitives.ReadUInt16LittleEndian(blob[pos..]);
            pos += sizeof(ushort);
            string assembly = Encoding.UTF8.GetString(blob.Slice(pos, asmLen));
            pos += asmLen;
            int token = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
            pos += sizeof(uint);
            tokens.Add((assembly, token));
        }

        return (pipeName, ackEventName, cmdPipeName, tokens);
    }

    private static int WriteUtf16String(Span<byte> span, int pos, string value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], checked((ushort)value.Length));
        pos += sizeof(ushort);
        for (int i = 0; i < value.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], value[i]);
            pos += sizeof(ushort);
        }
        return pos;
    }

    private static (string Value, int NewPos) ReadUtf16String(ReadOnlySpan<byte> span, int pos)
    {
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
        pos += sizeof(ushort);
        char[] chars = new char[len];
        for (int i = 0; i < len; i++)
        {
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
            pos += sizeof(ushort);
        }
        return (new string(chars), pos);
    }
}
