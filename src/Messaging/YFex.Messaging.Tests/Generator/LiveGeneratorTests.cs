using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public async Task Counter_PopulatedAfterActivation_AndFirstFetch()
    {
        var vm = new LiveTestVm(ct => Task.FromResult(42));
        vm.Activate();

        await PollUntilAsync(() => vm.Counter == 42);

        vm.Counter.Should().Be(42);
        vm.Deactivate();
    }

    [Fact]
    public async Task CounterError_IsSet_WhenComputationThrows()
    {
        var ex = new InvalidOperationException("boom");
        var vm = new LiveTestVm(ct => Task.FromException<int>(ex));
        vm.Activate();

        await PollUntilAsync(() => vm.CounterError is not null);

        vm.CounterError.Should().BeSameAs(ex);
        vm.Deactivate();
    }

    // ── RefreshCounterAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshCounterAsync_TriggersAnotherFetch()
    {
        int callCount = 0;
        var vm = new LiveTestVm(ct => { callCount++; return Task.FromResult(callCount); });
        vm.Activate();

        await PollUntilAsync(() => callCount >= 1);
        callCount.Should().Be(1);

        // Fire-and-forget the refresh; wait for the value to update
        _ = vm.RefreshCounterAsync();
        await PollUntilAsync(() => callCount >= 2);

        callCount.Should().Be(2);
        vm.Counter.Should().Be(2);
        vm.Deactivate();
    }

    // ── Deactivation cleanup ───────────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_StopsUpdates()
    {
        int fetchCount = 0;
        var vm = new LiveTestVm(ct => { fetchCount++; return Task.FromResult(fetchCount); });
        vm.Activate();

        await PollUntilAsync(() => fetchCount >= 1);
        vm.Deactivate();

        int countAtDeactivate = fetchCount;
        await Task.Delay(30); // give any racing async to settle

        fetchCount.Should().Be(countAtDeactivate, "no more fetches after deactivate");
        vm.Counter.Should().BeNull("generated field nulled → companion returns null");
    }

    // ── Polling ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollingVm_FetchesMultipleTimes_WhileActive()
    {
        var vm = new PollingLiveVm();
        vm.Activate();

        await PollUntilAsync(() => vm.FetchCount >= 3, timeoutMs: 1000);

        vm.FetchCount.Should().BeGreaterOrEqualTo(3);
        vm.Deactivate();
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private static async Task PollUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException($"Condition not met within {timeoutMs} ms");
            await Task.Delay(2, CancellationToken.None);
        }
    }
}
