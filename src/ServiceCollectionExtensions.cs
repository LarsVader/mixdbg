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
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<ISourceFileService, SourceFileService>();
        services.AddSingleton<IDapServer, DapServerService>();
        services.AddSingleton<IDapDispatcher, DapDispatcherService>();
        services.AddSingleton<IDbgEngWrapper, DbgEngWrapperService>();
        services.AddSingleton<INativeDebugger, NativeDebuggerService>();
        services.AddSingleton<IManagedDebugger, ManagedDebuggerService>();
        services.AddSingleton<IProfilerPipeService, ProfilerPipeService>();

        // State models (singletons created by services)
        services.AddSingleton(sp =>
            logPath != null ? new Models.LogStore(logPath)
                            : sp.GetRequiredService<ILoggingService>().CreateStore());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IDapServer>().CreateModel(input, output));
        services.AddSingleton(new Models.DebugSessionModel());

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
