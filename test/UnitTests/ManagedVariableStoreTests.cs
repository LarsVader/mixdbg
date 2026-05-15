using MixDbg.Models;

namespace MixDbg.Tests;

/// <summary>
/// Tests for <see cref="ManagedVariableStore"/> — allocation, retrieval,
/// clear, offset scheme, and <see cref="ManagedVariableStore.IsManaged"/>.
/// </summary>
public sealed class ManagedVariableStoreTests
{
    // ── Allocate ─────────────────────────────────────────

    [Fact]
    public void Allocate_WhenCalled_ReturnsReferenceAtOrAboveBaseOffset()
    {
        WhenAllocating();

        ThenAllocatedRefIsAtOrAboveBaseOffset();
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
    public void Get_WhenRefExists_ReturnsLocals()
    {
        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenLocalsAreNotNull();
    }

    [Fact]
    public void Get_WhenRefExists_ReturnsSameLocals()
    {
        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenLocalsAreSameAsOriginal();
    }

    [Fact]
    public void Get_WhenRefDoesNotExist_ReturnsNull()
    {
        WhenGettingNonExistentRef(999_999);

        ThenLocalsAreNull();
    }

    // ── Clear ────────────────────────────────────────────

    [Fact]
    public void Clear_WhenCalled_InvalidatesAllReferences()
    {
        WhenAllocating();
        WhenClearing();
        WhenGettingAllocatedRef();

        ThenLocalsAreNull();
    }

    [Fact]
    public void Clear_WhenCalled_ResetsCounter()
    {
        WhenAllocating();
        WhenClearing();
        WhenAllocatingAnother();

        ThenSecondRefEqualsFirstRef();
    }

    // ── IsManaged ────────────────────────────────────────

    [Fact]
    public void IsManaged_WhenRefAtBaseOffset_ReturnsTrue()
    {
        GivenRefToCheck(ManagedVariableStore.BaseOffset);

        WhenCheckingIsManaged();

        ThenIsManagedIsTrue();
    }

    [Fact]
    public void IsManaged_WhenRefAboveBaseOffset_ReturnsTrue()
    {
        GivenRefToCheck(ManagedVariableStore.BaseOffset + 42);

        WhenCheckingIsManaged();

        ThenIsManagedIsTrue();
    }

    [Fact]
    public void IsManaged_WhenRefBelowBaseOffset_ReturnsFalse()
    {
        GivenRefToCheck(1);

        WhenCheckingIsManaged();

        ThenIsManagedIsFalse();
    }

    [Fact]
    public void IsManaged_WhenRefIsZero_ReturnsFalse()
    {
        GivenRefToCheck(0);

        WhenCheckingIsManaged();

        ThenIsManagedIsFalse();
    }

    // ── Allocated refs are always managed ────────────────

    [Fact]
    public void Allocate_WhenCalled_AllocatedRefIsManaged()
    {
        WhenAllocating();

        ThenAllocatedRefIsManaged();
    }

    #region Given

    private void GivenRefToCheck(int refValue) => _refToCheck = refValue;

    #endregion

    #region When

    private void WhenAllocating() => _allocatedRef = _testee.Allocate(_locals);

    private void WhenAllocatingAnother() => _secondRef = _testee.Allocate(_locals2);

    private void WhenGettingAllocatedRef() => _retrievedLocals = _testee.Get(_allocatedRef);

    private void WhenGettingNonExistentRef(int refId) => _retrievedLocals = _testee.Get(refId);

    private void WhenClearing() => _testee.Clear();

    private void WhenCheckingIsManaged() => _isManagedResult = ManagedVariableStore.IsManaged(_refToCheck);

    #endregion

    #region Then

    private void ThenAllocatedRefIsAtOrAboveBaseOffset() => Assert.True(_allocatedRef >= ManagedVariableStore.BaseOffset);

    private void ThenAllocatedRefsAreDistinct() => Assert.NotEqual(_allocatedRef, _secondRef);

    private void ThenLocalsAreNotNull() => Assert.NotNull(_retrievedLocals);

    private void ThenLocalsAreNull() => Assert.Null(_retrievedLocals);

    private void ThenLocalsAreSameAsOriginal() => Assert.Same(_locals, _retrievedLocals);

    private void ThenSecondRefEqualsFirstRef() => Assert.Equal(_allocatedRef, _secondRef);

    private void ThenIsManagedIsTrue() => Assert.True(_isManagedResult);

    private void ThenIsManagedIsFalse() => Assert.False(_isManagedResult);

    private void ThenAllocatedRefIsManaged() => Assert.True(ManagedVariableStore.IsManaged(_allocatedRef));

    #endregion

    #region Misc

    private readonly ManagedVariableStore _testee = new();
    private readonly VariableInfo[] _locals = [new VariableInfo("a", "1", "int", 0)];
    private readonly VariableInfo[] _locals2 = [new VariableInfo("b", "2", "int", 0)];

    private int _allocatedRef;
    private int _secondRef;
    private int _refToCheck;
    private VariableInfo[]? _retrievedLocals;
    private bool _isManagedResult;

    #endregion
}
