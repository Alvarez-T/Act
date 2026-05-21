using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YFex.State.Mvvm;
using YFex.State.Notification;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Notifications;

public class CastingAndConversionTests
{
    [Fact]
    public void TaskNotifier_Null_ConvertsToNullTask()
    {
        global::YFex.State.TaskNotifier? n = null;
        Task? t = n;
        t.Should().BeNull();
    }

    [Fact]
    public void TaskNotifierGeneric_Null_ConvertsToNullTask()
    {
        global::YFex.State.TaskNotifier<int>? n = null;
        Task<int>? t = n;
        t.Should().BeNull();
    }

    [Fact]
    public void StateObject_Casts_ToINotifyChanged_AndIActivatable()
    {
        var vm = new TestStateObject();
        ((object)vm).Should().BeAssignableTo<INotifyChanged>();
        ((object)vm).Should().BeAssignableTo<IActivatable>();
    }

    [Fact]
    public void MvvmStateObject_Casts_ToAllExpectedInterfaces()
    {
        MvvmStateObject vm = new MvvmPersonVm();
        ((object)vm).Should().BeAssignableTo<System.ComponentModel.INotifyPropertyChanged>();
        ((object)vm).Should().BeAssignableTo<System.ComponentModel.INotifyPropertyChanging>();
        ((object)vm).Should().BeAssignableTo<System.ComponentModel.INotifyDataErrorInfo>();
        ((object)vm).Should().BeAssignableTo<IChangedHandler>();
    }

    [Fact]
    public void IStateCommand_TypedContravariance_CompilesAtRuntime()
    {
        // IStateCommand<in T> means we can assign IStateCommand<object> to IStateCommand<string>.
        global::YFex.State.Commands.IStateCommand<object> baseCmd = new ContraCmd();
        global::YFex.State.Commands.IStateCommand<string> derived = baseCmd;
        derived.Should().NotBeNull();
    }

    private sealed class ContraCmd : global::YFex.State.Commands.IStateCommand<object>
    {
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) { }
        public event Action? CanExecuteChanged;
        private void Raise() => CanExecuteChanged?.Invoke();
    }
}
