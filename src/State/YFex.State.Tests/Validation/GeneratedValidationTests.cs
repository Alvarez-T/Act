#pragma warning disable CS0618 // exercises the obsolete (Phase 0) validation API on purpose
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using YFex.State.Mvvm;
using YFex.State.Validation;

namespace YFex.State.Tests.Validation;

/// <summary>
/// Emission tests for [ValidateWith] / [ValidateAsync]: the VMs below only compile if the
/// generator emits correct helpers (Phase 0 of the validation design).
/// </summary>
public class GeneratedValidationTests
{
    // ── 1. Sync only ───────────────────────────────────────────────────────

    [Fact]
    public void Sync_InvalidValue_SetsErrorUnderPropertyName()
    {
        var vm = new SyncVm();

        vm.Name = "";

        vm.Validation.HasErrors.Should().BeTrue();
        vm.Validation.GetErrors(nameof(SyncVm.Name)).Should().Equal("required");
    }

    [Fact]
    public void Sync_ValidValueAfterInvalid_ClearsError()
    {
        var vm = new SyncVm();
        vm.Name = "";

        vm.Name = "ok";

        vm.Validation.HasErrors.Should().BeFalse();
        vm.Validation.GetErrors(nameof(SyncVm.Name)).Should().BeEmpty();
    }

    [Fact]
    public void Sync_MultipleValidators_FirstFailureWins()
    {
        var vm = new SyncVm();

        vm.Name = new string('x', 11);

        vm.Validation.GetError(nameof(SyncVm.Name)).Should().Be("too long");
    }

    [Fact]
    public void Sync_OnMvvmObject_ExposesErrorsThroughINotifyDataErrorInfo_WithoutManualWiring()
    {
        var vm = new MvvmSyncVm();
        var indei = (INotifyDataErrorInfo)vm;
        var changed = new List<string?>();
        indei.ErrorsChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Name = "";

        indei.HasErrors.Should().BeTrue();
        indei.GetErrors(nameof(MvvmSyncVm.Name)).Cast<ErrorItem>().Select(e => e.Message).Should().Equal("required");
        changed.Should().Equal(nameof(MvvmSyncVm.Name));
    }

    // ── 2. Async only ──────────────────────────────────────────────────────

    [Fact]
    public void AsyncOnly_ValidatorRuns_AndSetsError()
    {
        using var _ = NoSyncContext();
        var vm = new AsyncOnlyVm();

        vm.Email = "taken@x.com";

        WaitUntil(() => vm.Validation.HasErrors);
        vm.Validation.GetError(nameof(AsyncOnlyVm.Email)).Should().Be("taken");
    }

    [Fact]
    public void AsyncOnly_ValidValue_ClearsPreviousError()
    {
        using var _ = NoSyncContext();
        var vm = new AsyncOnlyVm();
        vm.Email = "taken@x.com";
        WaitUntil(() => vm.Validation.HasErrors);

        vm.Email = "free@x.com";

        WaitUntil(() => !vm.Validation.HasErrors);
    }

    // ── 3. Sync + async ────────────────────────────────────────────────────

    [Fact]
    public void SyncAndAsync_AsyncRunsOnlyWhenSyncPassed()
    {
        using var _ = NoSyncContext();
        var vm = new SyncAndAsyncVm();
        CountingTakenValidator.Owner = vm;

        vm.Code = "";                     // sync fails
        vm.AsyncCalls.Should().Be(0);
        vm.Validation.GetError(nameof(SyncAndAsyncVm.Code)).Should().Be("required");

        vm.Code = "taken";                // sync passes → async runs
        WaitUntil(() => vm.Validation.GetError(nameof(SyncAndAsyncVm.Code)) == "taken");
        vm.AsyncCalls.Should().Be(1);
    }

