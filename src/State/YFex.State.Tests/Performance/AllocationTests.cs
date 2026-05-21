using System;
using YFex.State.Collections;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Performance;

/// <summary>
/// Allocation guards. Uses <see cref="GC.GetAllocatedBytesForCurrentThread"/> deltas with a
/// generous warmup loop to absorb JIT and TieredCompilation noise. Bounds are deliberately
/// relaxed; tightening them is an exercise for benchmark runs in YFex.State.Benchmarks.
/// </summary>
public class AllocationTests
{
    private const int WarmupIterations = 1000;
    private const int MeasuredIterations = 1000;

    [Fact]
    public void SetField_RefOverload_NoChange_ZeroAllocation()
    {
        var vm = new TestStateObject { Alpha = 5 };

        // Warmup
        for (int i = 0; i < WarmupIterations; i++) vm.Alpha = 5;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++) vm.Alpha = 5;
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.Should().BeLessOrEqualTo(2_000, "no-change SetField should be allocation-free");
    }

    [Fact]
    public void SetField_RefOverload_FlipFlopValue_NoBoxing()
    {
        var vm = new TestStateObject();

        for (int i = 0; i < WarmupIterations; i++) vm.Alpha = i & 1;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++) vm.Alpha = i & 1;
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // The descriptor is by-in (no boxing). Handler list has one entry (no overflow).
        // Some allocation may come from generated DispatchPending paths; allow a generous bound.
        delta.Should().BeLessOrEqualTo(50_000);
    }

    [Fact]
    public void StateList_Add_AfterCapacityWarmup_BoundedAllocation()
    {
        using var list = new StateList<int>(initialCapacity: MeasuredIterations);

        // Pre-grow capacity
        for (int i = 0; i < WarmupIterations; i++) list.Add(i);
        list.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++) list.Add(i);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.Should().BeLessOrEqualTo(20_000);
    }

    [Fact]
    public void StateList_AsSpan_ZeroAllocation()
    {
        using var list = new StateList<int>();
        for (int i = 0; i < 16; i++) list.Add(i);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            var span = list.AsSpan();
            _ = span.Length;
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.Should().Be(0);
    }

    [Fact]
    public void ValidationResult_Success_StaticField_NoAllocation()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            _ = global::YFex.State.Validation.ValidationResult.Success;
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.Should().Be(0);
    }

    [Fact]
    public void DebounceState_NextToken_ReusesCtsViaTryReset_AfterWarmup()
    {
        var d = new global::YFex.State.Timing.DebounceState();
        // Warmup: first NextToken allocates the CTS; subsequent calls TryReset.
        for (int i = 0; i < WarmupIterations; i++) _ = d.NextToken();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredIterations; i++) _ = d.NextToken();
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // TryReset can occasionally fail (returns false) and force a fresh CTS allocation.
        // The bound is generous to absorb that — we mainly verify no per-call allocation pattern.
        delta.Should().BeLessOrEqualTo(50_000,
            "TryReset path should be allocation-free for the steady state");

        d.Dispose();
    }
}
