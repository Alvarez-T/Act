using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YFex.State;
using YFex.State.Notification;
using YFex.State.Testing;

// ── Helper ────────────────────────────────────────────────────────────────────

static void Pass(string name) => Console.WriteLine($"  [PASS] {name}");

// ── Original smoke tests ───────────────────────────────────────────────────────

Console.WriteLine("=== Original tests ===");
{
    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    vm.FirstName = "Alice";
    vm.LastName = "Smith";

    recorder.AssertChanged(nameof(PersonViewModel.FirstName));
    recorder.AssertChanged(nameof(PersonViewModel.LastName));
    recorder.AssertChangedInOrder(nameof(PersonViewModel.FirstName), nameof(PersonViewModel.LastName));
    Pass("basic change notification");

    int countBefore = recorder.Events.Count;
    vm.FirstName = "Alice";
    if (recorder.Events.Count != countBefore) throw new Exception("same-value suppression fired");
    Pass("same-value suppression");

    recorder.Clear();
    using (vm.BeginUpdate())
    {
        vm.FirstName = "Bob";
        vm.LastName = "Jones";
        if (recorder.Events.Count != 0) throw new Exception("events fired inside BeginUpdate");
    }
    if (recorder.Events.Count != 2) throw new Exception($"expected 2 on flush, got {recorder.Events.Count}");
    Pass("batch update");
}

// ── Scenario 1: PropertyChanging fires before PropertyChanged ─────────────────

Console.WriteLine("\n=== Scenario 1: PropertyChanging order ===");
{
    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    vm.FirstName = "Charlie";

    recorder.AssertChangingThenChanged(nameof(PersonViewModel.FirstName));
    Pass("PropertyChanging fires before PropertyChanged");
}

// ── Scenario 2: PropertyChanging is NOT batched ───────────────────────────────

Console.WriteLine("\n=== Scenario 2: PropertyChanging not batched ===");
{
    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    using (vm.BeginUpdate())
    {
        vm.FirstName = "D1";
        vm.LastName = "D2";
        vm.Age = 42;

        if (recorder.ChangingEvents.Count != 3)
            throw new Exception($"expected 3 immediate Changing events, got {recorder.ChangingEvents.Count}");
        if (recorder.Events.Count != 0)
            throw new Exception($"expected 0 Changed inside batch, got {recorder.Events.Count}");
    }

    if (recorder.Events.Count != 3)
        throw new Exception($"expected 3 Changed on flush, got {recorder.Events.Count}");

    Pass("PropertyChanging fires immediately inside batch; PropertyChanged deferred");
}

// ── Scenario 3: Custom IEqualityComparer on SetField ─────────────────────────

Console.WriteLine("\n=== Scenario 3: Custom IEqualityComparer in SetField ===");
{
    var vm = new ComparerViewModel();
    using var recorder = new StateRecorder<ComparerViewModel>(vm);

    vm.SetTagManually("HELLO");
    if (recorder.Events.Count != 1) throw new Exception("event not fired on first set");

    recorder.Clear();
    vm.SetTagManually("hello"); // case-insensitive — should suppress
    if (recorder.Events.Count != 0) throw new Exception("event fired despite equal value");

    Pass("custom IEqualityComparer suppresses equal values");
}

// ── Scenario 4: Callback SetField (model relay) ───────────────────────────────

Console.WriteLine("\n=== Scenario 4: Callback SetField ===");
{
    var inner = new PersonModel { Name = "Eve" };
    var vm = new RelayViewModel(inner);
    using var recorder = new StateRecorder<RelayViewModel>(vm);

    vm.SetName("Frank");

    if (inner.Name != "Frank") throw new Exception("inner model not updated");
    recorder.AssertChangingThenChanged(RelayViewModel.NameDescriptor.PropertyName);
    Pass("callback SetField updates model and fires both notifications");
}

// ── Scenario 5: TaskNotifier manual API ──────────────────────────────────────