    [Fact]
    public void SyncAndAsync_SyncFailure_DiscardsInFlightAsyncResult()
    {
        using var _ = NoSyncContext();
        var vm = new GatedVm();
        var gate = Gates.Create("sa-pending");

        vm.Value = "sa-pending";          // sync passes, async blocks on the gate
        vm.Value = "";                    // sync fails → in-flight check invalidated
        gate.SetResult(ValidationResult.Success);   // stale async result would clear the error

        Thread.Sleep(50);
        vm.Validation.GetError(nameof(GatedVm.Value)).Should().Be("required");
    }

    // ── 4. Race: latest value wins ─────────────────────────────────────────

    [Fact]
    public void Race_SlowOlderResult_DoesNotOverwriteNewerResult()
    {
        using var _ = NoSyncContext();
        var vm = new GatedVm();
        var gateA = Gates.Create("race-A");
        var gateB = Gates.Create("race-B");

        vm.Value = "race-A";              // slow
        vm.Value = "race-B";              // fast

        gateB.SetResult(new ValidationResult("B is bad", "Value"));
        WaitUntil(() => vm.Validation.GetError(nameof(GatedVm.Value)) == "B is bad");

        // A ignores cancellation and completes later with a different outcome.
        gateA.SetResult(new ValidationResult("A is bad", "Value"));
        Thread.Sleep(50);

        vm.Validation.GetError(nameof(GatedVm.Value)).Should().Be("B is bad");
    }

    [Fact]
    public void Race_PreviousCheckIsCancelled_WhenValueChanges()
    {
        using var _ = NoSyncContext();
        var vm = new GatedVm();
        var gateA = Gates.Create("cancel-A");
        Gates.Create("cancel-B");

        vm.Value = "cancel-A";
        vm.Value = "cancel-B";

        WaitUntil(() => Gates.CancelledValues.Contains("cancel-A"));
        gateA.Task.IsCompleted.Should().BeFalse("A never completed; it was superseded");
    }

    // ── 5. Throwing validator ──────────────────────────────────────────────

    [Fact]
    public void ThrowingAsyncValidator_DoesNotCrash_AndRoutesToFaultHook()
    {
        using var _ = NoSyncContext();
        var vm = new ThrowingVm();

        vm.Value = "boom";

        WaitUntil(() => vm.Faults.Count == 1);
        vm.Faults[0].Property.Should().Be(nameof(ThrowingVm.Value));
        vm.Faults[0].Exception.Should().BeOfType<InvalidOperationException>();
        vm.Validation.HasErrors.Should().BeFalse("a fault is not a validation error");
    }

    // ── 6. UI-thread marshalling ───────────────────────────────────────────

    [Fact]
    public void Async_ResultIsAppliedOnCapturedSynchronizationContext()
    {
        var ctx = new PumpingSyncContext();
        var old = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        GatedVm vm;
        TaskCompletionSource<ValidationResult> gate;
        try
        {
            vm = new GatedVm();
            gate = Gates.Create("ctx-A");
            vm.Value = "ctx-A";
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(old);
        }

        gate.SetResult(new ValidationResult("bad", "Value"));
        Thread.Sleep(50);

        vm.Validation.HasErrors.Should().BeFalse("the continuation is queued on the captured context");
        ctx.RunPending();
        vm.Validation.GetError(nameof(GatedVm.Value)).Should().Be("bad");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    // Async continuations must not be posted to xunit's max-concurrency context while the
    // test thread blocks in WaitUntil → run those tests with no SynchronizationContext.
    private static Helpers.SyncContextScope NoSyncContext() => new(null);

    // Blocking wait: the test must stay on the thread that created the VM (StateObject
    // asserts owner-thread in DEBUG), so no await between setter calls.
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 2000)
        => SpinWait.SpinUntil(condition, timeoutMs).Should().BeTrue("condition should be met in time");

    /// <summary>Queues posted callbacks until <see cref="RunPending"/> is called.</summary>
    private sealed class PumpingSyncContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunPending()
        {
            var old = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.TryDequeue(out var item)) item.Item1(item.Item2);
            }
            finally
            {
                SetSynchronizationContext(old);
            }
        }
    }
}

