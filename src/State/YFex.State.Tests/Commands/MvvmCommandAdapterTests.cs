using System;
using System.Windows.Input;
using YFex.State.Commands;
using YFex.State.Mvvm;

namespace YFex.State.Tests.Commands;

public class MvvmCommandAdapterTests
{
    [Fact]
    public void NonGeneric_Execute_DelegatesToUnderlyingCommand()
    {
        var underlying = new FakeCommand();
        var adapter = new MvvmCommandAdapter(underlying);

        ((ICommand)adapter).Execute(null);

        underlying.ExecuteCallCount.Should().Be(1);
    }

    [Fact]
    public void NonGeneric_CanExecute_DelegatesToUnderlyingCommand()
    {
        var underlying = new FakeCommand { CanExecuteResult = true };
        var adapter = new MvvmCommandAdapter(underlying);

        ((ICommand)adapter).CanExecute(null).Should().BeTrue();

        underlying.CanExecuteResult = false;
        ((ICommand)adapter).CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void NonGeneric_CanExecuteChanged_FiresWhenUnderlyingFires()
    {
        var underlying = new FakeCommand();
        var adapter = new MvvmCommandAdapter(underlying);
        int fired = 0;
        adapter.CanExecuteChanged += (_, _) => fired++;

        underlying.RaiseCanExecuteChanged();

        fired.Should().Be(1);
    }

    [Fact]
    public void Generic_TypedExecute_DoesNotBoxValueTypes()
    {
        var underlying = new FakeTypedCommand<int>();
        var adapter = new MvvmCommandAdapter<int>(underlying);

        adapter.Execute(42);

        underlying.LastParameter.Should().Be(42);
    }

    [Fact]
    public void Generic_LegacyObjectExecute_UnboxesParameter()
    {
        var underlying = new FakeTypedCommand<int>();
        var adapter = new MvvmCommandAdapter<int>(underlying);

        ((ICommand)adapter).Execute(42); // boxed

        underlying.LastParameter.Should().Be(42);
    }

    [Fact]
    public void Generic_LegacyObjectExecute_WrongType_Throws()
    {
        var underlying = new FakeTypedCommand<int>();
        var adapter = new MvvmCommandAdapter<int>(underlying);

        var act = () => ((ICommand)adapter).Execute("not-an-int");

        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Generic_CanExecuteChanged_PropagatesFromUnderlying()
    {
        var underlying = new FakeTypedCommand<int>();
        var adapter = new MvvmCommandAdapter<int>(underlying);
        int fired = 0;
        adapter.CanExecuteChanged += (_, _) => fired++;

        underlying.RaiseCanExecuteChanged();

        fired.Should().Be(1);
    }

    private sealed class FakeCommand : IStateCommand
    {
        public int ExecuteCallCount;
        public bool CanExecuteResult = true;

        public bool CanExecute() => CanExecuteResult;
        public void Execute() => ExecuteCallCount++;
        public event Action? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke();
    }

    private sealed class FakeTypedCommand<T> : IStateCommand<T>
    {
        public T? LastParameter;
        public bool CanExecute(T parameter) => true;
        public void Execute(T parameter) => LastParameter = parameter;
        public event Action? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke();
    }
}