Console.WriteLine("\n=== Scenario 5: TaskNotifier manual API ===");
{
    var vm = new TaskViewModel();
    using var recorder = new StateRecorder<TaskViewModel>(vm);

    var tcs = new TaskCompletionSource();
    vm.SetLoadOp(tcs.Task);

    recorder.AssertChanged(TaskViewModel.LoadOpDescriptor.PropertyName);
    recorder.AssertChangingCount(TaskViewModel.LoadOpDescriptor.PropertyName, 1);
    int countAfterAssign = recorder.Events.Count;

    tcs.SetResult();
    await Task.Delay(50);

    if (recorder.Events.Count != countAfterAssign + 1)
        throw new Exception($"expected 1 extra Changed after completion, got {recorder.Events.Count - countAfterAssign}");
    recorder.AssertChangingCount(TaskViewModel.LoadOpDescriptor.PropertyName, 1); // still only once

    Pass("TaskNotifier fires PropertyChanged on assignment and on completion; PropertyChanging once");
}

// ── Scenario 6: [NotifyOnTaskCompletion] generated setter ────────────────────

Console.WriteLine("\n=== Scenario 6: [NotifyOnTaskCompletion] codegen ===");
{
    var vm = new AsyncViewModel();
    using var recorder = new StateRecorder<AsyncViewModel>(vm);

    var tcs = new TaskCompletionSource<int>();
    vm.Result = tcs.Task;

    recorder.AssertChanged(nameof(AsyncViewModel.Result));
    int countAfterAssign = recorder.Events.Count;

    tcs.SetResult(42);
    await Task.Delay(50);

    if (recorder.Events.Count != countAfterAssign + 1)
        throw new Exception($"expected 1 extra Changed on completion, got {recorder.Events.Count - countAfterAssign}");

    Pass("[NotifyOnTaskCompletion] re-fires PropertyChanged on task completion");
}

// ── Scenario 7: Coalesced batch Post (off-UI-thread) ─────────────────────────

// Note: construct VM with custom context captured, then clear current context before
// mutating. Same thread (owner assertion passes) but SynchronizationContext.Current
// differs from the captured one, triggering the off-thread coalescing path.
Console.WriteLine("\n=== Scenario 7: Coalesced batch Post ===");
{
    var counter = new PostCounter();
    var trackingContext = new TrackingContext(counter);
    SynchronizationContext.SetSynchronizationContext(trackingContext);

    var vm = new MvvmPersonViewModel(); // captures trackingContext
    counter.Reset();

    // Simulate "not on UI thread" — same thread (owner check passes), different current context
    SynchronizationContext.SetSynchronizationContext(null);

    using (vm.BeginUpdate())
    {
        vm.FirstName = "G1";
        vm.LastName = "G2";
        vm.Age = 7;
    } // flush → OnBatchFlushCompleted → one Post via trackingContext

    if (counter.ChangedPosts != 1)
        throw new Exception($"expected 1 batch Post for PropertyChanged, got {counter.ChangedPosts}");

    Pass("one Post per batch flush when current context differs from captured");
}

// ── Scenario 8: Pre-change is NOT coalesced ───────────────────────────────────

Console.WriteLine("\n=== Scenario 8: Pre-change posts are per-mutation ===");
{
    var counter = new PostCounter();
    var trackingContext = new TrackingContext(counter);
    SynchronizationContext.SetSynchronizationContext(trackingContext);

    var vm = new MvvmPersonViewModel(); // captures trackingContext
    counter.Reset();

    SynchronizationContext.SetSynchronizationContext(null); // trigger off-thread path

    using (vm.BeginUpdate())
    {
        vm.FirstName = "H1";
        vm.LastName = "H2";
        vm.Age = 8;
    }

    if (counter.ChangingPosts != 3)
        throw new Exception($"expected 3 PropertyChanging Posts (one per mutation), got {counter.ChangingPosts}");

    Pass("PropertyChanging Posts 3 times (per-mutation), not coalesced");
}

