using System.Text;
using System.Text.Json;

using MixDbg.Models;
using MixDbg.Models.Dap;

namespace MixDbg.Services;

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
        Dictionary<string, string>? headers = ReadHeaders(model);
        if (headers is null) return null;

        if (!headers.TryGetValue("Content-Length", out string? lengthStr)
            || !int.TryParse(lengthStr, out int length))
        {
            throw new InvalidOperationException("Missing or invalid Content-Length header");
        }

        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = model.Input.Read(body, read, length - read);
            if (n == 0) return null;
            read += n;
        }

        string json = Encoding.UTF8.GetString(body);
        return JsonSerializer.Deserialize<RequestMessage>(json, JsonOpts);
    }

    public void SendResponse(DapServerModel model, RequestMessage request, object? body = null)
    {
        ResponseMessage response = new()
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
        ResponseMessage response = new()
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
        EventMessage evt = new()
        {
            Seq = NextSeq(model),
            Event = eventName,
            Body = body,
        };
        WriteMessage(model, evt);
    }

    private static void WriteMessage(DapServerModel model, ProtocolMessage message)
    {
        string json = JsonSerializer.Serialize(message, message.GetType(), JsonOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string header = $"Content-Length: {bytes.Length}\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

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
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? line = ReadLine(model);
            if (line is null) return null;
            if (line.Length == 0) break;

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                headers[key] = value;
            }
        }
        return headers.Count > 0 ? headers : null;
    }

    private static string? ReadLine(DapServerModel model)
    {
        StringBuilder sb = new();
        while (true)
        {
            int b = model.Input.ReadByte();
            if (b == -1) return sb.Length > 0 ? sb.ToString() : null;
            if (b == '\r')
            {
                int next = model.Input.ReadByte();
                if (next == '\n') return sb.ToString();
                _ = sb.Append((char)b);
                if (next != -1) _ = sb.Append((char)next);
            }
            else
            {
                _ = sb.Append((char)b);
            }
        }
    }
}