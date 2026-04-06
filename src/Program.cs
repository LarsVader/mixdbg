using Microsoft.Extensions.DependencyInjection;

using MixDbg;
using MixDbg.Models;
using MixDbg.Services.Interfaces;

// DAP adapters communicate over stdin/stdout.
using Stream stdin = Console.OpenStandardInput();
using Stream stdout = Console.OpenStandardOutput();

// Optional: --logpath <path> overrides the default ~/mixdbg.log location.
string? logPath = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--logpath")
        logPath = args[i + 1];
}

ServiceProvider services = new ServiceCollection()
    .AddMixDbgCore(stdin, stdout, logPath)
    .BuildServiceProvider();

IDapDispatcher dispatcher = services.GetRequiredService<IDapDispatcher>();
DebugSessionModel sessionModel = services.GetRequiredService<DebugSessionModel>();

// Run the message loop until disconnect or EOF
dispatcher.Run();
sessionModel.Dispose();