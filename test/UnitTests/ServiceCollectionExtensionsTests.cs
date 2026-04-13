using Microsoft.Extensions.DependencyInjection;

using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Tests;

/// <summary>
/// Tests that the DI composition root correctly registers and resolves services.
/// </summary>
public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    [Fact]
    public void AddMixDbgCore_ResolvesIDapServer()
    {
        IDapServer server = _provider.GetRequiredService<IDapServer>();
        Assert.NotNull(server);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesIDapDispatcher()
    {
        IDapDispatcher dispatcher = _provider.GetRequiredService<IDapDispatcher>();
        Assert.NotNull(dispatcher);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesISourceFileService()
    {
        ISourceFileService sourceFiles = _provider.GetRequiredService<ISourceFileService>();
        Assert.NotNull(sourceFiles);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesDebugSessionModel()
    {
        DebugSessionModel session = _provider.GetRequiredService<DebugSessionModel>();
        Assert.NotNull(session);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesDapServerModel()
    {
        DapServerModel model = _provider.GetRequiredService<DapServerModel>();
        Assert.NotNull(model);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesLogStore()
    {
        LogStore store = _provider.GetRequiredService<LogStore>();
        Assert.NotNull(store);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesIManagedDebugger()
    {
        IManagedDebugger debugger = _provider.GetRequiredService<IManagedDebugger>();
        Assert.NotNull(debugger);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesIProfilerPipeService()
    {
        IProfilerPipeService pipeService = _provider.GetRequiredService<IProfilerPipeService>();
        Assert.NotNull(pipeService);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesHandlerServices()
    {
        IEnumerable<IDapHandlerService> handlers = _provider.GetServices<IDapHandlerService>();
        Assert.NotEmpty(handlers);
    }

    [Fact]
    public void AddMixDbgCore_ResolvesIEngineLifecycleService()
    {
        IEngineLifecycleService lifecycle = _provider.GetRequiredService<IEngineLifecycleService>();
        Assert.NotNull(lifecycle);
    }

    #region Misc

    private readonly ServiceProvider _provider;
    private readonly string _logPath;

    public ServiceCollectionExtensionsTests()
    {
        using MemoryStream input = new();
        using MemoryStream output = new();
        _logPath = Path.Combine(Path.GetTempPath(), $"mixdbg_test_{Guid.NewGuid()}.log");
        _provider = new ServiceCollection()
            .AddMixDbgCore(input, output, _logPath)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.GetRequiredService<LogStore>().Dispose();
        _provider.Dispose();
        try { File.Delete(_logPath); } catch { }
    }

    #endregion
}
