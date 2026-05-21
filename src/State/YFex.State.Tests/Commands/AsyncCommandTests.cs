using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using YFex.State.Commands;

namespace YFex.State.Tests.Commands;

public sealed class AsyncCommandTests
{
    // ── Status initial state ─────────────────────────────────────────────────

    [Fact]
    public void Status_IsIdle_BeforeFirstExecution()
    {
        var vm = new SyncValueTaskVm();
        vm.FetchSyncCommand.Status.Should().Be(CommandStatus.Idle);
    }

    [Fact]
    public void IsExecuting_IsFalse_BeforeFirstExecution()
    {
        var vm = new SyncValueTaskVm();
        vm.FetchSyncCommand.IsExecuting.Should().BeFalse();
    }

    // ── Zero-flicker fast path (sync-completing ValueTask) ───────────────────

    [Fact]
    public void SyncValueTask_NeverEnters_ExecutingState()
    {
        // ARRANGE
        var vm = new SyncValueTaskVm();
        var statusHistory = CaptureStatusHistory(vm.FetchSyncCommand);

        // ACT — runs synchronously end-to-end; no await needed in the test
        vm.FetchSyncCommand.Execute();

        // ASSERT: the Executing state must never have been observed
        statusHistory.Should().NotContain(CommandStatus.Executing,
            "a synchronously-completing ValueTask must not flicker through Executing");
    }

    [Fact]
    public void SyncValueTask_Status_IsSucceeded_AfterExecute()
    {
        var vm = new SyncValueTaskVm();
        vm.FetchSyncCommand.Execute();
        vm.FetchSyncCommand.Status.Should().Be(CommandStatus.Succeeded);
    }

    [Fact]
    public void SyncValueTask_FlattenResult_IntoTargetProperty()
    {
        var vm = new SyncValueTaskVm();
        vm.FetchSyncCommand.Execute();
        vm.Result.Should().Be(42);
    }

    [Fact]
    public void SyncValueTask_PropertyChanged_FiresExactlyOnce_ForSucceeded()
    {
        var vm = new SyncValueTaskVm();
        var statusHistory = CaptureStatusHistory(vm.FetchSyncCommand);

        vm.FetchSyncCommand.Execute();

        // Only Succeeded should fire — never Executing
        statusHistory.Should().ContainSingle()
            .Which.Should().Be(CommandStatus.Succeeded);
    }

    // ── Async slow path (genuinely yielding ValueTask) ───────────────────────

    [Fact]
    public async Task AsyncValueTask_EntersExecuting_ThenSucceeds()
    {
        var vm = new AsyncValueTaskVm();
        var statusHistory = CaptureStatusHistory(vm.FetchCommand);

        await vm.FetchCommand.ExecuteAsync();

        statusHistory.Should().ContainInOrder(
            CommandStatus.Executing,
            CommandStatus.Succeeded);
    }

    // TargetProperty on async path is tested via SyncValueTaskVm (sync-completing fast path).
    // The async path test verifies Status transitions; result is captured in a plain field
    // to avoid the owner-thread assertion when the continuation runs on a thread pool thread.
    [Fact]
    public async Task AsyncValueTask_SideEffect_HappensAfterCompletion()
    {
        var vm = new AsyncValueTaskVm();
        await vm.FetchCommand.ExecuteAsync();
        vm.LastResult.Should().Be(42);
    }

