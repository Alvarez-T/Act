using YFex.Persistence.FileSystem;

namespace YFex.Persistence.Tests.Stores;

public sealed class FileSystemSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"YFex_Test_{Guid.NewGuid():N}");
    private FileSystemSnapshotStore Store => new(_dir);

    [Fact]
    public async Task Load_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = await Store.LoadAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_CreatesDirectory_AndFile()
    {
        byte[] data = [42, 43];
        await Store.SaveAsync("snap", data);

        Directory.Exists(_dir).Should().BeTrue();
        var loaded = await Store.LoadAsync("snap");
        loaded.Should().Equal(data);
    }

    [Fact]
    public async Task Save_Overwrites_ExistingFile()
    {
        await Store.SaveAsync("k", [1]);
        await Store.SaveAsync("k", [2, 3]);

        var loaded = await Store.LoadAsync("k");
        loaded.Should().Equal([2, 3]);
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        await Store.SaveAsync("k", [5]);
        await Store.DeleteAsync("k");

        var result = await Store.LoadAsync("k");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Delete_IsNoOp_ForNonExistentKey()
    {
        var act = async () => await Store.DeleteAsync("ghost");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task KeyWithSpecialChars_IsSanitizedToSafeFilename()
    {
        // Keys like "yfex:snapshot:CustomerEdit" must not crash on path-unsafe chars
        var act = async () => await Store.SaveAsync("yfex:snapshot:My/Model?x=1", [7]);
        await act.Should().NotThrowAsync();

        var loaded = await Store.LoadAsync("yfex:snapshot:My/Model?x=1");
        loaded.Should().Equal([7]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
