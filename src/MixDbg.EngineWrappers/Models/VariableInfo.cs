namespace MixDbg.Models;

/// <summary>
/// A resolved variable from the debug engine's symbol group.
/// Returned by <see cref="Services.IDbgEngWrapper.GetVariables"/>.
/// </summary>
public readonly record struct VariableInfo(
    string Name,
    string Value,
    string? Type,
    int VariablesReference);