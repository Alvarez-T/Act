using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YFex.Messaging.Rpc;
using YFex.Messaging.Rpc.Sqlite;

namespace YFex.Messaging.Tests.Storage;

/// <summary>Tests #29–31: Storage backend swap, SQLite persistence, and concurrent reads.</summary>
[Trait("Category", "Storage")]
public sealed class StorageTests
{
    private readonly string _dbDir;
    private readonly string _dbFile;

    public StorageTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"yfex-test-{Guid.NewGuid():N}");
        _dbFile = $"test-{Guid.NewGuid():N}.db";
        Directory.CreateDirectory(_dbDir);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(IServiceProvider Sp, SqliteClientStorage Storage)> CreateSqliteAsync()
    {
        var services = new ServiceCollection();
        services.AddYFexSqliteStorage(opts =>
        {
            opts.Directory = _dbDir;
            opts.DatabaseFileName = _dbFile;
        });
        var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<SqliteClientStorage>();
        _ = await storage.GetAsync("_warmup"); // trigger schema creation
        return (sp, storage);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ── Test #29: Storage backend swap ────────────────────────────────────────

    [Fact]
    public async Task InMemoryStorage_SetAndGet_RoundTrips()
    {
        IClientStorage storage = new InMemoryClientStorage();
        var data = Bytes("hello");

        await storage.SetAsync("key1", data);
        var result = await storage.GetAsync("key1");

        result.Should().Equal(data);
    }

    [Fact]
    public async Task SqliteStorage_SetAndGet_RoundTrips()
    {
        var (sp, storage) = await CreateSqliteAsync();
        await using var _ = sp as IAsyncDisposable;
        var data = Bytes("sqlite-value");

        await storage.SetAsync("key1", data);
        var result = await storage.GetAsync("key1");

        result.Should().Equal(data);
    }

    [Fact]
    public async Task InMemoryStorage_Delete_RemovesEntry()
    {
        IClientStorage storage = new InMemoryClientStorage();
        await storage.SetAsync("k", Bytes("v"));
        await storage.DeleteAsync("k");

        (await storage.GetAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task SqliteStorage_Delete_RemovesEntry()
    {
        var (sp, storage) = await CreateSqliteAsync();
        await using var _ = sp as IAsyncDisposable;

        await storage.SetAsync("k", Bytes("v"));
        await storage.DeleteAsync("k");

        (await storage.GetAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task InMemoryStorage_GetKeysWithPrefix_ReturnsMatchingKeys()
    {
        IClientStorage storage = new InMemoryClientStorage();
        await storage.SetAsync("cache:a", Bytes("1"));
        await storage.SetAsync("cache:b", Bytes("2"));
        await storage.SetAsync("outbox:x", Bytes("3"));

        var keys = await storage.GetKeysWithPrefixAsync("cache:");
        keys.Should().BeEquivalentTo(["cache:a", "cache:b"]);
    }

    [Fact]
    public async Task SqliteStorage_GetKeysWithPrefix_ReturnsMatchingKeys()
    {
        var (sp, storage) = await CreateSqliteAsync();
        await using var _ = sp as IAsyncDisposable;

        await storage.SetAsync("cache:a", Bytes("1"));
        await storage.SetAsync("cache:b", Bytes("2"));
        await storage.SetAsync("outbox:x", Bytes("3"));

        var keys = await storage.GetKeysWithPrefixAsync("cache:");
        keys.Should().BeEquivalentTo(["cache:a", "cache:b"]);
    }

    // ── Test #30: SQLite persistence across "restart" ─────────────────────────

    [Fact]
    public async Task SqliteStorage_PersistsAcrossDispose_WhenReopenedWithSamePath()
    {
        var data = Bytes("persistent-value");
        const string key = "persist:test";

        // First "session" — write and close
        {
            var (sp, storage) = await CreateSqliteAsync();
            await storage.SetAsync(key, data);
            await ((IAsyncDisposable)sp).DisposeAsync();
        }

        // Second "session" — reopen the same file and read
        {
            var (sp, storage) = await CreateSqliteAsync();
            await using var _ = sp as IAsyncDisposable;
            var result = await storage.GetAsync(key);
            result.Should().Equal(data,
                "value written in first session must survive close+reopen of the same DB file");
        }
    }

    // ── Test #31: SQLite concurrent reads ─────────────────────────────────────

    [Fact]
    public async Task SqliteStorage_ConcurrentReads_AllReturnSameValue()
    {
        var (sp, storage) = await CreateSqliteAsync();
        await using var _ = sp as IAsyncDisposable;
        var data = Bytes("concurrent");
        await storage.SetAsync("shared", data);

        var tasks = Enumerable.Range(0, 8).Select(_ => storage.GetAsync("shared").AsTask());
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().Equal(data));
    }

    [Fact]
    public async Task SqliteStorage_TTL_ExpiresEntry()
    {
        var (sp, storage) = await CreateSqliteAsync();
        await using var _ = sp as IAsyncDisposable;

        await storage.SetAsync("ttl-key", Bytes("expiring"), ttl: TimeSpan.FromMilliseconds(10));
        await Task.Delay(50);

        (await storage.GetAsync("ttl-key")).Should().BeNull("entry past TTL must not be returned");
    }
}
