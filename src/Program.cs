using Microsoft.Extensions.DependencyInjection;

using MixDbg;
using MixDbg.Models;
using MixDbg.Services.Interfaces;

// DAP adapters communicate over stdin/stdout.
using Stream stdin = Console.OpenStandardInput();
using Stream stdout = Console.OpenStandardOutput();

// Optional: --logpath <path> overrides the default ~/mixdbg.log location.
// Optional: --verbose enables verbose (per-event, per-variable) logging.
string? logPath = null;
bool verbose = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--logpath" && i + 1 < args.Length)
        logPath = args[i + 1];
    if (args[i] == "--verbose")
        verbose = true;
}

ServiceProvider services = new ServiceCollection()
    .AddMixDbgCore(stdin, stdout, logPath, verbose)
    .BuildServiceProvider();

IDapDispatcher dispatcher = services.GetRequiredService<IDapDispatcher>();
DebugSessionModel sessionModel = services.GetRequiredService<DebugSessionModel>();

// Run the message loop until disconnect or EOF
dispatcher.Run();
sessionModel.Dispose();
services.GetRequiredService<LogStore>().Dispose();