// ── Scenario 9: Feature switch disables PropertyChanging ─────────────────────

Console.WriteLine("\n=== Scenario 9: Feature switch disables PropertyChanging ===");
{
    // Note: FeatureSwitches caches on first read. Set the switch BEFORE creating the VM
    // and before any other test on this TF has read it.
    // This scenario is run last to avoid poisoning the cache for other tests.
    // In production apps the switch is set once at startup via RuntimeHostConfigurationOption.
    AppContext.SetSwitch("YFex.State.EnableINotifyPropertyChangingSupport", false);

    // Force cache reset (smoke-test only — not part of the public API)
    YFex.State.Internal.FeatureSwitches.ResetForTesting();

    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    vm.FirstName = "Iris";

    recorder.AssertChanged(nameof(PersonViewModel.FirstName));
    if (recorder.ChangingEvents.Count != 0)
        throw new Exception($"PropertyChanging fired despite switch off, count={recorder.ChangingEvents.Count}");

    Pass("feature switch disables PropertyChanging without affecting PropertyChanged");
}

Console.WriteLine("\nAll Phase 1 smoke tests PASSED");

// ── Types under test ──────────────────────────────────────────────────────────

public partial class PersonViewModel : StateObject
{
    [Observable] public partial string FirstName { get; set; }
    [Observable] public partial string LastName { get; set; }
    [Observable] public partial int Age { get; set; }
    [Observable] public partial string FullName { get; }
}

public partial class MvvmPersonViewModel : YFex.State.Mvvm.MvvmStateObject
{
    [Observable] public partial string FirstName { get; set; }
    [Observable] public partial string LastName { get; set; }
    [Observable] public partial int Age { get; set; }
}

public partial class AsyncViewModel : StateObject
{
    [Observable, NotifyOnTaskCompletion] public partial Task<int>? Result { get; set; }
}

public partial class TaskViewModel : StateObject
{
    private TaskNotifier? _loadOp;
    public static readonly ChangedNotification LoadOpDescriptor =
        new() { PropertyName = "LoadOpDescriptor", PropertyId = 99u };

    public void SetLoadOp(Task? value)
        => SetFieldAndNotifyOnCompletion(ref _loadOp, value, in LoadOpDescriptor);
}

public partial class ComparerViewModel : StateObject
{
    private string _tag = string.Empty;
    private static readonly ChangedNotification TagDescriptor =
        new() { PropertyName = "Tag", PropertyId = 0u };

    public void SetTagManually(string value)
        => SetField(_tag, value, StringComparer.OrdinalIgnoreCase, v => _tag = v, in TagDescriptor);
}

public class PersonModel { public string Name { get; set; } = ""; }

public partial class RelayViewModel : StateObject
{
    private readonly PersonModel _model;
    public static readonly ChangedNotification NameDescriptor =
        new() { PropertyName = "NameDescriptor", PropertyId = 0u };

    public RelayViewModel(PersonModel model) => _model = model;

    public void SetName(string value)
        => SetField(_model.Name, value, _model, static (m, v) => m.Name = v, in NameDescriptor);
}

// ── SynchronizationContext helpers for Scenarios 7 & 8 ───────────────────────

public sealed class PostCounter
{
    public int ChangedPosts;
    public int ChangingPosts;
    public void Reset() { ChangedPosts = 0; ChangingPosts = 0; }
}

public sealed class TrackingContext : SynchronizationContext
{
    private readonly PostCounter _counter;
    public TrackingContext(PostCounter counter) => _counter = counter;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (state is (YFex.State.Mvvm.MvvmStateObject, System.ComponentModel.PropertyChangedEventArgs[]))
            Interlocked.Increment(ref _counter.ChangedPosts);
        else if (state is (YFex.State.Mvvm.MvvmStateObject, System.ComponentModel.PropertyChangingEventArgs))
            Interlocked.Increment(ref _counter.ChangingPosts);
        // Run callbacks inline so the test can await Task.Delay and see results
        d(state);
    }
}
