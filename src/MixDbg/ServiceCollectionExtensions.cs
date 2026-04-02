using Microsoft.Extensions.DependencyInjection;
using MixDbg.Dap;
using MixDbg.Engine;
using MixDbg.Services;

namespace MixDbg;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMixDbgCore(this IServiceCollection services,
        Stream input, Stream output)
    {
        // Stateless services
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ISourceFileService, SourceFileService>();

        // State containers (singletons for the session lifetime)
        services.AddSingleton<IDapServer>(sp => new DapServer(input, output));
        services.AddSingleton<DapDispatcher>();
        services.AddSingleton<DebugSession>();

        // Factory for lazy NativeDebugger creation
        services.AddSingleton<Func<NativeDebugger>>(sp => () =>
            new NativeDebugger(
                sp.GetRequiredService<IDapServer>(),
                sp.GetRequiredService<ILogService>(),
                sp.GetRequiredService<ISourceFileService>()));

        return services;
    }
}
