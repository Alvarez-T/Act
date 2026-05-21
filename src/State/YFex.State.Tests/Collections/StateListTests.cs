using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using YFex.State.Collections;
using YFex.State.Notification;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Collections;

public class StateListTests
{
    private sealed class CountingHandler : IChangedHandler
    {
        public List<ChangedNotification> Captured { get; } = new();
        public void OnChanged(object source, in ChangedNotification notification) =>
            Captured.Add(notification);
    }

    [Fact]
    public void EmptyList_HasZeroCount()
    {
        using var list = new StateList<int>();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void Add_AppendsItem_FiresItemsAdded()
    {
        using var list = new StateList<int>();
        var h = new CountingHandler();
        list.Subscribe(h);

        list.Add(42);

        list.Count.Should().Be(1);
        list[0].Should().Be(42);
        h.Captured.Should().ContainSingle();
        var n = h.Captured[0];
        n.Kind.Should().Be(ChangeKind.ItemsAdded);
        n.Index.Should().Be(0);
        n.Count.Should().Be(1);
    }

    [Fact]
    public void AddRange_Empty_DoesNotFire()
    {
        using var list = new StateList<int>();
        var h = new CountingHandler();
        list.Subscribe(h);

        list.AddRange(Array.Empty<int>().AsSpan());

        h.Captured.Should().BeEmpty();
    }

    [Fact]
    public void AddRange_NonEmpty_FiresOneNotificationWithCorrectCount()
    {
        using var list = new StateList<int>();
        var h = new CountingHandler();
        list.Subscribe(h);

        list.AddRange(new[] { 1, 2, 3 }.AsSpan());

        list.Count.Should().Be(3);
        h.Captured.Should().ContainSingle();
        h.Captured[0].Kind.Should().Be(ChangeKind.ItemsAdded);
        h.Captured[0].Count.Should().Be(3);
    }

    [Fact]
    public void Remove_ExistingItem_ReturnsTrue_FiresItemsRemoved()
    {
        using var list = new StateList<int>();
        list.Add(10); list.Add(20); list.Add(30);
        var h = new CountingHandler();
        list.Subscribe(h);

        bool removed = list.Remove(20);

        removed.Should().BeTrue();
        list.Count.Should().Be(2);
        list[0].Should().Be(10);
        list[1].Should().Be(30);
        h.Captured.Should().ContainSingle();
        h.Captured[0].Kind.Should().Be(ChangeKind.ItemsRemoved);
    }

    [Fact]
    public void Remove_MissingItem_ReturnsFalse_NoNotification()
    {
        using var list = new StateList<int>();
        list.Add(1);
        var h = new CountingHandler();
        list.Subscribe(h);

        bool removed = list.Remove(999);

        removed.Should().BeFalse();
        h.Captured.Should().BeEmpty();
    }

    [Fact]
    public void RemoveAt_OutOfRange_Throws()
    {
        using var list = new StateList<int>();

        var act = () => list.RemoveAt(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Clear_ResetsCount_FiresItemsCleared()
    {
        using var list = new StateList<int>();
        list.Add(1); list.Add(2);
        var h = new CountingHandler();
        list.Subscribe(h);

        list.Clear();

        list.Count.Should().Be(0);
        h.Captured.Should().ContainSingle();
        h.Captured[0].Kind.Should().Be(ChangeKind.ItemsCleared);
    }

    [Fact]
    public void Indexer_OutOfRangeGet_Throws()
    {
        using var list = new StateList<int>();
        var act = () => _ = list[0];
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Indexer_Set_FiresItemReplaced()
    {
        using var list = new StateList<int>();
        list.Add(1);
        var h = new CountingHandler();
        list.Subscribe(h);

        list[0] = 99;

        list[0].Should().Be(99);
        h.Captured.Should().ContainSingle();
        h.Captured[0].Kind.Should().Be(ChangeKind.ItemReplaced);
        h.Captured[0].Index.Should().Be(0);
        h.Captured[0].Count.Should().Be(1);
    }

    [Fact]
    public void IndexOf_MissingItem_ReturnsMinusOne()
    {
        using var list = new StateList<int>();
        list.IndexOf(42).Should().Be(-1);
    }

    [Fact]
    public void IndexOf_ExistingItem_ReturnsPosition()
    {
        using var list = new StateList<int>();
        list.Add(10); list.Add(20); list.Add(30);
        list.IndexOf(20).Should().Be(1);
    }

    [Fact]
    public void AsSpan_ReturnsSliceOfSizeCount()
    {
        using var list = new StateList<int>(initialCapacity: 16);
        list.Add(1); list.Add(2);

        var span = list.AsSpan();

        span.Length.Should().Be(2);
        span[0].Should().Be(1);
        span[1].Should().Be(2);
    }

    [Fact]
    public void Enumerator_YieldsItemsInOrder()
    {
        using var list = new StateList<int>();
        list.Add(10); list.Add(20); list.Add(30);

        list.ToList().Should().Equal(10, 20, 30);
    }

    [Fact]
    public void NonGenericEnumerator_Works()
    {
        using var list = new StateList<int>();
        list.Add(1); list.Add(2);

        var collected = new List<int>();
        IEnumerator e = ((IEnumerable)list).GetEnumerator();
        while (e.MoveNext()) collected.Add((int)e.Current!);

        collected.Should().Equal(1, 2);
    }

    [Fact]
    public void GrowsBeyondCapacity_PreservesItems()
    {
        using var list = new StateList<int>(initialCapacity: 4);
        for (int i = 0; i < 100; i++) list.Add(i);

        list.Count.Should().Be(100);
        for (int i = 0; i < 100; i++) list[i].Should().Be(i);
    }

    [Fact]
    public void Subscribe_Dedupes_SubsequentSubscribesAreNoOp()
    {
        using var list = new StateList<int>();
        var h = new CountingHandler();
        list.Subscribe(h);
        list.Subscribe(h);

        list.Add(1);

        h.Captured.Should().HaveCount(1);
    }

    [Fact]
    public void Unsubscribe_RemovesHandler()
    {
        using var list = new StateList<int>();
        var h = new CountingHandler();
        list.Subscribe(h);
        list.Unsubscribe(h);

        list.Add(1);

        h.Captured.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var list = new StateList<int>();
        list.Add(1);

        list.Dispose();
        var act = () => list.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Implements_AllExpectedInterfaces()
    {
        using var list = new StateList<int>();
        list.Should().BeAssignableTo<INotifyChanged>();
        list.Should().BeAssignableTo<IActivatable>();
        list.Should().BeAssignableTo<IDisposable>();
        list.Should().BeAssignableTo<IReadOnlyList<int>>();
    }
}
