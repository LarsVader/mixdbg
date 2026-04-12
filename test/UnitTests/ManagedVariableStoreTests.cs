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
    public void Get_WhenRefExists_ReturnsEntry()
    {
        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenEntryIsNotNull();
    }

    [Fact]
    public void Get_WhenRefExists_ReturnsSameEntry()
    {
        WhenAllocating();
        WhenGettingAllocatedRef();

        ThenEntryIsSameAsOriginal();
    }

    [Fact]
    public void Get_WhenRefDoesNotExist_ReturnsNull()
    {
        WhenGettingNonExistentRef(999_999);

        ThenEntryIsNull();
    }

    // ── Clear ────────────────────────────────────────────

    [Fact]
    public void Clear_WhenCalled_InvalidatesAllReferences()
    {
        WhenAllocating();
        WhenClearing();
        WhenGettingAllocatedRef();

        ThenEntryIsNull();
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

    private void WhenAllocating() => _allocatedRef = _testee.Allocate(_entry);

    private void WhenAllocatingAnother() => _secondRef = _testee.Allocate(_entry2);

    private void WhenGettingAllocatedRef() => _retrievedEntry = _testee.Get(_allocatedRef);

    private void WhenGettingNonExistentRef(int refId) => _retrievedEntry = _testee.Get(refId);

    private void WhenClearing() => _testee.Clear();

    private void WhenCheckingIsManaged() => _isManagedResult = ManagedVariableStore.IsManaged(_refToCheck);

    #endregion

    #region Then

    private void ThenAllocatedRefIsAtOrAboveBaseOffset() => Assert.True(_allocatedRef >= ManagedVariableStore.BaseOffset);

    private void ThenAllocatedRefsAreDistinct() => Assert.NotEqual(_allocatedRef, _secondRef);

    private void ThenEntryIsNotNull() => Assert.NotNull(_retrievedEntry);

    private void ThenEntryIsNull() => Assert.Null(_retrievedEntry);

    private void ThenEntryIsSameAsOriginal() => Assert.Same(_entry, _retrievedEntry);

    private void ThenSecondRefEqualsFirstRef() => Assert.Equal(_allocatedRef, _secondRef);

    private void ThenIsManagedIsTrue() => Assert.True(_isManagedResult);

    private void ThenIsManagedIsFalse() => Assert.False(_isManagedResult);

    private void ThenAllocatedRefIsManaged() => Assert.True(ManagedVariableStore.IsManaged(_allocatedRef));

    #endregion

    #region Misc

    private readonly ManagedVariableStore _testee = new();
    private readonly ManagedVariableEntry _entry = new() { ArrayCount = 3 };
    private readonly ManagedVariableEntry _entry2 = new() { ArrayCount = 5 };

    private int _allocatedRef;
    private int _secondRef;
    private int _refToCheck;
    private ManagedVariableEntry? _retrievedEntry;
    private bool _isManagedResult;

    #endregion
}
