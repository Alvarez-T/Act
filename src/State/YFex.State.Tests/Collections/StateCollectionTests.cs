using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using YFex.State.Collections;
using YFex.State.Mvvm.Collections;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Collections;

public class StateCollectionTests
{
    [Fact]
    public void Add_FiresCollectionChangedAsAdd()
    {
        using var list = new StateList<int>();
        using var view = list.ToStateCollection();

        NotifyCollectionChangedAction? action = null;
        view.CollectionChanged += (_, e) => action = e.Action;

        list.Add(42);

        action.Should().Be(NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public void Remove_FiresCollectionChangedAsRemove()
    {
        using var list = new StateList<int>();
        list.Add(1); list.Add(2);
        using var view = list.ToStateCollection();

        NotifyCollectionChangedAction? action = null;
        view.CollectionChanged += (_, e) => action = e.Action;

        list.Remove(1);

        action.Should().Be(NotifyCollectionChangedAction.Remove);
    }

    [Fact]
    public void Clear_FiresCollectionChangedAsReset()
    {
        using var list = new StateList<int>();
        list.Add(1);
        using var view = list.ToStateCollection();

        NotifyCollectionChangedAction? action = null;
        view.CollectionChanged += (_, e) => action = e.Action;

        list.Clear();

        action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void IndexerSet_FiresCollectionChangedAsReplace()
    {
        using var list = new StateList<int>();
        list.Add(1);
        using var view = list.ToStateCollection();

        NotifyCollectionChangedAction? action = null;
        IList? oldItems = null;
        IList? newItems = null;
        int? startingIndex = null;
        view.CollectionChanged += (_, e) =>
        {
            action = e.Action;
            oldItems = e.OldItems;
            newItems = e.NewItems;
            startingIndex = e.NewStartingIndex;
        };

        list[0] = 99;

        action.Should().Be(NotifyCollectionChangedAction.Replace);
        newItems.Should().NotBeNull();
        newItems!.Count.Should().Be(1);
        newItems[0].Should().Be(99);
        oldItems.Should().NotBeNull();
        oldItems!.Count.Should().Be(1);
        oldItems[0].Should().Be(1);
        startingIndex.Should().Be(0);
    }

    [Fact]
    public void IndexerSet_OnReferenceType_PreservesOldReferenceInOldItems()
    {
        using var list = new StateList<string>();
        list.Add("alpha");
        using var view = list.ToStateCollection();

        IList? oldItems = null;
        view.CollectionChanged += (_, e) => oldItems = e.OldItems;

        list[0] = "beta";

        oldItems.Should().NotBeNull();
        oldItems![0].Should().Be("alpha");
    }

    [Fact]
    public void PropertyChanged_FiresForCount()
    {
        using var list = new StateList<int>();
        using var view = list.ToStateCollection();

        string? propName = null;
        ((INotifyPropertyChanged)view).PropertyChanged += (_, e) => propName = e.PropertyName;

        list.Add(1);

        propName.Should().Be(nameof(view.Count));
    }

    [Fact]
    public void IListGeneric_RoutesToUnderlyingList()
    {
        using var list = new StateList<int>();
        using var view = list.ToStateCollection();

        view.Add(10);
        list.Count.Should().Be(1);

        view.Insert().Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Insert_Throws()
    {
        using var list = new StateList<int>();
        using var view = list.ToStateCollection();

        var act = () => view.Insert(0, 1);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void IListNonGeneric_Works()
    {
        using var list = new StateList<int>();
        using var view = list.ToStateCollection();

        ((IList)view).Add(42);
        ((IList)view).Contains(42).Should().BeTrue();
        ((IList)view).IndexOf(42).Should().Be(0);
    }

    [Fact]
    public void Implements_AllExpectedInterfaces()
    {
        using var list = new StateList<int>();
        using var view = list.ToStateCollection();

        view.Should().BeAssignableTo<INotifyCollectionChanged>();
        view.Should().BeAssignableTo<INotifyPropertyChanged>();
        view.Should().BeAssignableTo<IList<int>>();
        view.Should().BeAssignableTo<IList>();
        view.Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void Dispose_UnsubscribesFromSource()
    {
        using var list = new StateList<int>();
        var view = list.ToStateCollection();

        int fired = 0;
        view.CollectionChanged += (_, _) => fired++;

        view.Dispose();
        list.Add(1);

        fired.Should().Be(0);
    }

    [Fact]
    public void Enumerator_YieldsItemsInOrder()
    {
        using var list = new StateList<int>();
        list.Add(1); list.Add(2); list.Add(3);
        using var view = list.ToStateCollection();

        view.ToList().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void CopyTo_CopiesItemsToTargetArray()
    {
        using var list = new StateList<int>();
        list.Add(10); list.Add(20);
        using var view = list.ToStateCollection();

        var arr = new int[3];
        view.CopyTo(arr, 1);

        arr.Should().Equal(0, 10, 20);
    }
}

public static class StateCollectionTestActHelpers
{
    public static Action Insert(this StateCollection<int> view) => () => view.Insert(0, 99);
}
