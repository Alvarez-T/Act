using System.Threading;
using System.Threading.Tasks;
using YFex.Messaging.Internal;

namespace YFex.Messaging.Tests.LiveState;

public sealed class DefaultLiveStateTests
{
    private static DefaultLiveStateFactory Factory => new();

    // ── Initial state ──────────────────────────────────────────────────────────

    [Fact]
    public void Value_IsDefault_BeforeFirstFetch()
    {
        // Use a never-completing task so the first fetch cannot race past this assertion.
        // The CTS cancellation in Dispose() will cancel the delay and clean up.
        using var state = Factory.Create<int>(ct => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => 0));
        state.Value.Should().Be(default, "first fetch hasn't run yet");
    }

    [Fact]
    public void Error_IsNull_Initially()
    {
        using var state = Factory.Create<int>(ct => Task.FromResult(1));
        state.Error.Should().BeNull();
    }

    // ── After first fetch ──────────────────────────────────────────────────────

    [Fact]
    public async Task Value_PopulatedAfterFirstFetch()
    {
        using var state = Factory.Create<int>(ct => Task.FromResult(99));

        await PollUntilAsync(() => state.Value == 99);

        state.Value.Should().Be(99);
        state.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task IsLoading_FalseAfterFetch()
    {
        using var state = Factory.Create<int>(ct => Task.FromResult(1));

        await PollUntilAsync(() => !state.IsLoading && state.Value == 1);

        state.IsLoading.Should().BeFalse();
    }

    // ── Error handling ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Error_IsSet_WhenComputationThrows()
    {
        var ex = new InvalidOperationException("fail");
        using var state = Factory.Create<int>(ct => Task.FromException<int>(ex));

        await PollUntilAsync(() => state.Error is not null);

        state.Error.Should().BeSameAs(ex);
        state.Value.Should().Be(0, "value unchanged when error occurs");
    }

    // ── RecomputeAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RecomputeAsync_TriggersAnotherFetch()
    {
        int callCount = 0;
        using var state = Factory.Create<int>(ct =>
        {
            callCount++;
            return Task.FromResult(callCount);
        });

        // Wait for first automatic fetch
        await PollUntilAsync(() => callCount == 1);
        callCount.Should().Be(1);
        state.Value.Should().Be(1);

        // Trigger refresh — RecomputeAsync runs computation again
        await state.RecomputeAsync();
        await PollUntilAsync(() => callCount == 2);

        callCount.Should().Be(2);
        state.Value.Should().Be(2);
    }

    // ── Dispose ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_StopsUpdates()
    {
        int callCount = 0;
        var state = Factory.Create<int>(ct => { callCount++; return Task.FromResult(callCount); });

        // Wait for initial fetch
        await PollUntilAsync(() => callCount == 1);
        state.Dispose();

        await state.RecomputeAsync();          // disposed → no-op
        await Task.Delay(30);                  // give any spurious update a chance

        callCount.Should().Be(1, "no fetch after dispose");
    }

    // ── Polling ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Poll_FetchesMultipleTimes()
    {
        int callCount = 0;
        using var state = Factory.Create<int>(
            ct => { callCount++; return Task.FromResult(callCount); },
            new LiveStateOptions { PollMs = 20 });

        await PollUntilAsync(() => callCount >= 3, timeoutMs: 1000);

        callCount.Should().BeGreaterOrEqualTo(3);
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private static async Task PollUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException($"Condition '{condition}' not met within {timeoutMs} ms");
            await Task.Delay(2, CancellationToken.None);
        }
    }
}
