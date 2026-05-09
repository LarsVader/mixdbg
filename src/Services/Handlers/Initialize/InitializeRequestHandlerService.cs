using MixDbg.Models;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Initialize;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Initialize;

/// <summary>
/// Handles the DAP initialize handshake, sends the initialized event, and returns capabilities.
/// </summary>
public class InitializeRequestHandlerService(
        IDapServer server,
        DapServerModel transport,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<Capabilities, InitializeRequestArguments>, IDapAfterResponseAction
{
    public const string DapMessage = "initialize";

    public override string Command => DapMessage;

    public override Capabilities ExecuteInternal(InitializeRequestArguments args)
    {
        sessionModel.State = SessionState.Initialized;

        return new Capabilities
        {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = false,
            SupportsEvaluateForHovers = true,
            SupportsTerminateRequest = true,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <c>initialized</c> event MUST be emitted after the initialize response
    /// is on the wire. nvim-dap's <c>event_initialized</c> handler runs
    /// synchronously: with no breakpoints set, <c>set_breakpoints({})</c> calls
    /// <c>on_done</c> immediately, which checks
    /// <c>self.capabilities.supportsConfigurationDoneRequest</c> — but that
    /// field is still nil if the initialize response hasn't been processed
    /// yet. The check fails, <c>configurationDone</c> is never sent, and our
    /// engine sits in <c>ProcessCommandsUntilResume</c> forever.
    /// </remarks>
    public void OnAfterResponse()
        => server.SendEvent(transport, "initialized", new InitializedEventBody());
}