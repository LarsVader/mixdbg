using MixDbg.Dap;
using MixDbg.Engine;
using MixDbg.Handlers;

// DAP adapters communicate over stdin/stdout.
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var server = new DapServer(stdin, stdout);
var session = new DebugSession(server);
var dispatcher = new DapDispatcher(server);

// Register all handlers
InitializeHandler.Register(dispatcher, session);
LifecycleHandlers.Register(dispatcher, session);
StubHandlers.Register(dispatcher, session);

// Run the message loop until disconnect or EOF
dispatcher.Run();
session.Dispose();
