using Microsoft.Extensions.DependencyInjection;
using MixDbg;
using MixDbg.Handlers;
using MixDbg.Models;
using MixDbg.Services;

// DAP adapters communicate over stdin/stdout.
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var services = new ServiceCollection()
    .AddMixDbgCore(stdin, stdout)
    .BuildServiceProvider();

var dispatcher = services.GetRequiredService<IDapDispatcher>();
var dispatcherModel = services.GetRequiredService<DapDispatcherModel>();
var session = services.GetRequiredService<IDebugSession>();
var sessionModel = services.GetRequiredService<DebugSessionModel>();

// Register all handlers
InitializeHandler.Register(dispatcher, dispatcherModel, session, sessionModel);
LifecycleHandlers.Register(dispatcher, dispatcherModel, session, sessionModel);
StubHandlers.Register(dispatcher, dispatcherModel, session, sessionModel);

// Run the message loop until disconnect or EOF
dispatcher.Run(dispatcherModel);
sessionModel.Dispose();
