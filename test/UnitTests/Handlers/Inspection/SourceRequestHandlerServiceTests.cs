using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Handlers.Inspection;

namespace MixDbg.Tests.Handlers.Inspection;

public sealed class SourceRequestHandlerServiceTests
{
    [Fact]
    public void Command_ReturnsSource() =>
        Assert.Equal("source", _testee.Command);

    [Fact]
    public void ExecuteInternal_DoesNotThrow() =>
        _testee.ExecuteInternal(new EmptyArguments());

    [Fact]
    public void Execute_ReturnsNull() =>
        Assert.Null(_testee.Execute(null));

    private readonly SourceRequestHandlerService _testee = new();
}
