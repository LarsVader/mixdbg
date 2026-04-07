using Microsoft.Extensions.DependencyInjection;

using MixDbg.Services;
using MixDbg.Services.Interfaces;

namespace MixDbg;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMixDbgCore(this IServiceCollection services,
        Stream input, Stream output, string? logPath = null)
    {
        // Stateless services
        _ = services.AddSingleton<ILoggingService, LoggingService>();
        _ = services.AddSingleton<ISourceFileService, SourceFileService>();
        _ = services.AddSingleton<IDapServer, DapServerService>();
        _ = services.AddSingleton<IDapDispatcher, DapDispatcherService>();
        _ = services.AddEngineWrappers();
        _ = services.AddSingleton<IEngineLifecycleService, EngineLifecycleService>();
        _ = services.AddSingleton<IBreakpointService, BreakpointService>();
        _ = services.AddSingleton<IEngineQueryService, EngineQueryService>();
        _ = services.AddSingleton<IManagedDebugger, ManagedDebuggerService>();
        _ = services.AddSingleton<IProfilerPipeService, ProfilerPipeService>();

        // State models (singletons created by services)
        _ = services.AddSingleton(sp =>
            logPath != null ? new Models.LogStore(logPath)
                            : sp.GetRequiredService<ILoggingService>().CreateStore());
        _ = services.AddSingleton(sp =>
            sp.GetRequiredService<IDapServer>().CreateModel(input, output));
        _ = services.AddSingleton(new Models.DebugSessionModel());

        // Register all IDapHandlerService implementations
        typeof(ServiceCollectionExtensions)
            .Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDapHandlerService).IsAssignableFrom(t))
            .ToList()
            .ForEach(serviceType => services.AddSingleton(typeof(IDapHandlerService), serviceType));

        return services;
    }
}