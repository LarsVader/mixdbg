using MixDbg.Models;

namespace MixDbg.Tests;

public sealed class NativeDebuggerModelTests : IDisposable
{
    [Fact]
    public void QueueEngineQuery_WhenEngineExecutesQuery_ReturnsResult()
    {
        // Start a consumer thread that processes the command queue.
        Thread consumer = new(() =>
        {
            foreach (Action cmd in _model.Commands.GetConsumingEnumerable())
                cmd();
        })
        { IsBackground = true };
        consumer.Start();

        int result = _model.QueueEngineQuery(() => 42);

        Assert.Equal(42, result);
        _model.Commands.CompleteAdding();
        _ = consumer.Join(1000);
    }

    [Fact]
    public void QueueEngineQuery_WhenEngineThrows_PropagatesException()
    {
        Thread consumer = new(() =>
        {
            foreach (Action cmd in _model.Commands.GetConsumingEnumerable())
                cmd();
        })
        { IsBackground = true };
        consumer.Start();

        AggregateException ex = Assert.Throws<AggregateException>(() =>
            _model.QueueEngineQuery<int>(() => throw new InvalidOperationException("engine error")));

        _ = Assert.IsType<InvalidOperationException>(ex.InnerException);
        _model.Commands.CompleteAdding();
        _ = consumer.Join(1000);
    }

    [Fact]
    public void JitMethodMapping_GetNativeAddress_WhenExactMatch_ReturnsCorrectAddress()
    {
        JitMethodMapping mapping = new()
        {
            CodeStart = 0x1000,
            ILToNativeMap = [(0, 0), (10, 20), (25, 50)],
        };

        ulong addr = mapping.GetNativeAddress(10);

        Assert.Equal(0x1000UL + 20, addr);
    }

    [Fact]
    public void JitMethodMapping_GetNativeAddress_WhenNoMatch_ReturnsCodeStart()
    {
        JitMethodMapping mapping = new()
        {
            CodeStart = 0x2000,
            ILToNativeMap = [(10, 20), (25, 50)],
        };

        // IL offset 5 is before any entry, so bestNativeOffset stays 0.
        ulong addr = mapping.GetNativeAddress(5);

        Assert.Equal(0x2000UL, addr);
    }

    [Fact]
    public void JitMethodMapping_GetNativeAddress_WhenBetweenEntries_ReturnsLargestLessOrEqual()
    {
        JitMethodMapping mapping = new()
        {
            CodeStart = 0x3000,
            ILToNativeMap = [(0, 0), (10, 20), (25, 50)],
        };

        // IL offset 15 is between 10 and 25, so the best match is (10, 20).
        ulong addr = mapping.GetNativeAddress(15);

        Assert.Equal(0x3000UL + 20, addr);
    }

    [Fact]
    public void IsTargetStopped_WhenStoppedNotSet_ReturnsFalse() =>
        Assert.False(_model.IsTargetStopped);

    [Fact]
    public void IsTargetStopped_WhenStoppedIsSet_ReturnsTrue()
    {
        _model.Stopped.Set();

        Assert.True(_model.IsTargetStopped);
    }

    [Fact]
    public void Dispose_WhenDisposeActionSet_InvokesIt()
    {
        bool invoked = false;
        _model.DisposeAction = () => invoked = true;

        _model.Dispose();

        Assert.True(invoked);
    }

    [Fact]
    public void Dispose_WhenNoDisposeAction_DoesNotThrow() =>
        _model.Dispose();

    #region Misc

    private readonly NativeDebuggerModel _model = new()
    {
        Wrapper = new DbgEngWrapperModel(),
        CorWrapper = new CorDebugWrapperModel(),
    };

    public void Dispose()
    {
        try { _model.Commands.CompleteAdding(); } catch (ObjectDisposedException) { }
        try { _model.Commands.Dispose(); } catch (ObjectDisposedException) { }
        try { _model.Stopped.Dispose(); } catch (ObjectDisposedException) { }
        try { _model.EngineReady.Dispose(); } catch (ObjectDisposedException) { }
    }

    #endregion
}
