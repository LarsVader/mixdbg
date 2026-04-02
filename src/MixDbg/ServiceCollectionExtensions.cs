using Microsoft.Extensions.DependencyInjection;
using MixDbg.Dap;
using MixDbg.Engine;
using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMixDbgCore(this IServiceCollection services,
        Stream input, Stream output)
    {
        // Stateless services
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<ISourceFileService, SourceFileService>();
        services.AddSingleton<IDapServer, DapServerService>();

        // State models (singletons for the session lifetime)
        services.AddSingleton(sp =>
            sp.GetRequiredService<ILoggingService>().CreateStore());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDapServer>().CreateModel(input, output));

        // State containers
        services.AddSingleton<DapDispatcher>();
        services.AddSingleton<DebugSession>();

        // Factory for lazy NativeDebugger creation
        services.AddSingleton<Func<NativeDebugger>>(sp => () =>
            new NativeDebugger(
                sp.GetRequiredService<IDapServer>(),
                sp.GetRequiredService<DapServerModel>(),
                sp.GetRequiredService<ILoggingService>(),
                sp.GetRequiredService<LogStore>(),
                sp.GetRequiredService<ISourceFileService>()));

        return services;
    }
}