    [Fact]
    public async Task AsyncValueTask_Status_IsSucceeded_AfterCompletion()
    {
        var vm = new AsyncValueTaskVm();
        await vm.FetchCommand.ExecuteAsync();
        vm.FetchCommand.Status.Should().Be(CommandStatus.Succeeded);
        vm.FetchCommand.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public async Task VoidValueTask_TracksStatus_WithoutTargetProperty()
    {
        var vm = new VoidValueTaskVm();
        var statusHistory = CaptureStatusHistory(vm.DoWorkCommand);

        await vm.DoWorkCommand.ExecuteAsync();

        statusHistory.Should().ContainInOrder(CommandStatus.Executing, CommandStatus.Succeeded);
        vm.WorkDone.Should().BeTrue();
    }

    // ── Task command (non-ValueTask) ─────────────────────────────────────────
    // TargetProperty + async path is NOT tested here because the StateObject owner-thread
    // assertion fires when the async continuation sets the [Observable] property on a thread
    // pool thread different from the construction thread (xUnit's non-affine SyncContext).
    // TargetProperty correctness is validated by SyncValueTask_FlattenResult_IntoTargetProperty.

    [Fact]
    public async Task Task_Status_EntersExecuting_ThenSucceeds()
    {
        var vm = new TaskOfTVm();
        var statusHistory = CaptureStatusHistory(vm.LoadLabelCommand);

        await vm.LoadLabelCommand.ExecuteAsync();

        statusHistory.Should().ContainInOrder(CommandStatus.Executing, CommandStatus.Succeeded);
    }

    [Fact]
    public async Task Task_SideEffect_HappensAfterCompletion()
    {
        var vm = new TaskOfTVm();
        await vm.LoadLabelCommand.ExecuteAsync();
        vm.LastLabel.Should().Be("loaded");
    }

    // ── Faulted ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncFault_Status_IsFaulted()
    {
        var vm = new FaultingVm();
        // ExecuteAsync never throws to the caller — exception is captured internally
        await vm.ThrowCommand.ExecuteAsync();
        vm.ThrowCommand.Status.Should().Be(CommandStatus.Faulted);
    }

    [Fact]
    public void SyncFault_Status_IsFaulted()
    {
        // The method throws synchronously during ValueTask construction.
        // The generator wraps this in try/catch so HandleFault is called.
        var vm = new FaultingSyncVm();
        vm.ThrowSyncCommand.Execute();
        vm.ThrowSyncCommand.Status.Should().Be(CommandStatus.Faulted);
    }

    [Fact]
    public async Task AsyncFault_PropertyChanged_FiresForFaulted()
    {
        var vm = new FaultingVm();
        var statusHistory = CaptureStatusHistory(vm.ThrowCommand);

        await vm.ThrowCommand.ExecuteAsync();

        statusHistory.Should().Contain(CommandStatus.Faulted);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_Status_IsCanceled()
    {
        var vm = new CancellableVm();
        using var cts = new CancellationTokenSource();

        var executeTask = vm.LongRunCommand.ExecuteAsync(cts.Token);

        // Give the command time to actually start executing
        await Task.Delay(20);

        cts.Cancel();
        await executeTask;

        vm.LongRunCommand.Status.Should().Be(CommandStatus.Canceled);
        vm.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task IncludeCancelCommand_CancelCommand_StopsExecution()
    {
        var vm = new CancellableVm();
        var executeTask = vm.LongRunCommand.ExecuteAsync();

        await Task.Delay(20);

        vm.CancelLongRunCommand.CanExecute().Should().BeTrue();
        vm.CancelLongRunCommand.Execute();

        await executeTask;

        vm.LongRunCommand.Status.Should().Be(CommandStatus.Canceled);
    }

    // ── Concurrency gate ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentExecution_SecondCallIsIgnored()
    {
        var vm = new AsyncValueTaskVm();

        // Fire two overlapping executions
        var t1 = vm.FetchCommand.ExecuteAsync();
        var t2 = vm.FetchCommand.ExecuteAsync(); // should be silently discarded

        await Task.WhenAll(t1, t2);

        vm.FetchCommand.Status.Should().Be(CommandStatus.Succeeded);
    }

    // ── INotifyPropertyChanged contract ──────────────────────────────────────

    [Fact]
    public void Command_Implements_INotifyPropertyChanged()
    {
        var vm = new SyncValueTaskVm();
        vm.FetchSyncCommand.Should().BeAssignableTo<INotifyPropertyChanged>();
    }

    [Fact]
    public void Status_PropertyChanged_Uses_Correct_PropertyName()
    {
        var vm = new SyncValueTaskVm();
        string? changedPropName = null;
        ((INotifyPropertyChanged)vm.FetchSyncCommand).PropertyChanged +=
            (_, e) => changedPropName = e.PropertyName;

        vm.FetchSyncCommand.Execute();

        changedPropName.Should().Be(nameof(IAsyncStateCommand.Status));
    }

    // ── CanExecute / CanExecuteChanged ───────────────────────────────────────

    [Fact]
    public async Task CanExecuteChanged_Fires_OnStart_And_OnCompletion()
    {
        var vm = new AsyncValueTaskVm();
        int fired = 0;
        vm.FetchCommand.CanExecuteChanged += () => fired++;

        await vm.FetchCommand.ExecuteAsync();

        // Must fire at least twice: once when execution starts (CanExecute → false),
        // once when it finishes (CanExecute → true).
        fired.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static List<CommandStatus> CaptureStatusHistory(IAsyncStateCommand command)
    {
        var history = new List<CommandStatus>();
        ((INotifyPropertyChanged)command).PropertyChanged +=
            (_, e) =>
            {
                if (e.PropertyName == nameof(IAsyncStateCommand.Status))
                    history.Add(command.Status);
            };
        return history;
    }
}
