using System.Text.RegularExpressions;

namespace MixDbg.Engine.Sos;

/// <summary>
/// Parses text output from SOS debugger extension commands.
/// </summary>
internal static partial class SosOutputParser
{
    /// <summary>
    /// Parses <c>!bpmd</c> output to extract the breakpoint ID assigned by dbgeng.
    /// </summary>
    /// <returns>
    /// A tuple of (success, breakpoint ID if available, diagnostic message).
    /// </returns>
    public static (bool Success, uint? BpId, string? Message) ParseBpmdOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (false, null, "Empty bpmd output");

        // SOS !bpmd outputs lines like:
        //   "Adding pending breakpoints..."
        //   "MethodDesc = 00007FFA12345678"
        //   "Setting breakpoint: bp 3 [00007FFA12345678]"
        //   "Breakpoint 3 set."
        // Or for JITTED methods:
        //   "Found 1 methods in module ..."
        //   "MethodDesc = 00007FFA12345678"
        //   "Setting breakpoint: bp 5 ..."

        // Pattern 1: "Breakpoint <id> set."
        var setMatch = BpSetRegex().Match(output);
        if (setMatch.Success && uint.TryParse(setMatch.Groups[1].Value, out var setId))
            return (true, setId, null);

        // Pattern 2: "Setting breakpoint: bp <id>"
        var settingMatch = BpSettingRegex().Match(output);
        if (settingMatch.Success && uint.TryParse(settingMatch.Groups[1].Value, out var settingId))
            return (true, settingId, null);

        // Pattern 3: Pending breakpoint — no ID yet, but considered success
        if (output.Contains("Adding pending breakpoints", StringComparison.OrdinalIgnoreCase))
            return (true, null, "Pending — will resolve when method is JIT-compiled");

        // Check for known error patterns
        if (output.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return (false, null, output.Trim());

        // Unknown output — treat as potential success (bpmd may have deferred)
        return (true, null, output.Trim());
    }

    [GeneratedRegex(@"Breakpoint\s+(\d+)\s+set", RegexOptions.IgnoreCase)]
    private static partial Regex BpSetRegex();

    [GeneratedRegex(@"Setting breakpoint:\s*bp\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BpSettingRegex();
}
