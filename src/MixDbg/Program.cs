using Microsoft.Extensions.DependencyInjection;
using MixDbg;
using MixDbg.Dap;
using MixDbg.Engine;
using MixDbg.Handlers;

// DAP adapters communicate over stdin/stdout.
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var services = new ServiceCollection()
    .AddMixDbgCore(stdin, stdout)
    .BuildServiceProvider();

var dispatcher = services.GetRequiredService<DapDispatcher>();
var session = services.GetRequiredService<DebugSession>();

// Register all handlers
InitializeHandler.Register(dispatcher, session);
LifecycleHandlers.Register(dispatcher, session);
StubHandlers.Register(dispatcher, session);

// Run the message loop until disconnect or EOF
dispatcher.Run();
session.Dispose();
