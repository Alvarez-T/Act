using YFex.Persistence.Tests.TestObjects;

namespace YFex.Persistence.Tests;

public sealed class PersistenceServiceTests
{
    private static (PersistenceService service, MemorySnapshotStore store) CreateService()
    {
        var store   = new MemorySnapshotStore();
        var service = new PersistenceService(store);
        PersistenceService.Configure(service);
        return (service, store);
    }

    // ── Register / Unregister ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveSnapshot_CallsCaptureAsync_OnAllProviders()
    {
        var (service, _) = CreateService();
        int captureCount = 0;
        service.Register(new LambdaProvider("a", capture: () => { captureCount++; return [1]; }));
        service.Register(new LambdaProvider("b", capture: () => { captureCount++; return [2]; }));

        await service.SaveSnapshotAsync();

        captureCount.Should().Be(2);
    }

    [Fact]
    public async Task SaveSnapshot_SkipsProvider_WhenCaptureReturnsNull()
    {
        var (service, store) = CreateService();
        service.Register(new LambdaProvider("empty", capture: () => null));

        await service.SaveSnapshotAsync();

        // Nothing written → store has no entry for this discriminator
        var raw = await store.LoadAsync("yfex:snapshot:empty");
        raw.Should().BeNull();
    }

    // ── Save → Restore round-trip ──────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_SaveAndRestore_DeliversSameData()
    {
        var (service, _) = CreateService();
        byte[]? captured = null;
        byte[]? restored = null;

        var provider = new LambdaProvider("rt",
            capture: () => [10, 20, 30],
            restore: data => restored = data.ToArray());
        service.Register(provider);

        await service.SaveSnapshotAsync();
        await service.RestoreSnapshotAsync();

        restored.Should().Equal([10, 20, 30]);
    }

    [Fact]
    public async Task Restore_DoesNotThrow_WhenNoSnapshotExists()
    {
        var (service, _) = CreateService();
        service.Register(new LambdaProvider("x",
            restore: _ => throw new InvalidOperationException("should not be called")));

        var act = async () => await service.RestoreSnapshotAsync();
        await act.Should().NotThrowAsync();
    }

    // ── Unregister ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unregistered_Provider_NotCaptured()
    {
        var (service, store) = CreateService();
        int calls = 0;
        var provider = new LambdaProvider("un", capture: () => { calls++; return [1]; });
        service.Register(provider);
        service.Unregister(provider);

        await service.SaveSnapshotAsync();

        calls.Should().Be(0);
    }

    // ── StateObjectSnapshotProvider integration ────────────────────────────────

    [Fact]
    public async Task StateObjectProvider_FullRoundTrip_ViaService()
    {
        var (service, _) = CreateService();
        var source = new TestPersistableObject { Name = "Carol", Count = 77 };
        service.Register(new StateObjectSnapshotProvider<TestPersistableObject>(source, "carol"));

        await service.SaveSnapshotAsync();

        var target = new TestPersistableObject();
        service.Unregister(service.GetProviders().First()); // swap to target
        service.Register(new StateObjectSnapshotProvider<TestPersistableObject>(target, "carol"));
        await service.RestoreSnapshotAsync();

        target.Name.Should().Be("Carol");
        target.Count.Should().Be(77);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class LambdaProvider(
        string discriminator,
        Func<byte[]?>? capture = null,
        Action<ReadOnlySpan<byte>>? restore = null,
        int version = 1) : ISnapshotProvider
    {
        public string Discriminator => discriminator;
        public int Version          => version;

        public ValueTask<byte[]?> CaptureAsync(CancellationToken ct = default)
            => ValueTask.FromResult(capture?.Invoke());

        public ValueTask RestoreAsync(byte[] data, int storedVersion, CancellationToken ct = default)
        {
            if (storedVersion != Version) return ValueTask.CompletedTask;
            restore?.Invoke(data);
            return ValueTask.CompletedTask;
        }
    }
}

// Extension to expose providers list for test assertion
file static class PersistenceServiceTestExtensions
{
    internal static IEnumerable<ISnapshotProvider> GetProviders(this PersistenceService svc)
    {
        // Access via capture — expose only in tests
        var field = typeof(PersistenceService)
            .GetField("_providers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return ((System.Collections.Generic.List<ISnapshotProvider>)field.GetValue(svc)!).ToArray();
    }
}
