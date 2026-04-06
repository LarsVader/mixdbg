namespace MixDbg.Models;

/// <summary>
/// A raw managed stack frame from ICorDebug, before PDB source resolution.
/// Contains the method token, module path, and IL offset needed for the
/// caller to resolve source locations via <see cref="Services.IPdbSourceMapper"/>.
/// </summary>
public sealed record RawManagedFrame(int MethodToken, string? ModulePath, int ILOffset, string Name);
