using System.Text;
using System.Text.Json;
using MixDbg.Services;

namespace MixDbg.Dap;

/// <summary>
/// Reads DAP messages from stdin and writes responses/events to stdout.
/// DAP uses HTTP-style Content-Length headers followed by JSON bodies.
/// </summary>
public sealed class DapServer : IDapServer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private int _seq;

    public DapServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Reads the next DAP request from the input stream.
    /// Returns null on EOF.
    /// </summary>
    public RequestMessage? ReadRequest()
    {
        var headers = ReadHeaders();
        if (headers is null) return null;

        if (!headers.TryGetValue("Content-Length", out var lengthStr)
            || !int.TryParse(lengthStr, out var length))
        {
            throw new InvalidOperationException("Missing or invalid Content-Length header");
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = _input.Read(body, read, length - read);
            if (n == 0) return null;
            read += n;
        }

        var json = Encoding.UTF8.GetString(body);
        return JsonSerializer.Deserialize<RequestMessage>(json, JsonOpts);
    }

    /// <summary>
    /// Sends a successful response to a request.
    /// </summary>
    public void SendResponse(RequestMessage request, object? body = null)
    {
        var response = new ResponseMessage
        {
            Seq = NextSeq(),
            RequestSeq = request.Seq,
            Success = true,
            Command = request.Command,
            Body = body,
        };
        WriteMessage(response);
    }

    /// <summary>
    /// Sends an error response to a request.
    /// </summary>
    public void SendErrorResponse(RequestMessage request, string message)
    {
        var response = new ResponseMessage
        {
            Seq = NextSeq(),
            RequestSeq = request.Seq,
            Success = false,
            Command = request.Command,
            Message = message,
        };
        WriteMessage(response);
    }

    /// <summary>
    /// Sends a DAP event.
    /// </summary>
    public void SendEvent(string eventName, object? body = null)
    {
        var evt = new EventMessage
        {
            Seq = NextSeq(),
            Event = eventName,
            Body = body,
        };
        WriteMessage(evt);
    }

    private void WriteMessage(ProtocolMessage message)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        lock (_writeLock)
        {
            _output.Write(headerBytes);
            _output.Write(bytes);
            _output.Flush();
        }
    }

    private int NextSeq() => Interlocked.Increment(ref _seq);

    private Dictionary<string, string>? ReadHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = ReadLine();
            if (line is null) return null;
            if (line.Length == 0) break;

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers[key] = value;
            }
        }
        return headers.Count > 0 ? headers : null;
    }

    private string? ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = _input.ReadByte();
            if (b == -1) return sb.Length > 0 ? sb.ToString() : null;
            if (b == '\r')
            {
                var next = _input.ReadByte();
                if (next == '\n') return sb.ToString();
                sb.Append((char)b);
                if (next != -1) sb.Append((char)next);
            }
            else
            {
                sb.Append((char)b);
            }
        }
    }
}
