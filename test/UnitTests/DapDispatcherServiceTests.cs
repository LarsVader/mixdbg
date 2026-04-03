using System.Text;
using System.Text.Json;
using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services;
using NSubstitute;

namespace MixDbg.Tests;

public sealed class DapDispatcherServiceTests
{
    [Fact]
    public void CreateModel_WhenCalled_ReturnsModelWithEmptyHandlers()
    {
        WhenCreatingModel();

        ThenModelHandlersAreEmpty();
    }

    [Fact]
    public void Register_WhenCalled_AddsHandler()
    {
        GivenAHandler("test", _ => null);

        ThenHandlerExistsFor("test");
    }

    [Fact]
    public void Run_WhenHandlerRegistered_InvokesHandlerAndSendsResponse()
    {
        GivenAHandler("doStuff", _ => new { result = 42 });
        GivenServerReturnsRequests(MakeRequest("doStuff", 1));

        WhenRunning();

        ThenResponseWasSentFor("doStuff");
    }

    [Fact]
    public void Run_WhenUnknownCommand_SendsErrorResponse()
    {
        GivenServerReturnsRequests(MakeRequest("unknown", 1));

        WhenRunning();

        ThenErrorResponseWasSent();
    }

    [Fact]
    public void Run_WhenHandlerThrowsException_SendsErrorResponse()
    {
        GivenAHandler("fail", _ => throw new InvalidOperationException("boom"));
        GivenServerReturnsRequests(MakeRequest("fail", 1));

        WhenRunning();

        ThenErrorResponseWasSentWithMessage("boom");
    }

    [Fact]
    public void Run_WhenDisconnectException_SendsResponseAndStops()
    {
        GivenAHandler("disconnect", _ => throw new DisconnectException());
        GivenServerReturnsRequests(MakeRequest("disconnect", 1));

        WhenRunning();

        ThenResponseWasSentForDisconnect();
    }

    [Fact]
    public void Run_WhenEof_StopsLoop()
    {
        GivenServerReturnsEof();

        WhenRunning();

        ThenNoResponseWasSent();
    }

    [Fact]
    public void RegisterTyped_WhenCalled_DeserializesArguments()
    {
        GivenATypedHandler();
        GivenServerReturnsRequests(MakeRequestWithArgs("launch", 1,
            new LaunchRequestArguments { Program = "test.exe" }));

        WhenRunning();

        ThenTypedHandlerReceivedProgram("test.exe");
    }

    [Fact]
    public void DeserializeArgs_WhenNoArguments_ReturnsDefault()
    {
        GivenARequestWithNoArguments();

        WhenDeserializingArgs();

        ThenDeserializedArgsIsDefault();
    }

    #region Given

    private void GivenAHandler(string command, Func<RequestMessage, object?> handler)
    {
        _testee.Register(_dispatcherModel, command, handler);
    }

    private void GivenATypedHandler()
    {
        _testee.Register<LaunchRequestArguments>(_dispatcherModel, "launch", args =>
        {
            _capturedProgram = args.Program;
            return null;
        });
    }

    private void GivenServerReturnsRequests(params RequestMessage[] requests)
    {
        var queue = new Queue<RequestMessage?>(requests.Cast<RequestMessage?>().Append(null));
        _server.ReadRequest(_transport).Returns(_ => queue.Dequeue());
    }

    private void GivenServerReturnsEof()
    {
        _server.ReadRequest(_transport).Returns((RequestMessage?)null);
    }

    private void GivenARequestWithNoArguments()
    {
        _requestForDeserialize = new RequestMessage { Seq = 1, Command = "test" };
    }

    #endregion

    #region When

    private void WhenCreatingModel()
    {
        _createdModel = _testee.CreateModel();
    }

    private void WhenRunning()
    {
        _testee.Run(_dispatcherModel);
    }

    private void WhenDeserializingArgs()
    {
        _deserializedArgs = DapDispatcherService.DeserializeArgs<LaunchRequestArguments>(_requestForDeserialize!);
    }

    #endregion

    #region Then

    private void ThenModelHandlersAreEmpty()
    {
        Assert.Empty(_createdModel!.Handlers);
    }

    private void ThenHandlerExistsFor(string command)
    {
        Assert.True(_dispatcherModel.Handlers.ContainsKey(command));
    }

    private void ThenResponseWasSentFor(string command)
    {
        _server.Received(1).SendResponse(
            _transport,
            Arg.Is<RequestMessage>(r => r.Command == command),
            Arg.Any<object?>());
    }

    private void ThenErrorResponseWasSent()
    {
        _server.Received(1).SendErrorResponse(
            _transport,
            Arg.Any<RequestMessage>(),
            Arg.Is<string>(s => s.Contains("Unknown command")));
    }

    private void ThenErrorResponseWasSentWithMessage(string message)
    {
        _server.Received(1).SendErrorResponse(
            _transport,
            Arg.Any<RequestMessage>(),
            Arg.Is<string>(s => s.Contains(message)));
    }

    private void ThenResponseWasSentForDisconnect()
    {
        _server.Received(1).SendResponse(
            _transport,
            Arg.Is<RequestMessage>(r => r.Command == "disconnect"),
            Arg.Any<object?>());
    }

    private void ThenNoResponseWasSent()
    {
        _server.DidNotReceive().SendResponse(
            Arg.Any<DapServerModel>(),
            Arg.Any<RequestMessage>(),
            Arg.Any<object?>());
    }

    private void ThenTypedHandlerReceivedProgram(string expected)
    {
        Assert.Equal(expected, _capturedProgram);
    }

    private void ThenDeserializedArgsIsDefault()
    {
        Assert.NotNull(_deserializedArgs);
        Assert.Equal("", _deserializedArgs!.Program);
    }

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly DapDispatcherModel _dispatcherModel;
    private readonly DapDispatcherService _testee;

    private DapDispatcherModel? _createdModel;
    private string? _capturedProgram;
    private RequestMessage? _requestForDeserialize;
    private LaunchRequestArguments? _deserializedArgs;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DapDispatcherServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new DapDispatcherService(_server, _transport, _log, _logStore);
        _dispatcherModel = _testee.CreateModel();
    }

    private static RequestMessage MakeRequest(string command, int seq)
    {
        return new RequestMessage { Seq = seq, Command = command };
    }

    private static RequestMessage MakeRequestWithArgs<T>(string command, int seq, T args)
    {
        var json = JsonSerializer.Serialize(args, JsonOpts);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        return new RequestMessage { Seq = seq, Command = command, Arguments = element };
    }

    #endregion
}
