using Microsoft.Extensions.DependencyInjection;
using MixDbg.Engine.Sos;
using MixDbg.Services;

namespace MixDbg;

/// <summary>
/// Registers engine wrapper services (DbgEng, ICorDebug, PDB mapper).
/// </summary>
public static class EngineWrappersServiceCollectionExtensions
{
    /// <summary>
    /// Adds all engine wrapper services to the DI container.
    /// </summary>
    public static IServiceCollection AddEngineWrappers(this IServiceCollection services)
    {
        services.AddSingleton<IDbgEngWrapper, DbgEngWrapperService>();
        services.AddSingleton<ICorDebugWrapper, CorDebugWrapperService>();
        services.AddSingleton<IPdbSourceMapper, PdbSourceMapperService>();
        return services;
    }
}
