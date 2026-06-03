using System.Diagnostics;
using YFex.Cqrs;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Performance;

/// <summary>
/// Test #40: Hot-path dispatch performance smoke.
/// Target: ~5 Âµs per dispatch including registry lookup. Not a micro-benchmark â€” just
/// a sanity check that no obvious regression has been introduced.
/// </summary>
[Trait("Category", "Performance")]
[Collection("DispatcherTests")]
public sealed class HotPathPerformanceTests
{
    public HotPathPerformanceTests() => TestDispatcherFixture.ClearStore();

    [Fact]
    public async Task Dispatch_100k_Queries_InUnder5Seconds()
    {
        TestDispatcherFixture.Store[1] = new TestItem(1, "Perf");
        var fx = new TestDispatcherFixture();
        var query = new TestAggregate.Queries.GetByIdQuery(1);

        // Warm up the dispatcher (compile delegates, populate dict)
        for (int i = 0; i < 100; i++)
            _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(query);

        const int iterations = 100_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(query);
        sw.Stop();

        var totalMs = sw.ElapsedMilliseconds;
        var perCallUs = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1_000_000 / iterations;

        // Generous bound: 50 Âµs per call (10Ã— the target) to avoid CI flakiness.
        // The important thing is that this runs at all without errors, not the precise Âµs figure.
        perCallUs.Should().BeLessThan(50,
            $"100k dispatches took {totalMs} ms ({perCallUs:F2} Âµs/call); expected < 50 Âµs/call");
    }

    [Fact]
    public void FrozenDictionary_Lookup_IsSubMicrosecond()
    {
        // Isolated test for just the registry lookup â€” no async overhead.
        var fx = new TestDispatcherFixture();
        var type = typeof(TestAggregate.Queries.GetByIdQuery);
        var registry = fx.Registry;

        const int iterations = 1_000_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _ = registry.Queries.TryGetValue(type, out _);
        sw.Stop();

        var perCallNs = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1_000_000_000 / iterations;

        // FrozenDictionary is optimised for Type keys; target is ~5 ns.
        // Bound set to 200 ns to be CI-safe.
        perCallNs.Should().BeLessThan(200,
            $"FrozenDictionary lookup took {perCallNs:F1} ns per call; expected < 200 ns");
    }
}
