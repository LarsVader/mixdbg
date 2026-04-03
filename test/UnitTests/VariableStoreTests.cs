using MixDbg.Engine.DbgEng;
using MixDbg.Models;
using NSubstitute;

namespace MixDbg.Tests;

public sealed class VariableStoreTests
{
    // ── Allocate ─────────────────────────────────────────

    [Fact]
    public void Allocate_WhenCalled_ReturnsPositiveReference()
    {
        WhenAllocating();

        ThenAllocatedRefIsPositive();
    }

    [Fact]
    public void Allocate_WhenCalledTwice_ReturnsDistinctReferences()
    {
        WhenAllocating();
        WhenAllocatingAnother();

        ThenAllocatedRefsAreDistinct();
    }

    // ── Get ──────────────────────────────────────────────

    [Fact]
    public void Get_WhenRefExists_ReturnsContainer()
    {
        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenContainerIsNotNull();
    }

    [Fact]
    public void Get_WhenRefExists_ContainerHasCorrectGroup()
    {
        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenContainerGroupIs(_group);
    }

    [Fact]
    public void Get_WhenRefExists_ContainerHasCorrectStartIndex()
    {
        GivenStartIndex(5);

        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenContainerStartIndexIs(5);
    }

    [Fact]
    public void Get_WhenRefExists_ContainerHasCorrectCount()
    {
        GivenCount(3);

        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenContainerCountIs(3);
    }

    [Fact]
    public void Get_WhenRefDoesNotExist_ReturnsNull()
    {
        WhenGettingNonExistentRef(999);

        ThenContainerIsNull();
    }

    // ── Clear ────────────────────────────────────────────

    [Fact]
    public void Clear_WhenCalled_InvalidatesAllReferences()
    {
        WhenAllocating();
        WhenClearing();
        WhenGettingAllocatedRef();

        ThenContainerIsNull();
    }

    [Fact]
    public void Clear_WhenCalled_ResetsCounter()
    {
        WhenAllocating();
        WhenClearing();
        WhenAllocatingAnother();

        ThenSecondRefEqualsFirstRef();
    }

    #region Given

    private void GivenStartIndex(uint startIndex)
    {
        _startIndex = startIndex;
    }

    private void GivenCount(uint count)
    {
        _count = count;
    }

    #endregion

    #region When

    private void WhenAllocating()
    {
        _allocatedRef = _testee.Allocate(_group, _startIndex, _count);
    }

    private void WhenAllocatingAnother()
    {
        _secondRef = _testee.Allocate(_group2, 0, 1);
    }

    private void WhenGettingAllocatedRef()
    {
        _container = _testee.Get(_allocatedRef);
    }

    private void WhenGettingNonExistentRef(int refId)
    {
        _container = _testee.Get(refId);
    }

    private void WhenClearing()
    {
        _testee.Clear();
    }

    #endregion

    #region Then

    private void ThenAllocatedRefIsPositive()
    {
        Assert.True(_allocatedRef > 0);
    }

    private void ThenAllocatedRefsAreDistinct()
    {
        Assert.NotEqual(_allocatedRef, _secondRef);
    }

    private void ThenContainerIsNotNull()
    {
        Assert.NotNull(_container);
    }

    private void ThenContainerIsNull()
    {
        Assert.Null(_container);
    }

    private void ThenContainerGroupIs(IDebugSymbolGroup2 expected)
    {
        Assert.Same(expected, _container!.Group);
    }

    private void ThenContainerStartIndexIs(uint expected)
    {
        Assert.Equal(expected, _container!.StartIndex);
    }

    private void ThenContainerCountIs(uint expected)
    {
        Assert.Equal(expected, _container!.Count);
    }

    private void ThenSecondRefEqualsFirstRef()
    {
        Assert.Equal(_allocatedRef, _secondRef);
    }

    #endregion

    #region Misc

    private readonly VariableStore _testee = new();
    private readonly IDebugSymbolGroup2 _group = Substitute.For<IDebugSymbolGroup2>();
    private readonly IDebugSymbolGroup2 _group2 = Substitute.For<IDebugSymbolGroup2>();

    private uint _startIndex;
    private uint _count = 1;
    private int _allocatedRef;
    private int _secondRef;
    private VariableContainer? _container;

    #endregion
}
