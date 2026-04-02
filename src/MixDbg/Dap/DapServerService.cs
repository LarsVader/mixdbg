using System.Text;
using System.Text.Json;
using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg.Dap;

/// <summary>
/// Stateless DAP transport service. All mutable state lives in
/// <see cref="DapServerModel"/>.
/// </summary>
internal sealed class DapServerService : IDapServer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public DapServerModel CreateModel(Stream input, Stream output)
        => new(input, output);

    public RequestMessage? ReadRequest(DapServerModel model)
    {
        var headers = ReadHeaders(model);
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
            var n = model.Input.Read(body, read, length - read);
            if (n == 0) return null;
            read += n;
        }

        var json = Encoding.UTF8.GetString(body);
        return JsonSerializer.Deserialize<RequestMessage>(json, JsonOpts);
    }

    public void SendResponse(DapServerModel model, RequestMessage request, object? body = null)
    {
        var response = new ResponseMessage
        {
            Seq = NextSeq(model),
            RequestSeq = request.Seq,
            Success = true,
            Command = request.Command,
            Body = body,
        };
        WriteMessage(model, response);
    }

    public void SendErrorResponse(DapServerModel model, RequestMessage request, string message)
    {
        var response = new ResponseMessage
        {
            Seq = NextSeq(model),
            RequestSeq = request.Seq,
            Success = false,
            Command = request.Command,
            Message = message,
        };
        WriteMessage(model, response);
    }

    public void SendEvent(DapServerModel model, string eventName, object? body = null)
    {
        var evt = new EventMessage
        {
            Seq = NextSeq(model),
            Event = eventName,
            Body = body,
        };
        WriteMessage(model, evt);
    }

    private static void WriteMessage(DapServerModel model, ProtocolMessage message)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        lock (model.WriteLock)
        {
            model.Output.Write(headerBytes);
            model.Output.Write(bytes);
            model.Output.Flush();
        }
    }

    private static int NextSeq(DapServerModel model)
        => Interlocked.Increment(ref model.Seq);

    private static Dictionary<string, string>? ReadHeaders(DapServerModel model)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = ReadLine(model);
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

    private static string? ReadLine(DapServerModel model)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = model.Input.ReadByte();
            if (b == -1) return sb.Length > 0 ? sb.ToString() : null;
            if (b == '\r')
            {
                var next = model.Input.ReadByte();
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
