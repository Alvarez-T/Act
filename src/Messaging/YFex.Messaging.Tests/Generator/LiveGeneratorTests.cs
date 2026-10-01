using YFex.Messaging.Internal;
using YFex.State.Notification;

namespace YFex.Messaging.Tests.Generator;

/// <summary>
/// Tests for source-generated [Live] property wiring: activation creates the
/// ILiveState, value/loading/error companions work, RefreshXAsync triggers a re-fetch,
/// and deactivation cleans up the state.
/// </summary>
public sealed class LiveGeneratorTests
{
    public LiveGeneratorTests()
    {
        LiveStateProvider.Configure(new DefaultLiveStateFactory());
    }

    // ── Companions before activation ───────────────────────────────────────────

    [Fact]
    public void Counter_IsDefault_BeforeActivation()
    {
        var vm = new LiveTestVm(ct => Task.FromResult(99));
        vm.Counter.Should().BeNull("ILiveState not yet created before Activate()");
    }

    [Fact]
    public void IsCounterLoading_IsFalse_BeforeActivation()
    {
        var vm = new LiveTestVm(ct => Task.FromResult(1));
        vm.IsCounterLoading.Should().BeFalse();
    }

    // ── Value after activation ─────────────────────────────────────────────────
    // StateObject is single-threaded (owner-thread assert in DEBUG), so these tests stay
    // synchronous: every Activate/Deactivate call runs on the thread that created the VM.

    [Fact]
    public void Counter_PopulatedAfterActivation_AndFirstFetch()
    {
        using var scope = new NoSyncContextScope();
        var vm = new LiveTestVm(ct => Task.FromResult(42));
        vm.Activate();

        WaitUntil(() => vm.Counter == 42);

        vm.Counter.Should().Be(42);
        vm.Deactivate();
    }

    [Fact]
    public void CounterError_IsSet_WhenComputationThrows()
    {
        using var scope = new NoSyncContextScope();
        var ex = new InvalidOperationException("boom");
        var vm = new LiveTestVm(ct => Task.FromException<int>(ex));
        vm.Activate();

        WaitUntil(() => vm.CounterError is not null);

        vm.CounterError.Should().BeSameAs(ex);
        vm.Deactivate();
    }

    // ── RefreshCounterAsync ────────────────────────────────────────────────────

    [Fact]
    public void RefreshCounterAsync_TriggersAnotherFetch()
    {
        using var scope = new NoSyncContextScope();
        int callCount = 0;
        var vm = new LiveTestVm(ct => Task.FromResult(Interlocked.Increment(ref callCount)));
        vm.Activate();

        WaitUntil(() => Volatile.Read(ref callCount) >= 1 && vm.Counter == 1);
        callCount.Should().Be(1);

        // Fire-and-forget the refresh; wait for the value to update
        _ = vm.RefreshCounterAsync();
        WaitUntil(() => vm.Counter == 2);

        callCount.Should().Be(2);
        vm.Counter.Should().Be(2);
        vm.Deactivate();
    }

    // ── Deactivation cleanup ───────────────────────────────────────────────────

    [Fact]
    public void Deactivate_StopsUpdates()
    {
        using var scope = new NoSyncContextScope();
        int fetchCount = 0;
        var vm = new LiveTestVm(ct => Task.FromResult(Interlocked.Increment(ref fetchCount)));
        vm.Activate();

        WaitUntil(() => Volatile.Read(ref fetchCount) >= 1);
        vm.Deactivate();

        int countAtDeactivate = Volatile.Read(ref fetchCount);
        Thread.Sleep(30); // give any racing async to settle

        Volatile.Read(ref fetchCount).Should().Be(countAtDeactivate, "no more fetches after deactivate");
        vm.Counter.Should().BeNull("generated field nulled → companion returns null");
    }

    // ── Polling ────────────────────────────────────────────────────────────────

    [Fact]
    public void PollingVm_FetchesMultipleTimes_WhileActive()
    {
        using var scope = new NoSyncContextScope();
        var vm = new PollingLiveVm();
        vm.Activate();

        WaitUntil(() => vm.FetchCount >= 3, timeoutMs: 1000);

        vm.FetchCount.Should().BeGreaterThanOrEqualTo(3);
        vm.Deactivate();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Blocking wait: no await between VM calls, so the test never leaves the owner thread.
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 2000)
        => SpinWait.SpinUntil(condition, timeoutMs).Should().BeTrue($"condition should be met within {timeoutMs} ms");

    /// <summary>
    /// Clears <see cref="SynchronizationContext.Current"/> for the test so the live state's
    /// continuations (e.g. its initial <c>Task.Yield</c>) run on the thread pool instead of
    /// being queued on xunit's max-concurrency context while the test thread blocks.
    /// </summary>
    private sealed class NoSyncContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public NoSyncContextScope() => SynchronizationContext.SetSynchronizationContext(null);

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
