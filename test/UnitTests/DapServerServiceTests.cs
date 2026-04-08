using System.Text;
using System.Text.Json;

using MixDbg.Models;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Initialize;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class DapServerServiceTests
{
    [Fact]
    public void CreateModel_WhenCalled_ReturnsModelWithStreams()
    {
        GivenInputOutput();

        WhenCreatingModel();

        ThenModelInputIs(_inputStream!);
        ThenModelOutputIs(_outputStream!);
    }

    [Fact]
    public void ReadRequest_WhenValidMessage_ReturnsRequest()
    {
        GivenADapRequest("initialize", seq: 1);

        WhenReadingRequest();

        ThenRequestIsNotNull();
        ThenRequestCommandIs("initialize");
        ThenRequestSeqIs(1);
    }

    [Fact]
    public void ReadRequest_WhenEof_ReturnsNull()
    {
        GivenAnEmptyInput();

        WhenReadingRequest();

        ThenRequestIsNull();
    }

    [Fact]
    public void ReadRequest_WhenMultipleRequests_ReadsSequentially()
    {
        GivenMultipleDapRequests("first", "second");

        WhenReadingRequest();
        ThenRequestCommandIs("first");

        WhenReadingRequest();
        ThenRequestCommandIs("second");
    }

    [Fact]
    public void SendResponse_WhenCalled_WritesContentLengthFramedJson()
    {
        GivenAModelWithOutputStream();
        GivenARequestMessage("test", seq: 5);

        WhenSendingResponse();

        ThenOutputContains("Content-Length:");
        ThenOutputContains("\"type\":\"response\"");
        ThenOutputContains("\"success\":true");
        ThenOutputContains("\"command\":\"test\"");
        ThenOutputContains("\"request_seq\":5");
    }

    [Fact]
    public void SendResponse_WhenBodyProvided_IncludesBody()
    {
        GivenAModelWithOutputStream();
        GivenARequestMessage("initialize", seq: 1);
        GivenAResponseBody(new Capabilities { SupportsConfigurationDoneRequest = true });

        WhenSendingResponseWithBody();

        ThenOutputContains("\"supportsConfigurationDoneRequest\":true");
    }

    [Fact]
    public void SendErrorResponse_WhenCalled_WritesErrorResponse()
    {
        GivenAModelWithOutputStream();
        GivenARequestMessage("bad", seq: 3);

        WhenSendingErrorResponse("something failed");

        ThenOutputContains("\"success\":false");
        ThenOutputContains("\"message\":\"something failed\"");
    }

    [Fact]
    public void SendEvent_WhenCalled_WritesEvent()
    {
        GivenAModelWithOutputStream();

        WhenSendingEvent("stopped", new StoppedEventBody { Reason = "breakpoint", ThreadId = 1 });

        ThenOutputContains("\"type\":\"event\"");
        ThenOutputContains("\"event\":\"stopped\"");
        ThenOutputContains("\"reason\":\"breakpoint\"");
    }

    [Fact]
    public void SendEvent_WhenNoBody_WritesEventWithoutBody()
    {
        GivenAModelWithOutputStream();

        WhenSendingEvent("initialized", null);

        ThenOutputContains("\"event\":\"initialized\"");
    }

    [Fact]
    public void SendResponse_WhenCalledMultipleTimes_IncrementsSeq()
    {
        GivenAModelWithOutputStream();
        GivenARequestMessage("first", seq: 1);

        WhenSendingResponse();
        WhenSendingResponse();

        ThenOutputContainsSeq(1);
        ThenOutputContainsSeq(2);
    }

    [Fact]
    public void ReadRequest_WhenMissingContentLength_ThrowsInvalidOperation()
    {
        GivenRawInput("Bad-Header: value\r\n\r\n{}");

        WhenReadingRequestExpectingException();

        ThenInvalidOperationExceptionWasThrown();
    }

    [Fact]
    public void ReadRequest_WhenInputEndsWithPartialContent_ReturnsNull()
    {
        // Content-Length says 100 bytes but only 5 bytes are available.
        GivenRawInput("Content-Length: 100\r\n\r\nshort");

        WhenReadingRequest();

        ThenRequestIsNull();
    }

    [Fact]
    public void ReadRequest_WhenHeaderHasNoColon_SkipsHeader()
    {
        // A header line without a colon should be silently ignored.
        // The next line provides a valid Content-Length.
        string json = "{\"seq\":1,\"type\":\"request\",\"command\":\"test\"}";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
        string raw = $"NoColonHeader\r\nContent-Length: {body.Length}\r\n\r\n{json}";
        GivenRawInput(raw);

        WhenReadingRequest();

        ThenRequestIsNotNull();
        ThenRequestCommandIs("test");
    }

    [Fact]
    public void ReadRequest_WhenCarriageReturnWithoutLineFeed_HandlesGracefully()
    {
        // \r followed by a non-\n character in the header line.
        // This tests the ReadLine branch where \r is not followed by \n.
        // Build "Content-Length: N\rX\r\n\r\n" + json — the \rX pair is appended to the header value.
        string json = "{\"seq\":1,\"type\":\"request\",\"command\":\"test\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);
        // "Content-Length: <len>\rX" forms a single logical line (CR not followed by LF).
        // Then normal "\r\n" terminates it, then blank "\r\n" ends headers.
        // The header value parsed will be " <len>\rX" which won't parse as int → exception.
        // To actually test the code path while still getting a valid request, we need a
        // non-essential header line with \r not followed by \n.
        byte[] raw = [
            // "Dummy: val\rX\r\n" — header with bare CR in value
            .. Encoding.ASCII.GetBytes("Dummy: val"),
            (byte)'\r', (byte)'X',
            (byte)'\r', (byte)'\n',
            // "Content-Length: <len>\r\n" — actual header
            .. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}"),
            (byte)'\r', (byte)'\n',
            // blank line ends headers
            (byte)'\r', (byte)'\n',
            // body
            .. body
        ];

        _inputStream = new MemoryStream(raw);
        _outputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);

        WhenReadingRequest();

        ThenRequestIsNotNull();
        ThenRequestCommandIs("test");
    }

    [Fact]
    public void ReadRequest_WhenCarriageReturnFollowedByEof_HandlesGracefully()
    {
        // \r at the very end of stream (next ReadByte returns -1).
        // Tests ReadLine branch: \r then next == -1 (sb.Append(\r) only).
        byte[] raw = [
            .. Encoding.ASCII.GetBytes("Content-Length: 5"),
            (byte)'\r',
            // EOF — no more bytes
        ];

        _inputStream = new MemoryStream(raw);
        _outputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);

        // ReadHeaders will parse "Content-Length: 5\r" as a single header line,
        // then hit EOF. The empty headers dict (only has malformed entries) returns null.
        WhenReadingRequest();

        ThenRequestIsNull();
    }

    #region Given

    private void GivenInputOutput()
    {
        _inputStream = new MemoryStream();
        _outputStream = new MemoryStream();
    }

    private void GivenAnEmptyInput()
    {
        _inputStream = new MemoryStream([]);
        _outputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);
    }

    private void GivenADapRequest(string command, int seq)
    {
        RequestMessage request = new() { Seq = seq, Command = command };
        string json = JsonSerializer.Serialize(request, _jsonOpts);
        byte[] body = Encoding.UTF8.GetBytes(json);
        string header = $"Content-Length: {body.Length}\r\n\r\n";
        byte[] full = [.. Encoding.ASCII.GetBytes(header), .. body];

        _inputStream = new MemoryStream(full);
        _outputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);
    }

    private void GivenMultipleDapRequests(params string[] commands)
    {
        MemoryStream ms = new();
        foreach ((string? cmd, int i) in commands.Select((c, i) => (c, i)))
        {
            RequestMessage request = new() { Seq = i + 1, Command = cmd };
            string json = JsonSerializer.Serialize(request, _jsonOpts);
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            ms.Write(header);
            ms.Write(body);
        }
        ms.Position = 0;

        _inputStream = ms;
        _outputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);
    }

    private void GivenAModelWithOutputStream()
    {
        _outputStream = new MemoryStream();
        _inputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);
    }

    private void GivenARequestMessage(string command, int seq) => _requestMessage = new RequestMessage { Seq = seq, Command = command };

    private void GivenAResponseBody(object body) => _responseBody = body;

    private void GivenRawInput(string raw)
    {
        _inputStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        _outputStream = new MemoryStream();
        _model = _testee.CreateModel(_inputStream, _outputStream);
    }

    #endregion

    #region When

    private void WhenCreatingModel() => _model = _testee.CreateModel(_inputStream!, _outputStream!);

    private void WhenReadingRequest() => _readResult = _testee.ReadRequest(_model!);

    private void WhenSendingResponse() => _testee.SendResponse(_model!, _requestMessage!);

    private void WhenSendingResponseWithBody() => _testee.SendResponse(_model!, _requestMessage!, _responseBody);

    private void WhenSendingErrorResponse(string message) => _testee.SendErrorResponse(_model!, _requestMessage!, message);

    private void WhenSendingEvent(string eventName, object? body) => _testee.SendEvent(_model!, eventName, body);

    private void WhenReadingRequestExpectingException()
    {
        try { _readResult = _testee.ReadRequest(_model!); }
        catch (Exception ex) { _thrownException = ex; }
    }

    #endregion

    #region Then

    private void ThenModelInputIs(Stream expected) => Assert.Same(expected, _model!.Input);

    private void ThenModelOutputIs(Stream expected) => Assert.Same(expected, _model!.Output);

    private void ThenRequestIsNotNull() => Assert.NotNull(_readResult);

    private void ThenRequestIsNull() => Assert.Null(_readResult);

    private void ThenRequestCommandIs(string expected) => Assert.Equal(expected, _readResult!.Command);

    private void ThenRequestSeqIs(int expected) => Assert.Equal(expected, _readResult!.Seq);

    private void ThenOutputContains(string expected)
    {
        string output = GetOutputString();
        Assert.Contains(expected, output);
    }

    private void ThenOutputContainsSeq(int seq)
    {
        string output = GetOutputString();
        Assert.Contains($"\"seq\":{seq}", output);
    }

    private void ThenInvalidOperationExceptionWasThrown() => _ = Assert.IsType<InvalidOperationException>(_thrownException);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore;
    private readonly DapServerService _testee;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private MemoryStream? _inputStream;
    private MemoryStream? _outputStream;
    private DapServerModel? _model;
    private RequestMessage? _readResult;
    private RequestMessage? _requestMessage;
    private object? _responseBody;
    private Exception? _thrownException;

    private string GetOutputString() => Encoding.UTF8.GetString(_outputStream!.ToArray());

    public DapServerServiceTests(){
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new(_log, _logStore);
    }

    #endregion
}
