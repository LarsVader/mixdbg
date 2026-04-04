using Microsoft.Extensions.DependencyInjection;
using MixDbg.Services;

namespace MixDbg;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMixDbgCore(this IServiceCollection services,
        Stream input, Stream output, string? logPath = null)
    {
        // Stateless services
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<ISourceFileService, SourceFileService>();
        services.AddSingleton<IDapServer, DapServerService>();
        services.AddSingleton<IDapDispatcher, DapDispatcherService>();
        services.AddSingleton<IDebugSession, DebugSessionService>();
        services.AddSingleton<INativeDebugger, NativeDebuggerService>();
        services.AddSingleton<IManagedDebugger, ManagedDebuggerService>();

        // State models (singletons created by services)
        services.AddSingleton(sp =>
            logPath != null ? new Models.LogStore(logPath)
                            : sp.GetRequiredService<ILoggingService>().CreateStore());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDapServer>().CreateModel(input, output));
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDapDispatcher>().CreateModel());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDebugSession>().CreateModel());

        return services;
    }
}
