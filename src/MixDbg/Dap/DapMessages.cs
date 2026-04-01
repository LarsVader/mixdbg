using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixDbg.Dap;

// ── Base protocol messages ──────────────────────────────

public abstract record ProtocolMessage
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public record RequestMessage : ProtocolMessage
{
    [JsonPropertyName("type")]
    public override string Type => "request";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

public record ResponseMessage : ProtocolMessage
{
    [JsonPropertyName("type")]
    public override string Type => "response";

    [JsonPropertyName("request_seq")]
    public int RequestSeq { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}

public record EventMessage : ProtocolMessage
{
    [JsonPropertyName("type")]
    public override string Type => "event";

    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}

// ── Initialize ──────────────────────────────────────────

public record InitializeRequestArguments
{
    [JsonPropertyName("clientID")]
    public string? ClientId { get; set; }

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("adapterID")]
    public string? AdapterId { get; set; }

    [JsonPropertyName("linesStartAt1")]
    public bool LinesStartAt1 { get; set; } = true;

    [JsonPropertyName("columnsStartAt1")]
    public bool ColumnsStartAt1 { get; set; } = true;

    [JsonPropertyName("pathFormat")]
    public string? PathFormat { get; set; }
}

public record Capabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")]
    public bool SupportsConfigurationDoneRequest { get; set; }

    [JsonPropertyName("supportsFunctionBreakpoints")]
    public bool SupportsFunctionBreakpoints { get; set; }

    [JsonPropertyName("supportsEvaluateForHovers")]
    public bool SupportsEvaluateForHovers { get; set; }

    [JsonPropertyName("supportsTerminateRequest")]
    public bool SupportsTerminateRequest { get; set; }
}

// ── Launch / Attach ─────────────────────────────────────

public record LaunchRequestArguments
{
    [JsonPropertyName("program")]
    public string Program { get; set; } = "";

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("symbolPath")]
    public string[]? SymbolPath { get; set; }

    [JsonPropertyName("noDebug")]
    public bool NoDebug { get; set; }
}

public record AttachRequestArguments
{
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("program")]
    public string? Program { get; set; }

    [JsonPropertyName("symbolPath")]
    public string[]? SymbolPath { get; set; }
}

// ── Breakpoints ─────────────────────────────────────────

public record SetBreakpointsArguments
{
    [JsonPropertyName("source")]
    public Source Source { get; set; } = new();

    [JsonPropertyName("breakpoints")]
    public SourceBreakpoint[] Breakpoints { get; set; } = [];
}

public record SourceBreakpoint
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }
}

public record Breakpoint
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Source? Source { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

public record SetBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")]
    public Breakpoint[] Breakpoints { get; set; } = [];
}

// ── Execution control ───────────────────────────────────

public record ContinueArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }
}

public record ContinueResponseBody
{
    [JsonPropertyName("allThreadsContinued")]
    public bool AllThreadsContinued { get; set; } = true;
}

public record StepArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("granularity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Granularity { get; set; }
}

// ── Stack / Scopes / Variables ──────────────────────────

public record StackTraceArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("levels")]
    public int Levels { get; set; }
}

public record StackTraceResponseBody
{
    [JsonPropertyName("stackFrames")]
    public StackFrame[] StackFrames { get; set; } = [];

    [JsonPropertyName("totalFrames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TotalFrames { get; set; }
}

public record StackFrame
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Source? Source { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("presentationHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresentationHint { get; set; }
}

public record Source
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }
}

public record ScopesArguments
{
    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }
}

public record ScopesResponseBody
{
    [JsonPropertyName("scopes")]
    public Scope[] Scopes { get; set; } = [];
}

public record Scope
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }

    [JsonPropertyName("expensive")]
    public bool Expensive { get; set; }
}

public record VariablesArguments
{
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}

public record VariablesResponseBody
{
    [JsonPropertyName("variables")]
    public Variable[] Variables { get; set; } = [];
}

public record Variable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}

// ── Threads ─────────────────────────────────────────────

public record ThreadsResponseBody
{
    [JsonPropertyName("threads")]
    public DapThread[] Threads { get; set; } = [];
}

public record DapThread
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

// ── Evaluate ────────────────────────────────────────────

public record EvaluateArguments
{
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("frameId")]
    public int? FrameId { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }
}

public record EvaluateResponseBody
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}

// ── Disconnect ──────────────────────────────────────────

public record DisconnectArguments
{
    [JsonPropertyName("restart")]
    public bool Restart { get; set; }

    [JsonPropertyName("terminateDebuggee")]
    public bool? TerminateDebuggee { get; set; }
}

// ── Event bodies ────────────────────────────────────────

public record StoppedEventBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("allThreadsStopped")]
    public bool AllThreadsStopped { get; set; } = true;
}

public record OutputEventBody
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "console";

    [JsonPropertyName("output")]
    public string Output { get; set; } = "";
}

public record TerminatedEventBody;

public record InitializedEventBody;
