using System.Text;
using System.Text.Json;
using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Interfaces;
using NSubstitute;

namespace MixDbg.Tests;

public sealed class DapDispatcherServiceTests
{
    [Fact]
    public void Run_WhenHandlerRegistered_InvokesHandlerAndSendsResponse()
    {
        GivenAHandler("doStuff", _ => new TestResponse());
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
    public void Run_WhenVoidHandler_SendsResponseWithNullBody()
    {
        GivenAHandler("launch", _ => null);
        GivenServerReturnsRequests(MakeRequest("launch", 1));

        WhenRunning();

        ThenResponseWasSentWithNullBody("launch");
    }

    #region Given

    private void GivenAHandler(string command, Func<JsonElement?, IDapMessage?> execute)
    {
        var handler = Substitute.For<IDapHandlerService>();
        handler.Command.Returns(command);
        handler.Execute(Arg.Any<JsonElement?>()).Returns(ci => execute(ci.ArgAt<JsonElement?>(0)));
        _handlers.Add(handler);
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

    #endregion

    #region When

    private void WhenRunning()
    {
        var testee = new DapDispatcherService(_handlers, _server, _transport, _log, _logStore);
        testee.Run();
    }

    #endregion

    #region Then

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

    private void ThenResponseWasSentWithNullBody(string command)
    {
        _server.Received(1).SendResponse(
            _transport,
            Arg.Is<RequestMessage>(r => r.Command == command),
            null);
    }

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly List<IDapHandlerService> _handlers = [];

    public DapDispatcherServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
    }

    private static RequestMessage MakeRequest(string command, int seq)
    {
        return new RequestMessage { Seq = seq, Command = command };
    }

    private record TestResponse : IDapMessage;

    #endregion
}