// ── Validators ─────────────────────────────────────────────────────────────

public sealed class RequiredValidator : IValidator<string>
{
    public static ValidationResult Validate(string value) =>
        string.IsNullOrEmpty(value) ? new ValidationResult("required", "") : ValidationResult.Success;
}

public sealed class MaxTenValidator : IValidator<string>
{
    public static ValidationResult Validate(string value) =>
        value is { Length: > 10 } ? new ValidationResult("too long", "") : ValidationResult.Success;
}

public sealed class TakenValidator : IAsyncValidator<string>
{
    public static async ValueTask<ValidationResult> ValidateAsync(string value, CancellationToken ct)
    {
        await Task.Yield();
        return value.StartsWith("taken", StringComparison.Ordinal)
            ? new ValidationResult("taken", "")
            : ValidationResult.Success;
    }
}

/// <summary>Completes when the test releases the gate registered for the value; ignores cancellation
/// for the result but records that it was requested.</summary>
public sealed class GatedValidator : IAsyncValidator<string>
{
    public static async ValueTask<ValidationResult> ValidateAsync(string value, CancellationToken ct)
    {
        using var _ = ct.Register(static v => Gates.CancelledValues.Add((string)v!), value);
        return await Gates.Get(value).Task.ConfigureAwait(false);
    }
}

public sealed class ThrowingValidator : IAsyncValidator<string>
{
    public static async ValueTask<ValidationResult> ValidateAsync(string value, CancellationToken ct)
    {
        await Task.Yield();
        throw new InvalidOperationException(value);
    }
}

public static class Gates
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<ValidationResult>> s_gates = new();
    public static readonly ConcurrentBag<string> CancelledValues = new();

    public static TaskCompletionSource<ValidationResult> Create(string value) =>
        s_gates[value] = new TaskCompletionSource<ValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource<ValidationResult> Get(string value) =>
        s_gates.TryGetValue(value, out var g) ? g : Create(value);
}

// ── View models (compile = emission test) ──────────────────────────────────

public partial class SyncVm : StateObject
{
    [Observable, ValidateWith(typeof(RequiredValidator)), ValidateWith(typeof(MaxTenValidator))]
    public partial string Name { get; set; }
}

public partial class MvvmSyncVm : MvvmStateObject
{
    [Observable, ValidateWith(typeof(RequiredValidator))]
    public partial string Name { get; set; }
}

public partial class AsyncOnlyVm : StateObject
{
    [Observable, ValidateAsync(typeof(TakenValidator))]
    public partial string Email { get; set; }
}

public partial class SyncAndAsyncVm : StateObject
{
    public int AsyncCalls;

    [Observable, ValidateWith(typeof(RequiredValidator)), ValidateAsync(typeof(CountingTakenValidator))]
    public partial string Code { get; set; }
}

public sealed class CountingTakenValidator : IAsyncValidator<string>
{
    [ThreadStatic] public static SyncAndAsyncVm? Owner;

    public static ValueTask<ValidationResult> ValidateAsync(string value, CancellationToken ct)
    {
        if (Owner is { } o) Interlocked.Increment(ref o.AsyncCalls);
        return TakenValidator.ValidateAsync(value, ct);
    }
}

public partial class GatedVm : StateObject
{
    [Observable, ValidateWith(typeof(RequiredValidator)), ValidateAsync(typeof(GatedValidator))]
    public partial string Value { get; set; }
}

public partial class ThrowingVm : StateObject
{
    public List<(string Property, Exception Exception)> Faults { get; } = new();

    [Observable, ValidateAsync(typeof(ThrowingValidator))]
    public partial string Value { get; set; }

    protected override void OnAsyncValidationFaulted(string propertyName, Exception exception)
        => Faults.Add((propertyName, exception));
}
