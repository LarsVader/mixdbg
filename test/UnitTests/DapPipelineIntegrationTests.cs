using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Execution;
using MixDbg.Models.DapMessages.Initialize;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Lifecycle;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Models.DapMessages.Threads;
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
        ThenNativeDebuggerStartEngineThreadWasCalled();
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
        MemoryStream ms = new();
        foreach (string json in requests)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            ms.Write(header);
            ms.Write(body);
        }
        ms.Position = 0;
        _inputStream = ms;
    }

    private void GivenSourceFileIsNative(string path) => _ = _sourceFiles.IsNativeFile(path).Returns(true);

    private void GivenSourceFileIsManaged(string path) => _ = _sourceFiles.IsNativeFile(path).Returns(false);

    private void GivenNativeDebuggerReturnsBreakpoints() => _ = _engine.SetBreakpointsOnEngine(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<SourceBreakpoint[]>())
            .Returns(ci =>
            {
                SourceBreakpoint[] bps = ci.ArgAt<SourceBreakpoint[]>(2);
                return [.. bps.Select((bp, i) => new Breakpoint
                {
                    Id = i + 1,
                    Verified = true,
                    Line = bp.Line,
                })];
            });

    private void GivenNativeDebuggerReturnsFrames(int count) => _ = _engine.GetStackTraceOnEngine(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns([.. Enumerable.Range(1, count).Select(i => new StackFrame
            {
                Id = i,
                Name = $"func_{i}",
                Line = i * 10,
            })]);

    private void GivenNativeDebuggerReturnsThreads(int count) => _ = _engine.GetThreadsOnEngine(Arg.Any<NativeDebuggerModel>())
            .Returns([.. Enumerable.Range(1, count).Select(i => new DapThread
            {
                Id = i,
                Name = $"Thread {i}",
            })]);

    #endregion

    #region When

    private void WhenRunningPipeline()
    {
        _outputStream = new MemoryStream();

        ServiceCollection services = new();

        // Real services
        _ = services.AddSingleton<ILoggingService, LoggingService>();
        _ = services.AddSingleton<IDapServer, DapServerService>();
        _ = services.AddSingleton<IDapDispatcher, DapDispatcherService>();
        // Mocked services
        _ = services.AddSingleton<INativeDebugger>(_engine);
        _ = services.AddSingleton(Substitute.For<IManagedDebugger>());
        _ = services.AddSingleton(_sourceFiles);

        // Models
        _ = services.AddSingleton(sp =>
            sp.GetRequiredService<ILoggingService>().CreateStore());
        _ = services.AddSingleton(sp =>
            sp.GetRequiredService<IDapServer>().CreateModel(_inputStream!, _outputStream));
        _ = services.AddSingleton(new DebugSessionModel());

        // Register all handler services via assembly scanning
        typeof(IDapHandlerService).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDapHandlerService).IsAssignableFrom(t))
            .ToList()
            .ForEach(t => services.AddSingleton(typeof(IDapHandlerService), t));

        ServiceProvider provider = services.BuildServiceProvider();

        IDapDispatcher dispatcher = provider.GetRequiredService<IDapDispatcher>();
        DebugSessionModel sessionModel = provider.GetRequiredService<DebugSessionModel>();

        dispatcher.Run();

        // Drain and execute any queued engine commands (normally consumed by the engine thread).
        if (sessionModel.Engine is NativeDebuggerModel engineModel)
        {
            engineModel.Commands.CompleteAdding();
            foreach (Action command in engineModel.Commands.GetConsumingEnumerable())
                command();
        }

        sessionModel.Dispose();

        ParseOutput();
    }

    #endregion

    #region Then

    private void ThenResponseExistsForCommand(string command, bool success)
    {
        ResponseMessage? resp = _responses.FirstOrDefault(r => r.Command == command);
        Assert.NotNull(resp);
        Assert.Equal(success, resp.Success);
    }

    private void ThenEventWasSent(string eventName) => Assert.Contains(_events, e => e.Event == eventName);

    private void ThenInitializeResponseHasCapabilities()
    {
        Capabilities body = GetResponseBody<Capabilities>("initialize");
        Assert.True(body.SupportsConfigurationDoneRequest);
        Assert.True(body.SupportsTerminateRequest);
    }

    private void ThenNativeDebuggerStartEngineThreadWasCalled() => _engine.Received(1).StartEngineThread(Arg.Any<NativeDebuggerModel>());

    private void ThenNativeDebuggerSetBreakpointsWasCalled(string filePath) => _ = _engine.Received().SetBreakpointsOnEngine(
            Arg.Any<NativeDebuggerModel>(),
            filePath,
            Arg.Any<SourceBreakpoint[]>());

    private void ThenNativeDebuggerContinueWasCalled() => _engine.Received().ExecuteContinueOnEngine(Arg.Any<NativeDebuggerModel>());

    private void ThenNativeDebuggerStepOverWasCalled() => _engine.Received(1).ExecuteStepOnEngine(Arg.Any<NativeDebuggerModel>(), EngineExecutionStatus.StepOver);

    private void ThenNativeDebuggerStepIntoWasCalled() => _engine.Received(1).ExecuteStepOnEngine(Arg.Any<NativeDebuggerModel>(), EngineExecutionStatus.StepInto);

    private void ThenNativeDebuggerStepOutWasCalled() => _engine.Received(1).ExecuteStepOutOnEngine(Arg.Any<NativeDebuggerModel>());

    private void ThenNativeDebuggerTerminateWasCalled() => _engine.Received().Terminate(Arg.Any<NativeDebuggerModel>());

    private void ThenNativeDebuggerDetachWasCalled() => _engine.Received().Detach(Arg.Any<NativeDebuggerModel>());

    private void ThenSetBreakpointsResponseHasCount(int expected)
    {
        SetBreakpointsResponseBody body = GetResponseBody<SetBreakpointsResponseBody>("setBreakpoints");
        Assert.Equal(expected, body.Breakpoints.Length);
    }

    private void ThenSetBreakpointsResponseAllVerified(bool expected)
    {
        SetBreakpointsResponseBody body = GetResponseBody<SetBreakpointsResponseBody>("setBreakpoints");
        Assert.All(body.Breakpoints, bp => Assert.Equal(expected, bp.Verified));
    }

    private void ThenContinueResponseHasAllThreadsContinued()
    {
        ContinueResponseBody body = GetResponseBody<ContinueResponseBody>("continue");
        Assert.True(body.AllThreadsContinued);
    }

    private void ThenStackTraceResponseHasFrameCount(int expected)
    {
        StackTraceResponseBody body = GetResponseBody<StackTraceResponseBody>("stackTrace");
        Assert.Equal(expected, body.StackFrames.Length);
    }

    private void ThenThreadsResponseHasCount(int expected)
    {
        ThreadsResponseBody body = GetResponseBody<ThreadsResponseBody>("threads");
        Assert.Equal(expected, body.Threads.Length);
    }

    private void ThenEvaluateResponseContains(string expected)
    {
        EvaluateResponseBody body = GetResponseBody<EvaluateResponseBody>("evaluate");
        Assert.Contains(expected, body.Result);
    }

    private void ThenAllResponsesAreSuccessful(params string[] commands)
    {
        foreach (string cmd in commands)
        {
            ResponseMessage? resp = _responses.FirstOrDefault(r => r.Command == cmd);
            Assert.NotNull(resp);
            Assert.True(resp.Success, $"Expected success for '{cmd}' but got failure: {resp.Message}");
        }
    }

    private void ThenResponseCountIs(int expected) => Assert.Equal(expected, _responses.Count);

    private void ThenResponseRequestSeqMatches(string command, int expectedRequestSeq)
    {
        ResponseMessage resp = _responses.First(r => r.Command == command);
        Assert.Equal(expectedRequestSeq, resp.RequestSeq);
    }

    private void ThenAllResponseSeqsAreUnique()
    {
        List<int> seqs = [.. _responses.Select(r => r.Seq)
, .. _events.Select(e => e.Seq)];
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    private void ThenRawOutputIsValidDapFraming()
    {
        string raw = Encoding.UTF8.GetString(_outputStream!.ToArray());
        string[] parts = raw.Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            int headerEnd = part.IndexOf("\r\n\r\n");
            Assert.True(headerEnd > 0, "Missing \\r\\n\\r\\n after Content-Length");
            string lengthStr = part[..headerEnd];
            Assert.True(int.TryParse(lengthStr, out int length), $"Invalid Content-Length: {lengthStr}");
            string body = part[(headerEnd + 4)..];
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
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
        ResponseMessage resp = _responses.First(r => r.Command == command);
        JsonElement element = (JsonElement)resp.Body!;
        return element.Deserialize<T>(JsonOpts)!;
    }

    public DapPipelineIntegrationTests()
    {
        _ = _engine.CreateModel().Returns(_ => new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        });
        _engine.When(e => e.StartEngineThread(Arg.Any<NativeDebuggerModel>()))
            .Do(ci =>
            {
                NativeDebuggerModel model = ci.ArgAt<NativeDebuggerModel>(0);
                model.EngineReady.Set();
                // Start a drain thread so QueueEngineQuery calls don't block.
                Thread drainThread = new(() =>
                {
                    try
                    {
                        foreach (Action cmd in model.Commands.GetConsumingEnumerable())
                            cmd();
                    }
                    catch (OperationCanceledException) { }
                })
                { IsBackground = true };
                drainThread.Start();
            });
    }

    private static string MakeRequest(int seq, string command, object? args = null)
    {
        Dictionary<string, object?> obj = new()
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
        string raw = Encoding.UTF8.GetString(_outputStream!.ToArray());

        // Split by Content-Length framing
        MemoryStream stream = new(Encoding.UTF8.GetBytes(raw));
        DapServerService reader = new();
        _ = reader.CreateModel(stream, Stream.Null);

        // Re-parse as generic messages
        string[] parts = raw.Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            int headerEnd = part.IndexOf("\r\n\r\n");
            if (headerEnd < 0) continue;
            string lengthStr = part[..headerEnd];
            if (!int.TryParse(lengthStr, out int length)) continue;
            string bodyStr = part.Substring(headerEnd + 4, length);

            JsonDocument doc = JsonDocument.Parse(bodyStr);
            string? type = doc.RootElement.GetProperty("type").GetString();

            if (type == "response")
            {
                ResponseMessage resp = new()
                {
                    Seq = doc.RootElement.GetProperty("seq").GetInt32(),
                    RequestSeq = doc.RootElement.GetProperty("request_seq").GetInt32(),
                    Success = doc.RootElement.GetProperty("success").GetBoolean(),
                    Command = doc.RootElement.GetProperty("command").GetString() ?? "",
                };
                if (doc.RootElement.TryGetProperty("message", out JsonElement msg))
                    resp.Message = msg.GetString();
                if (doc.RootElement.TryGetProperty("body", out JsonElement body))
                    resp.Body = body.Clone();
                _responses.Add(resp);
            }
            else if (type == "event")
            {
                EventMessage evt = new()
                {
                    Seq = doc.RootElement.GetProperty("seq").GetInt32(),
                    Event = doc.RootElement.GetProperty("event").GetString() ?? "",
                };
                if (doc.RootElement.TryGetProperty("body", out JsonElement body))
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