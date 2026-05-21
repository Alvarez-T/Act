namespace YFex.Persistence.Tests.Stores;

public sealed class MemorySnapshotStoreTests
{
    private static MemorySnapshotStore Fresh() => new();

    [Fact]
    public async Task Load_ReturnsNull_ForUnknownKey()
    {
        var store = Fresh();
        var result = await store.LoadAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_ThenLoad_ReturnsSameBytes()
    {
        var store = Fresh();
        byte[] data = [1, 2, 3, 4];

        await store.SaveAsync("key1", data);
        var loaded = await store.LoadAsync("key1");

        loaded.Should().Equal(data);
    }

    [Fact]
    public async Task Save_Overwrites_ExistingEntry()
    {
        var store = Fresh();
        await store.SaveAsync("k", [1]);
        await store.SaveAsync("k", [2, 3]);

        var loaded = await store.LoadAsync("k");
        loaded.Should().Equal([2, 3]);
    }

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        var store = Fresh();
        await store.SaveAsync("k", [9]);
        await store.DeleteAsync("k");

        var result = await store.LoadAsync("k");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Delete_IsNoOp_ForUnknownKey()
    {
        var store = Fresh();
        var act = async () => await store.DeleteAsync("nonexistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        var store = Fresh();
        await store.SaveAsync("a", [1]);
        await store.SaveAsync("b", [2]);

        store.Clear();

        (await store.LoadAsync("a")).Should().BeNull();
        (await store.LoadAsync("b")).Should().BeNull();
    }

    [Fact]
    public async Task MultipleKeys_StoredIndependently()
    {
        var store = Fresh();
        await store.SaveAsync("x", [10]);
        await store.SaveAsync("y", [20]);

        (await store.LoadAsync("x")).Should().Equal([10]);
        (await store.LoadAsync("y")).Should().Equal([20]);
    }
}
