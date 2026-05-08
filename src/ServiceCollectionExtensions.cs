using Microsoft.Extensions.DependencyInjection;

using MixDbg.Services;
using MixDbg.Services.Interfaces;

namespace MixDbg;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMixDbgCore(this IServiceCollection services,
        Stream input, Stream output, string? logPath = null, bool verbose = false)
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
        _ = services.AddSingleton<ISteppingService, SteppingService>();
        _ = services.AddSingleton<IStepResolutionService, StepResolutionService>();
        _ = services.AddSingleton<IManagedBreakpointService, ManagedBreakpointService>();
        _ = services.AddSingleton<IManagedBreakpointResolver, ManagedBreakpointResolverService>();
        _ = services.AddSingleton<IManagedDebugger, ManagedDebuggerService>();
        _ = services.AddSingleton<IProfilerPipeService, ProfilerPipeService>();
        _ = services.AddSingleton<IProfilerAttachIpcService, ProfilerAttachIpcService>();

        // State models
        _ = services.AddSingleton(new Models.VcxprojCache());
        _ = services.AddSingleton(sp =>
        {
            Models.LogStore store = logPath != null
                ? new Models.LogStore(logPath)
                : sp.GetRequiredService<ILoggingService>().CreateStore();
            if (verbose)
                store.MinLevel = Models.LogLevel.Verbose;
            return store;
        });
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