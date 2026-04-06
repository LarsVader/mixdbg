using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Interfaces;
using NSubstitute;

namespace MixDbg.Tests;

public sealed class DapPipelineIntegrationTests : IDisposable
{
    [Fact]
    public void Initialize_WhenSent_ReturnsCapabilitiesAndInitializedEvent()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments
            {
                ClientId = "neovim",
                AdapterId = "mixdbg",
            }),
            MakeRequest(2, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("initialize", success: true);
        ThenEventWasSent("initialized");
        ThenInitializeResponseHasCapabilities();
    }

    [Fact]
    public void Launch_WhenSentAfterInitialize_DelegatestoNativeDebugger()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
                Cwd = @"C:\app",
            }),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("launch", success: true);
        ThenNativeDebuggerLaunchWasCalled();
    }

    [Fact]
    public void SetBreakpoints_WhenSentBeforeLaunch_ReturnsPendingBreakpoints()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "setBreakpoints", new SetBreakpointsArguments
            {
                Source = new Source { Path = @"C:\src\main.cpp" },
                Breakpoints = [new SourceBreakpoint { Line = 10 }, new SourceBreakpoint { Line = 25 }],
            }),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("setBreakpoints", success: true);
        ThenSetBreakpointsResponseHasCount(2);
        ThenSetBreakpointsResponseAllVerified(true);
    }

    [Fact]
    public void SetBreakpoints_WhenManagedFile_ReturnsOptimisticallyVerified()
    {
        GivenSourceFileIsManaged(@"C:\src\Program.cs");
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "setBreakpoints", new SetBreakpointsArguments
            {
                Source = new Source { Path = @"C:\src\Program.cs" },
                Breakpoints = [new SourceBreakpoint { Line = 5 }],
            }),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("setBreakpoints", success: true);
        ThenSetBreakpointsResponseAllVerified(true);
    }

    [Fact]
    public void SetBreakpoints_WhenEngineRunning_DelegatesToNativeDebugger()
    {
        GivenNativeDebuggerReturnsBreakpoints();
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "setBreakpoints", new SetBreakpointsArguments
            {
                Source = new Source { Path = @"C:\src\main.cpp" },
                Breakpoints = [new SourceBreakpoint { Line = 42 }],
            }),
            MakeRequest(4, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenNativeDebuggerSetBreakpointsWasCalled(@"C:\src\main.cpp");
    }

    [Fact]
    public void ConfigurationDone_WhenPendingBreakpoints_AppliesAndContinues()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenNativeDebuggerReturnsBreakpoints();
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "setBreakpoints", new SetBreakpointsArguments
            {
                Source = new Source { Path = @"C:\src\main.cpp" },
                Breakpoints = [new SourceBreakpoint { Line = 10 }],
            }),
            MakeRequest(3, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(4, "configurationDone"),
            MakeRequest(5, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("configurationDone", success: true);
        ThenNativeDebuggerSetBreakpointsWasCalled(@"C:\src\main.cpp");
        ThenNativeDebuggerContinueWasCalled();
        ThenEventWasSent("breakpoint");
    }

    [Fact]
    public void Continue_WhenEngineRunning_DelegatesToNativeDebugger()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "continue", new ContinueArguments { ThreadId = 1 }),
            MakeRequest(4, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("continue", success: true);
        ThenContinueResponseHasAllThreadsContinued();
        ThenNativeDebuggerContinueWasCalled();
    }

    [Fact]
    public void StepCommands_WhenEngineRunning_DelegateToNativeDebugger()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "next", new StepArguments { ThreadId = 1 }),
            MakeRequest(4, "stepIn", new StepArguments { ThreadId = 1 }),
            MakeRequest(5, "stepOut", new StepArguments { ThreadId = 1 }),
            MakeRequest(6, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("next", success: true);
        ThenResponseExistsForCommand("stepIn", success: true);
        ThenResponseExistsForCommand("stepOut", success: true);
        ThenNativeDebuggerStepOverWasCalled();
        ThenNativeDebuggerStepIntoWasCalled();
        ThenNativeDebuggerStepOutWasCalled();
    }

    [Fact]
    public void StackTrace_WhenEngineRunning_ReturnsFrames()
    {
        GivenNativeDebuggerReturnsFrames(3);
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "stackTrace", new StackTraceArguments
            {
                ThreadId = 1,
                Levels = 20,
            }),
            MakeRequest(4, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("stackTrace", success: true);
        ThenStackTraceResponseHasFrameCount(3);
    }

    [Fact]
    public void Threads_WhenEngineRunning_ReturnsThreads()
    {
        GivenNativeDebuggerReturnsThreads(2);
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "threads"),
            MakeRequest(4, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("threads", success: true);
        ThenThreadsResponseHasCount(2);
    }

    [Fact]
    public void Threads_WhenNoEngine_ReturnsDefaultThread()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "threads"),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenThreadsResponseHasCount(1);
    }

    [Fact]
    public void Evaluate_WhenSent_ReturnsNotImplemented()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "evaluate", new EvaluateArguments
            {
                Expression = "myVar",
            }),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("evaluate", success: true);
        ThenEvaluateResponseContains("myVar");
    }

    [Fact]
    public void Disconnect_WhenTerminateTrue_TerminatesEngine()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "disconnect", new DisconnectArguments
            {
                TerminateDebuggee = true,
            }));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("disconnect", success: true);
        ThenNativeDebuggerTerminateWasCalled();
    }

    [Fact]
    public void Disconnect_WhenTerminateFalse_DetachesEngine()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "disconnect", new DisconnectArguments
            {
                TerminateDebuggee = false,
            }));

        WhenRunningPipeline();

        ThenNativeDebuggerDetachWasCalled();
    }

    [Fact]
    public void Terminate_WhenSent_TerminatesAndDisconnects()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
            }),
            MakeRequest(3, "terminate"));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("terminate", success: true);
        ThenNativeDebuggerTerminateWasCalled();
    }

    [Fact]
    public void UnknownCommand_WhenSent_ReturnsError()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "bogusCommand"),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("bogusCommand", success: false);
    }

    [Fact]
    public void SilentCommands_WhenSent_ReturnSuccessWithNullBody()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "setFunctionBreakpoints"),
            MakeRequest(3, "setExceptionBreakpoints"),
            MakeRequest(4, "loadedSources"),
            MakeRequest(5, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseExistsForCommand("setFunctionBreakpoints", success: true);
        ThenResponseExistsForCommand("setExceptionBreakpoints", success: true);
        ThenResponseExistsForCommand("loadedSources", success: true);
    }

    [Fact]
    public void FullSession_WhenTypicalSequence_AllResponsesSucceed()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenNativeDebuggerReturnsBreakpoints();
        GivenNativeDebuggerReturnsFrames(2);
        GivenNativeDebuggerReturnsThreads(3);
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments
            {
                ClientId = "neovim",
                AdapterId = "mixdbg",
            }),
            MakeRequest(2, "launch", new LaunchRequestArguments
            {
                Program = @"C:\app\test.exe",
                Cwd = @"C:\app",
            }),
            MakeRequest(3, "setBreakpoints", new SetBreakpointsArguments
            {
                Source = new Source { Path = @"C:\src\main.cpp" },
                Breakpoints = [new SourceBreakpoint { Line = 10 }],
            }),
            MakeRequest(4, "configurationDone"),
            MakeRequest(5, "threads"),
            MakeRequest(6, "stackTrace", new StackTraceArguments { ThreadId = 1, Levels = 20 }),
            MakeRequest(7, "continue", new ContinueArguments { ThreadId = 1 }),
            MakeRequest(8, "next", new StepArguments { ThreadId = 1 }),
            MakeRequest(9, "disconnect", new DisconnectArguments { TerminateDebuggee = true }));

        WhenRunningPipeline();

        ThenAllResponsesAreSuccessful("initialize", "launch", "setBreakpoints",
            "configurationDone", "threads", "stackTrace", "continue", "next", "disconnect");
        ThenResponseCountIs(9);
        ThenEventWasSent("initialized");
    }

    [Fact]
    public void Responses_WhenSent_HaveCorrectSeqAndRequestSeq()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "threads"),
            MakeRequest(3, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenResponseRequestSeqMatches("initialize", 1);
        ThenResponseRequestSeqMatches("threads", 2);
        ThenResponseRequestSeqMatches("disconnect", 3);
        ThenAllResponseSeqsAreUnique();
    }

    [Fact]
    public void Responses_WhenSent_AreContentLengthFramedJson()
    {
        GivenDapRequests(
            MakeRequest(1, "initialize", new InitializeRequestArguments()),
            MakeRequest(2, "disconnect", new DisconnectArguments()));

        WhenRunningPipeline();

        ThenRawOutputIsValidDapFraming();
    }

    #region Given

    private void GivenDapRequests(params string[] requests)
    {
        var ms = new MemoryStream();
        foreach (var json in requests)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            ms.Write(header);
            ms.Write(body);
        }
        ms.Position = 0;
        _inputStream = ms;
    }

    private void GivenSourceFileIsNative(string path)
    {
        _sourceFiles.IsNativeFile(path).Returns(true);
    }

    private void GivenSourceFileIsManaged(string path)
    {
        _sourceFiles.IsNativeFile(path).Returns(false);
    }

    private void GivenNativeDebuggerReturnsBreakpoints()
    {
        _engine.SetBreakpoints(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<SourceBreakpoint[]>())
            .Returns(ci =>
            {
                var bps = ci.ArgAt<SourceBreakpoint[]>(2);
                return bps.Select((bp, i) => new Breakpoint
                {
                    Id = i + 1,
                    Verified = true,
                    Line = bp.Line,
                }).ToArray();
            });
    }

    private void GivenNativeDebuggerReturnsFrames(int count)
    {
        _engine.GetStackTrace(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns(Enumerable.Range(1, count).Select(i => new StackFrame
            {
                Id = i,
                Name = $"func_{i}",
                Line = i * 10,
            }).ToArray());
    }

    private void GivenNativeDebuggerReturnsThreads(int count)
    {
        _engine.GetThreads(Arg.Any<NativeDebuggerModel>())
            .Returns(Enumerable.Range(1, count).Select(i => new DapThread
            {
                Id = i,
                Name = $"Thread {i}",
            }).ToArray());
    }

    #endregion

    #region When

    private void WhenRunningPipeline()
    {
        _outputStream = new MemoryStream();

        var services = new ServiceCollection();

        // Real services
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<IDapServer, DapServerService>();
        services.AddSingleton<IDapDispatcher, DapDispatcherService>();
        services.AddSingleton<IDebugSession, DebugSessionService>();

        // Mocked services
        services.AddSingleton<INativeDebugger>(_engine);
        services.AddSingleton(Substitute.For<IManagedDebugger>());
        services.AddSingleton(_sourceFiles);

        // Models
        services.AddSingleton(sp =>
            sp.GetRequiredService<ILoggingService>().CreateStore());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDapServer>().CreateModel(_inputStream!, _outputStream));
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDebugSession>().CreateModel());

        // Register all handler services via assembly scanning
        typeof(IDapHandlerService).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDapHandlerService).IsAssignableFrom(t))
            .ToList()
            .ForEach(t => services.AddSingleton(typeof(IDapHandlerService), t));

        var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IDapDispatcher>();
        var sessionModel = provider.GetRequiredService<DebugSessionModel>();

        dispatcher.Run();
        sessionModel.Dispose();

        ParseOutput();
    }

    #endregion

    #region Then

    private void ThenResponseExistsForCommand(string command, bool success)
    {
        var resp = _responses.FirstOrDefault(r => r.Command == command);
        Assert.NotNull(resp);
        Assert.Equal(success, resp.Success);
    }

    private void ThenEventWasSent(string eventName)
    {
        Assert.Contains(_events, e => e.Event == eventName);
    }

    private void ThenInitializeResponseHasCapabilities()
    {
        var body = GetResponseBody<Capabilities>("initialize");
        Assert.True(body.SupportsConfigurationDoneRequest);
        Assert.True(body.SupportsTerminateRequest);
    }

    private void ThenNativeDebuggerLaunchWasCalled()
    {
        _engine.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string[]?>());
    }

    private void ThenNativeDebuggerSetBreakpointsWasCalled(string filePath)
    {
        _engine.Received().SetBreakpoints(
            Arg.Any<NativeDebuggerModel>(),
            filePath,
            Arg.Any<SourceBreakpoint[]>());
    }

    private void ThenNativeDebuggerContinueWasCalled()
    {
        _engine.Received().Continue(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerStepOverWasCalled()
    {
        _engine.Received(1).StepOver(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerStepIntoWasCalled()
    {
        _engine.Received(1).StepInto(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerStepOutWasCalled()
    {
        _engine.Received(1).StepOut(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerTerminateWasCalled()
    {
        _engine.Received().Terminate(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerDetachWasCalled()
    {
        _engine.Received().Detach(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenSetBreakpointsResponseHasCount(int expected)
    {
        var body = GetResponseBody<SetBreakpointsResponseBody>("setBreakpoints");
        Assert.Equal(expected, body.Breakpoints.Length);
    }

    private void ThenSetBreakpointsResponseAllVerified(bool expected)
    {
        var body = GetResponseBody<SetBreakpointsResponseBody>("setBreakpoints");
        Assert.All(body.Breakpoints, bp => Assert.Equal(expected, bp.Verified));
    }

    private void ThenContinueResponseHasAllThreadsContinued()
    {
        var body = GetResponseBody<ContinueResponseBody>("continue");
        Assert.True(body.AllThreadsContinued);
    }

    private void ThenStackTraceResponseHasFrameCount(int expected)
    {
        var body = GetResponseBody<StackTraceResponseBody>("stackTrace");
        Assert.Equal(expected, body.StackFrames.Length);
    }

    private void ThenThreadsResponseHasCount(int expected)
    {
        var body = GetResponseBody<ThreadsResponseBody>("threads");
        Assert.Equal(expected, body.Threads.Length);
    }

    private void ThenEvaluateResponseContains(string expected)
    {
        var body = GetResponseBody<EvaluateResponseBody>("evaluate");
        Assert.Contains(expected, body.Result);
    }

    private void ThenAllResponsesAreSuccessful(params string[] commands)
    {
        foreach (var cmd in commands)
        {
            var resp = _responses.FirstOrDefault(r => r.Command == cmd);
            Assert.NotNull(resp);
            Assert.True(resp.Success, $"Expected success for '{cmd}' but got failure: {resp.Message}");
        }
    }

    private void ThenResponseCountIs(int expected)
    {
        Assert.Equal(expected, _responses.Count);
    }

    private void ThenResponseRequestSeqMatches(string command, int expectedRequestSeq)
    {
        var resp = _responses.First(r => r.Command == command);
        Assert.Equal(expectedRequestSeq, resp.RequestSeq);
    }

    private void ThenAllResponseSeqsAreUnique()
    {
        var seqs = _responses.Select(r => r.Seq)
            .Concat(_events.Select(e => e.Seq))
            .ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    private void ThenRawOutputIsValidDapFraming()
    {
        var raw = Encoding.UTF8.GetString(_outputStream!.ToArray());
        var parts = raw.Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var headerEnd = part.IndexOf("\r\n\r\n");
            Assert.True(headerEnd > 0, "Missing \\r\\n\\r\\n after Content-Length");
            var lengthStr = part[..headerEnd];
            Assert.True(int.TryParse(lengthStr, out var length), $"Invalid Content-Length: {lengthStr}");
            var body = part[(headerEnd + 4)..];
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            Assert.True(bodyBytes.Length >= length,
                $"Body too short: expected {length}, got {bodyBytes.Length}");
        }
    }

    #endregion

    #region Misc

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INativeDebugger _engine = Substitute.For<INativeDebugger>();
    private readonly ISourceFileService _sourceFiles = Substitute.For<ISourceFileService>();
    private MemoryStream? _inputStream;
    private MemoryStream? _outputStream;
    private readonly List<ResponseMessage> _responses = [];
    private readonly List<EventMessage> _events = [];

    private T GetResponseBody<T>(string command)
    {
        var resp = _responses.First(r => r.Command == command);
        var element = (JsonElement)resp.Body!;
        return element.Deserialize<T>(JsonOpts)!;
    }

    public DapPipelineIntegrationTests()
    {
        _engine.CreateModel().Returns(_ => new NativeDebuggerModel());
    }

    private static string MakeRequest(int seq, string command, object? args = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (args != null)
            obj["arguments"] = args;

        return JsonSerializer.Serialize(obj, JsonOpts);
    }

    private void ParseOutput()
    {
        _responses.Clear();
        _events.Clear();
        var raw = Encoding.UTF8.GetString(_outputStream!.ToArray());

        // Split by Content-Length framing
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var reader = new DapServerService();
        var model = reader.CreateModel(stream, Stream.Null);

        // Re-parse as generic messages
        var parts = raw.Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var headerEnd = part.IndexOf("\r\n\r\n");
            if (headerEnd < 0) continue;
            var lengthStr = part[..headerEnd];
            if (!int.TryParse(lengthStr, out var length)) continue;
            var bodyStr = part.Substring(headerEnd + 4, length);

            var doc = JsonDocument.Parse(bodyStr);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "response")
            {
                var resp = new ResponseMessage
                {
                    Seq = doc.RootElement.GetProperty("seq").GetInt32(),
                    RequestSeq = doc.RootElement.GetProperty("request_seq").GetInt32(),
                    Success = doc.RootElement.GetProperty("success").GetBoolean(),
                    Command = doc.RootElement.GetProperty("command").GetString() ?? "",
                };
                if (doc.RootElement.TryGetProperty("message", out var msg))
                    resp.Message = msg.GetString();
                if (doc.RootElement.TryGetProperty("body", out var body))
                    resp.Body = body.Clone();
                _responses.Add(resp);
            }
            else if (type == "event")
            {
                var evt = new EventMessage
                {
                    Seq = doc.RootElement.GetProperty("seq").GetInt32(),
                    Event = doc.RootElement.GetProperty("event").GetString() ?? "",
                };
                if (doc.RootElement.TryGetProperty("body", out var body))
                    evt.Body = body.Clone();
                _events.Add(evt);
            }
        }
    }

    public void Dispose()
    {
        _inputStream?.Dispose();
        _outputStream?.Dispose();
    }

    #endregion
}
