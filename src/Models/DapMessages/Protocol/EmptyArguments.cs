using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

/// <summary>
/// Empty arguments type for DAP commands that take no parameters.
/// </summary>
public record EmptyArguments : IDapMessageArguments;
