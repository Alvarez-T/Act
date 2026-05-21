using System;
using System.Threading;
using System.Threading.Tasks;

namespace YFex.State.Tests.Commands;

// ────────────────────────────────────────────────────────────────────────────
//  ViewModels used by AsyncCommandTests.
//  Each VM is a minimal, focused StateObject exercising one command scenario.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// ValueTask that always completes synchronously — result is pre-cached.
/// The zero-flicker fast path must never set Status = Executing.
/// </summary>
public partial class SyncValueTaskVm : StateObject
{
    [Observable] public partial int Result { get; set; }

    [StateCommand(TargetProperty = nameof(Result))]
    private ValueTask<int> FetchSync(CancellationToken ct) => new(42);
}

/// <summary>
/// ValueTask that always yields at least once (Task.Delay(1)).
/// Tests the slow async path and Status transitions (Executing → Succeeded).
/// TargetProperty is intentionally absent: the xUnit SynchronizationContext does not
/// guarantee that async continuations resume on the same physical thread as construction,
/// so calling the StateObject property setter (which has the owner-thread assertion) from
/// a continuation causes a false failure. TargetProperty assignment is covered by
/// SyncValueTaskVm (which hits the zero-flicker fast path and never leaves the calling thread).
/// </summary>
public partial class AsyncValueTaskVm : StateObject
{
    public int LastResult;

    [StateCommand]
    private async ValueTask FetchAsync(CancellationToken ct)
    {
        await Task.Delay(1, ct);
        LastResult = 42; // plain field, no NotifyChanged → no owner-thread assertion
    }
}

/// <summary>
/// Plain ValueTask (no result) to verify Status tracking without TargetProperty.
/// </summary>
public partial class VoidValueTaskVm : StateObject
{
    public bool WorkDone;

    [StateCommand]
    private async ValueTask DoWorkAsync(CancellationToken ct)
    {
        await Task.Delay(1, ct);
        WorkDone = true;
    }
}

/// <summary>
/// Task&lt;T&gt; command — tests Status transitions and TargetProperty for Task-returning methods.
/// Result stored in a plain field (not [Observable]) to avoid the owner-thread assertion when
/// the continuation resumes on a different xUnit thread-pool thread.
/// </summary>
public partial class TaskOfTVm : StateObject
{
    public string? LastLabel;

    [StateCommand]
    private async Task LoadLabelAsync(CancellationToken ct)
    {
        await Task.Delay(1, ct);
        LastLabel = "loaded";
    }
}

/// <summary>Throws unconditionally — Status should transition to Faulted.</summary>
public partial class FaultingVm : StateObject
{
    [StateCommand]
    private async ValueTask ThrowAsync(CancellationToken ct)
    {
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }
}

/// <summary>
/// Faults synchronously during ValueTask construction (before any await).
/// The generator wraps the initial call in a try/catch, so HandleFault fires.
/// </summary>
public partial class FaultingSyncVm : StateObject
{
    [StateCommand]
    private ValueTask ThrowSync(CancellationToken ct)
        => throw new InvalidOperationException("immediate boom");
}

/// <summary>Supports cancellation via IncludeCancelCommand.</summary>
public partial class CancellableVm : StateObject
{
    public bool Completed;

    [StateCommand(IncludeCancelCommand = true)]
    private async ValueTask LongRunAsync(CancellationToken ct)
    {
        await Task.Delay(10_000, ct);
        Completed = true;
    }
}
