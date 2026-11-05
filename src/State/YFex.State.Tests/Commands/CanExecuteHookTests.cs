using System.Threading;
using System.Threading.Tasks;
using YFex.State.Commands;

namespace YFex.State.Tests.Commands;

/// <summary>
/// A view model that implements the generated <c>__OnCanExecute_&lt;Name&gt;</c> hooks by hand, the way YFex.Authorization's
/// generator does for <c>[RequirePermission]</c> (authorization-consolidation ⚑Z19).
/// </summary>
public partial class GatedCommandsVm : StateObject
{
    public bool Allowed = true;
    public int AsyncRuns;
    public int SyncRuns;

    [StateCommand]
    private async Task SaveAsync(CancellationToken ct) { AsyncRuns++; await Task.CompletedTask; }

    [StateCommand]
    private void Refresh() => SyncRuns++;

    partial void __OnCanExecute_Save(ref bool canExecute) => canExecute &= Allowed;

    partial void __OnCanExecute_Refresh(ref bool canExecute) => canExecute &= Allowed;

    /// <summary>What the gate's change notification calls.</summary>
    public void GateChanged()
    {
        __RaiseCanExecuteChanged_Save();
        __RaiseCanExecuteChanged_Refresh();
    }
}

public sealed class CanExecuteHookTests
{
    [Fact]
    public async Task Async_command_asks_the_hook_and_refuses_to_run_when_it_says_no()
    {
        var vm = new GatedCommandsVm { Allowed = false };

        vm.SaveCommand.CanExecute().Should().BeFalse();
        await vm.SaveCommand.ExecuteAsync();
        vm.AsyncRuns.Should().Be(0);
        vm.SaveCommand.Status.Should().Be(CommandStatus.Idle, "a refused command does not change its status");

        vm.Allowed = true;
        vm.SaveCommand.CanExecute().Should().BeTrue();
        await vm.SaveCommand.ExecuteAsync();
        vm.AsyncRuns.Should().Be(1);
    }

    [Fact]
    public void Sync_command_asks_the_hook_and_refuses_to_run_when_it_says_no()
    {
        var vm = new GatedCommandsVm { Allowed = false };

        vm.RefreshCommand.CanExecute().Should().BeFalse();
        vm.RefreshCommand.Execute();
        vm.SyncRuns.Should().Be(0);

        vm.Allowed = true;
        vm.RefreshCommand.CanExecute().Should().BeTrue();
        vm.RefreshCommand.Execute();
        vm.SyncRuns.Should().Be(1);
    }

    [Fact]
    public void Raise_helper_notifies_CanExecuteChanged_of_created_commands()
    {
        var vm = new GatedCommandsVm();
        vm.GateChanged();                                   // commands not created yet: no-op, no throw

        int asyncChanges = 0, syncChanges = 0;
        vm.SaveCommand.CanExecuteChanged += () => asyncChanges++;
        vm.RefreshCommand.CanExecuteChanged += () => syncChanges++;
        vm.GateChanged();

        asyncChanges.Should().Be(1);
        syncChanges.Should().Be(1);
    }
}
