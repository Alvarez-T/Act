using System.Text;
using YFex.Persistence;
using YFex.Persistence.Sqlite;

namespace YFex.Messaging.Tests.Storage;

/// <summary>Tests #29–31: storage backend swap, SQLite persistence, and concurrent reads.</summary>
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

    private SqliteKeyValueStore CreateSqlite()
        => new(new SqliteConnectionFactory(Path.Combine(_dbDir, _dbFile)));

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ── Test #29: Storage backend swap ────────────────────────────────────────

    [Fact]
    public async Task InMemoryStorage_SetAndGet_RoundTrips()
    {
        IKeyValueStore storage = new MemoryKeyValueStore();
        var data = Bytes("hello");

        await storage.SetAsync("key1", data);
        var result = await storage.GetAsync("key1");

        result.Should().Equal(data);
    }

    [Fact]
    public async Task SqliteStorage_SetAndGet_RoundTrips()
    {
        var storage = CreateSqlite();
        var data = Bytes("sqlite-value");

        await storage.SetAsync("key1", data);
        var result = await storage.GetAsync("key1");

        result.Should().Equal(data);
    }

    [Fact]
    public async Task InMemoryStorage_Delete_RemovesEntry()
    {
        IKeyValueStore storage = new MemoryKeyValueStore();
        await storage.SetAsync("k", Bytes("v"));
        await storage.DeleteAsync("k");

        (await storage.GetAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task SqliteStorage_Delete_RemovesEntry()
    {
        var storage = CreateSqlite();
        await storage.SetAsync("k", Bytes("v"));
        await storage.DeleteAsync("k");

        (await storage.GetAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task InMemoryStorage_GetKeysWithPrefix_ReturnsMatchingKeys()
    {
        IKeyValueStore storage = new MemoryKeyValueStore();
        await storage.SetAsync("cache:a", Bytes("1"));
        await storage.SetAsync("cache:b", Bytes("2"));
        await storage.SetAsync("outbox:x", Bytes("3"));

        var keys = await storage.GetKeysWithPrefixAsync("cache:");
        keys.Should().BeEquivalentTo(["cache:a", "cache:b"]);
    }

    [Fact]
    public async Task SqliteStorage_GetKeysWithPrefix_ReturnsMatchingKeys()
    {
        var storage = CreateSqlite();
        await storage.SetAsync("cache:a", Bytes("1"));
        await storage.SetAsync("cache:b", Bytes("2"));
        await storage.SetAsync("outbox:x", Bytes("3"));

        var keys = await storage.GetKeysWithPrefixAsync("cache:");
        keys.Should().BeEquivalentTo(["cache:a", "cache:b"]);
    }

    // ── Test #30: SQLite persistence across "restart" ─────────────────────────

    [Fact]
    public async Task SqliteStorage_PersistsAcrossReopen_WhenReopenedWithSamePath()
    {
        var data = Bytes("persistent-value");
        const string key = "persist:test";

        // First "session" — write
        await CreateSqlite().SetAsync(key, data);

        // Second "session" — reopen the same file and read
        var result = await CreateSqlite().GetAsync(key);
        result.Should().Equal(data,
            "value written in first session must survive reopen of the same DB file");
    }

    // ── Test #31: SQLite concurrent reads ─────────────────────────────────────

    [Fact]
    public async Task SqliteStorage_ConcurrentReads_AllReturnSameValue()
    {
        var storage = CreateSqlite();
        var data = Bytes("concurrent");
        await storage.SetAsync("shared", data);

        var tasks = Enumerable.Range(0, 8).Select(_ => storage.GetAsync("shared").AsTask());
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().Equal(data));
    }

    [Fact]
    public async Task SqliteStorage_TTL_ExpiresEntry()
    {
        var storage = CreateSqlite();

        await storage.SetAsync("ttl-key", Bytes("expiring"), ttl: TimeSpan.FromMilliseconds(10));
        await Task.Delay(50);

        (await storage.GetAsync("ttl-key")).Should().BeNull("entry past TTL must not be returned");
    }
}
