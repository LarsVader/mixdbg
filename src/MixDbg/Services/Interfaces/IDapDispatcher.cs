using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

public interface IDapDispatcher
{
    DapDispatcherModel CreateModel();
    void Register(DapDispatcherModel model, string command, Func<RequestMessage, object?> handler);
    void Register<TArgs>(DapDispatcherModel model, string command, Func<TArgs, object?> handler);
    void Run(DapDispatcherModel model);
}
