using YFex.Persistence.Tests.TestObjects;

namespace YFex.Persistence.Tests.Providers;

public sealed class StateObjectSnapshotProviderTests
{
    private static StateObjectSnapshotProvider<T> Provider<T>(T obj, string key = "test", int version = 1)
        where T : IPersistableStateObject
        => new(obj, key, version);

    // ── CaptureAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_ReturnsBytes_ForNonDefaultState()
    {
        var obj = new TestPersistableObject { Name = "Alice", Count = 7 };
        var provider = Provider(obj);

        var data = await provider.CaptureAsync();

        data.Should().NotBeNull().And.NotBeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_ReturnsNull_WhenSnapshotIsEmpty()
    {
        // An object whose CaptureSnapshot() produces 0 bytes should map to null
        var obj = new EmptyPersistable();
        var provider = new StateObjectSnapshotProvider<EmptyPersistable>(obj, "empty");

        var data = await provider.CaptureAsync();

        // EmptyPersistable.CaptureSnapshot() returns []; provider maps to null
        data.Should().BeNull();
    }

    // ── RestoreAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_RoundTrip_PreservesValues()
    {
        var source = new TestPersistableObject { Name = "Bob", Count = 42 };
        var provider = Provider(source);

        var data = await provider.CaptureAsync();

        var target = new TestPersistableObject();
        var restoreProvider = Provider(target);
        await restoreProvider.RestoreAsync(data!, storedVersion: 1);

        target.Name.Should().Be("Bob");
        target.Count.Should().Be(42);
    }

    [Fact]
    public async Task RestoreAsync_SkipsRestore_WhenVersionMismatch()
    {
        var source = new TestPersistableObject { Name = "Eve", Count = 5 };
        var captureProvider = Provider(source, version: 1);
        var data = await captureProvider.CaptureAsync();

        var target = new TestPersistableObject { Name = "Original", Count = 0 };
        var restoreProvider = Provider(target, version: 2); // mismatch: stored=1 vs provider=2
        await restoreProvider.RestoreAsync(data!, storedVersion: 1);

        target.Name.Should().Be("Original", "version mismatch skips restore");
    }

    [Fact]
    public async Task Discriminator_And_Version_MatchConstructorArgs()
    {
        var obj = new TestPersistableObject();
        var provider = new StateObjectSnapshotProvider<TestPersistableObject>(obj, "my-key", version: 3);

        provider.Discriminator.Should().Be("my-key");
        provider.Version.Should().Be(3);
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private sealed class EmptyPersistable : IPersistableStateObject
    {
        public byte[] CaptureSnapshot() => [];
        public void RestoreSnapshot(ReadOnlySpan<byte> data) { }
    }
}
