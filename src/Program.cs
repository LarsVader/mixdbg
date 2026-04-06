using Microsoft.Extensions.DependencyInjection;
using MixDbg;
using MixDbg.Models;
using MixDbg.Services;

// DAP adapters communicate over stdin/stdout.
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

// Optional: --logpath <path> overrides the default ~/mixdbg.log location.
string? logPath = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--logpath")
        logPath = args[i + 1];
}

var services = new ServiceCollection()
    .AddMixDbgCore(stdin, stdout, logPath)
    .BuildServiceProvider();

var dispatcher = services.GetRequiredService<IDapDispatcher>();
var sessionModel = services.GetRequiredService<DebugSessionModel>();

// Run the message loop until disconnect or EOF
dispatcher.Run();
sessionModel.Dispose();
