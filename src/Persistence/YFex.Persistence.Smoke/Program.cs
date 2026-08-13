using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using YFex.Persistence;
using YFex.Persistence.Encryption;
using YFex.Persistence.FileSystem;
using YFex.Persistence.Sqlite;
using ZiggyCreatures.Caching.Fusion;

// ── Smoke harness ───────────────────────────────────────────────────────────────
// Records every check and CONTINUES on failure, so one break never hides the rest.
// [PASS]/[FAIL] are hard assertions; [NOTE] are observations to review by hand.

var results = new List<(string Scenario, string Name, bool Ok, string? Detail)>();
var notes = new List<string>();
string currentScenario = "(startup)";

void A(bool ok, string name, string? detail = null)
{
    results.Add((currentScenario, name, ok, ok ? null : detail));
    Console.WriteLine(ok ? $"    [PASS] {name}" : $"    [FAIL] {name}{(detail is null ? "" : " — " + detail)}");
}

void Note(string text)
{
    notes.Add($"{currentScenario}: {text}");
    Console.WriteLine($"    [NOTE] {text}");
}

async Task Scenario(string name, Func<Task> body)
{
    currentScenario = name;
    Console.WriteLine($"\n=== {name} ===");
    try { await body(); }
    catch (Exception ex)
    {
        results.Add((name, "scenario did not complete", false, ex.GetType().Name + ": " + ex.Message));
        Console.WriteLine($"    [FAIL] scenario threw — {ex.GetType().Name}: {ex.Message}");
    }
}

string tmpRoot = Path.Combine(Path.GetTempPath(), "yfex-persistence-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpRoot);

// ── Reusable contract bodies ────────────────────────────────────────────────────

async Task StoreContractAsync(IKeyValueStore store)
{
    await store.ClearAsync();
    A(await store.GetAsync("kv:a") is null, "get absent → null");
    A(!await store.ExistsAsync("kv:a"), "exists absent → false");

    await store.SetAsync("kv:a", [1, 2, 3]);
    var got = await store.GetAsync("kv:a");
    A(got is not null && got.SequenceEqual(new byte[] { 1, 2, 3 }), "round-trip bytes");
    A(await store.ExistsAsync("kv:a"), "exists present → true");

    await store.SetAsync("kv:b", [9]);
    await store.SetAsync("other:x", [9]);
    var keys = await store.GetKeysWithPrefixAsync("kv:");
    A(keys.Count == 2 && keys.Contains("kv:a") && keys.Contains("kv:b"), "prefix enumeration", $"expected 2, got {keys.Count}");

    await store.DeleteAsync("kv:a");
    A(await store.GetAsync("kv:a") is null, "delete removes entry");

    await store.SetAsync("kv:ttl", [7], TimeSpan.FromMilliseconds(80));
    A(await store.GetAsync("kv:ttl") is not null, "ttl present before expiry");
    await Task.Delay(180);
    A(await store.GetAsync("kv:ttl") is null, "ttl expired → null");

    await store.ClearAsync();
    A((await store.GetKeysWithPrefixAsync("")).Count == 0, "clear empties store");
}

async Task CacheCommonAsync(ICache cache)
{
    A((await cache.GetAsync<Person>("c:1")) is null, "GetAsync absent → default");
    var miss = await cache.TryGetAsync<Person>("c:1");
    A(miss.IsMiss && !miss.IsHit, "TryGetAsync absent → Miss");
    Person? implMiss = miss;
    A(implMiss is null, "implicit T? on miss → null");

    await cache.SetAsync("c:1", new Person("Ada", 36));
    A((await cache.GetAsync<Person>("c:1"))?.Name == "Ada", "SetAsync/GetAsync round-trip");

    var hit = await cache.TryGetAsync<Person>("c:1");
    A(hit.IsHit && !hit.IsStale, "TryGetAsync present → fresh hit");
    A(hit.TryGetValue(out var pv) && pv!.Name == "Ada", "hit.TryGetValue");
    Person? implHit = hit;
    A(implHit?.Name == "Ada", "implicit T? on hit → value");

    await cache.UpdateAsync<Person>("c:1", p => p with { Age = p.Age + 1 });
    A((await cache.GetAsync<Person>("c:1"))?.Age == 37, "UpdateAsync mutates in place");

    await cache.SetAsync("c:opt", new Person("Opt", 1), new CacheEntryOptions { Duration = TimeSpan.FromMinutes(1), Size = 1 });
    A((await cache.GetAsync<Person>("c:opt"))?.Name == "Opt", "SetAsync(options) round-trip");

    await cache.InvalidateAsync("c:1");
    A((await cache.TryGetAsync<Person>("c:1")).IsMiss, "InvalidateAsync → miss");

    await cache.SetAsync("c:ttl", new Person("T", 1), TimeSpan.FromMilliseconds(80));
    A((await cache.GetAsync<Person>("c:ttl")) is not null, "cache ttl before expiry");
    await Task.Delay(180);
    A((await cache.GetAsync<Person>("c:ttl")) is null, "cache ttl after expiry");
}

async Task CacheStaleAndPrefixAsync(ICache cache)
{
    await cache.SetAsync("q:a", new Person("A", 1));
    await cache.SetAsync("q:b", new Person("B", 2));
    await cache.SetAsync("z:c", new Person("C", 3));

    var keys = await cache.GetKeysWithPrefixAsync("q:");
    A(keys.Count == 2, "cache prefix enumeration", $"expected 2, got {keys.Count}");

    await cache.MarkStaleAsync("q:a");
    var r = await cache.TryGetAsync<Person>("q:a");
    A(r.IsHit && r.IsStale, "MarkStaleAsync → hit + stale");
    A(r.TryGetHit(out var cv) && cv.IsStale && cv.GetValue().Name == "A", "stale hit still carries value");
    A((await cache.GetAsync<Person>("q:a"))?.Name == "A", "stale value still served via GetAsync");
}

// ── 1. IKeyValueStore backends ──────────────────────────────────────────────────

await Scenario("KeyValueStore: MemoryKeyValueStore", async () =>
    await StoreContractAsync(new MemoryKeyValueStore()));

await Scenario("KeyValueStore: FileSystemKeyValueStore", async () =>
{
    var dir = Path.Combine(tmpRoot, "fs-store");
    await StoreContractAsync(new FileSystemKeyValueStore(dir));

    // Persistence across instances (same directory).
    var a = new FileSystemKeyValueStore(dir);
    await a.SetAsync("persist", Encoding.UTF8.GetBytes("kept"));
    var b = new FileSystemKeyValueStore(dir);
    var back = await b.GetAsync("persist");
    A(back is not null && Encoding.UTF8.GetString(back) == "kept", "survives new store instance (durable)");
});

await Scenario("KeyValueStore: SqliteKeyValueStore", async () =>
{
    var db = Path.Combine(tmpRoot, "kv.db");
    var factory = new SqliteConnectionFactory(db);
    await StoreContractAsync(new SqliteKeyValueStore(factory));

    var a = new SqliteKeyValueStore(factory);
    await a.SetAsync("persist", Encoding.UTF8.GetBytes("kept"));
    var b = new SqliteKeyValueStore(factory);
    var back = await b.GetAsync("persist");
    A(back is not null && Encoding.UTF8.GetString(back) == "kept", "survives new store instance (durable)");
});

await Scenario("KeyValueStore: EncryptedKeyValueStore (decorator)", async () =>
{
    var key = new byte[32];
    RandomNumberGenerator.Fill(key);
    using var protector = new AesGcmValueProtector(new ProvidedKeyProvider(key));
    var inner = new MemoryKeyValueStore();
    var enc = new EncryptedKeyValueStore(inner, protector);

    await StoreContractAsync(enc);

    await enc.SetAsync("secret", Encoding.UTF8.GetBytes("hello"));
    var back = await enc.GetAsync("secret");
    A(back is not null && Encoding.UTF8.GetString(back) == "hello", "encrypted round-trip");

    var raw = await inner.GetAsync("secret");
    A(raw is not null && !raw.SequenceEqual(Encoding.UTF8.GetBytes("hello")), "inner bytes are ciphertext (not plaintext)");

    var key2 = new byte[32];
    RandomNumberGenerator.Fill(key2);
    using var wrong = new AesGcmValueProtector(new ProvidedKeyProvider(key2));
    var encWrong = new EncryptedKeyValueStore(inner, wrong);
    A(await encWrong.GetAsync("secret") is null, "wrong key → null (auth failure, not corrupt data)");
});

// ── 2. ICache implementations ───────────────────────────────────────────────────

await Scenario("ICache: InMemoryCache", async () =>
{
    await CacheCommonAsync(new InMemoryCache());
    await CacheStaleAndPrefixAsync(new InMemoryCache());
});

await Scenario("ICache: KeyValueCache over MemoryKeyValueStore", async () =>
{
    await CacheCommonAsync(new KeyValueCache(new MemoryKeyValueStore()));
    await CacheStaleAndPrefixAsync(new KeyValueCache(new MemoryKeyValueStore()));
});

await Scenario("ICache: KeyValueCache over SqliteKeyValueStore", async () =>
{
    var factory = new SqliteConnectionFactory(Path.Combine(tmpRoot, "cache.db"));
    await CacheCommonAsync(new KeyValueCache(new SqliteKeyValueStore(factory, "cache_a")));
    await CacheStaleAndPrefixAsync(new KeyValueCache(new SqliteKeyValueStore(factory, "cache_b")));
});

await Scenario("ICache: FusionCacheAdapter (direct, L1-only)", async () =>
{
    using var fusion = new FusionCache(new FusionCacheOptions());
    var cache = new FusionCacheAdapter(fusion);

    await CacheCommonAsync(cache);

    // MarkStale maps to FusionCache ExpireAsync; observe how TryGet reports it.
    await cache.SetAsync("f:1", new Person("F", 1));
    await cache.MarkStaleAsync("f:1");
    var r = await cache.TryGetAsync<Person>("f:1");
    if (r.IsHit && r.IsStale)
        A(true, "MarkStale reflected as stale hit");
    else
        Note($"MarkStale not reflected in TryGetAsync (IsHit={r.IsHit}, IsStale={r.IsStale}) — adapter reports Fresh; FusionCache metadata not plumbed.");

    bool threw = false;
    try { await cache.GetKeysWithPrefixAsync("f:"); }
    catch (NotSupportedException) { threw = true; }
    A(threw, "GetKeysWithPrefixAsync throws NotSupportedException (documented)");
});

await Scenario("ICache: AddYFexFusionCache DI (bounded L1, #7)", async () =>
{
    var services = new ServiceCollection();
    services.AddYFexFusionCache(l1SizeLimit: 100);
    await using var sp = services.BuildServiceProvider();
    var cache = sp.GetRequiredService<ICache>();

    await cache.SetAsync("s:plain", new Person("Plain", 1));
    A((await cache.GetAsync<Person>("s:plain"))?.Name == "Plain", "plain SetAsync against SizeLimit L1");

    await cache.SetAsync("s:sized", new Person("Sized", 2), new CacheEntryOptions { Size = 1, Duration = TimeSpan.FromMinutes(1) });
    A((await cache.GetAsync<Person>("s:sized"))?.Name == "Sized", "sized SetAsync against SizeLimit L1");
});

// ── 3. Union types (CacheValue / CacheResult) ───────────────────────────────────

await Scenario("Unions: CacheValue<T> and CacheResult<T>", async () =>
{
    var fresh = CacheValue<int>.Fresh(10, DateTimeOffset.UnixEpoch);
    A(fresh.IsFresh && !fresh.IsStale && fresh.HasValue, "CacheValue.Fresh flags");
    int fv = fresh;
    A(fv == 10, "CacheValue implicit → T");
    A(fresh.LastModified == DateTimeOffset.UnixEpoch, "CacheValue carries LastModified");

    var stale = CacheValue<int>.Stale(20);
    A(stale.IsStale && stale.Match(_ => 1, _ => 2) == 2, "CacheValue.Stale + Match(stale arm)");

    CacheResult<int> hit = CacheValue<int>.Fresh(7); // implicit CacheValue → CacheResult
    A(hit.IsHit && !hit.IsMiss && !hit.IsStale, "CacheResult hit flags");
    A(hit.TryGetValue(out var hv) && hv == 7, "CacheResult.TryGetValue");
    int? hi = hit;
    A(hi == 7, "CacheResult implicit → T? on hit");

    var m = CacheResult<int>.Missing;
    A(m.IsMiss && !m.IsHit, "CacheResult miss flags");
    int? mi = m;
    A(mi is null, "CacheResult implicit → T? on miss → null");
    A(m.Match(_ => "h", () => "m") == "m", "CacheResult.Match(miss arm)");

    var staleHit = new CacheResult<int>(CacheValue<int>.Stale(5));
    A(staleHit.IsHit && staleHit.IsStale, "CacheResult composes a stale hit");

    await Task.CompletedTask;
});

// ── 4. Schema versioning (#4) ───────────────────────────────────────────────────

await Scenario("Schema versioning: drop-on-mismatch (#4)", async () =>
{
    var store = new MemoryKeyValueStore();
    var v1 = new KeyValueCache(store, schema: 1);
    await v1.SetAsync("doc", new Person("V1", 1));
    A((await v1.TryGetAsync<Person>("doc")).IsHit, "same-schema read → hit");

    var v2 = new KeyValueCache(store, schema: 2);
    A((await v2.TryGetAsync<Person>("doc")).IsMiss, "different-schema read → miss (dropped)");
    A(await store.GetAsync("doc") is null, "mismatched entry deleted from underlying store");
});

// ── 5. Provenance on read (#2) ──────────────────────────────────────────────────

await Scenario("Provenance: TryGetAsync timestamps (#2)", async () =>
{
    var before = DateTimeOffset.UtcNow.AddSeconds(-2);

    var mem = new InMemoryCache();
    await mem.SetAsync("p", new Person("P", 1));
    var rm = await mem.TryGetAsync<Person>("p");
    A(rm.TryGetHit(out var mcv) && mcv.LastModified is not null && mcv.LastModified >= before,
        "InMemoryCache reports LastModified");

    var kv = new KeyValueCache(new MemoryKeyValueStore());
    await kv.SetAsync("p", new Person("P", 1));
    var rk = await kv.TryGetAsync<Person>("p");
    A(rk.TryGetHit(out var kcv) && kcv.LastModified is not null && kcv.LastModified >= before,
        "KeyValueCache reports LastModified (from envelope StoredAtTicks)");
});

// ── 6. PersistenceService + ISnapshotProvider ───────────────────────────────────

await Scenario("PersistenceService: snapshot save/restore/clear", async () =>
{
    var store = new MemoryKeyValueStore();
    var svc = new PersistenceService(store);
    var provider = new CounterSnapshotProvider { Count = 5 };
    svc.Register(provider);

    await svc.SaveSnapshotAsync();
    provider.Count = 0;
    await svc.RestoreSnapshotAsync();
    A(provider.Count == 5, "save then restore recovers state", $"got {provider.Count}");

    await svc.ClearSnapshotAsync(provider.Discriminator);
    provider.Count = 9;
    await svc.RestoreSnapshotAsync();
    A(provider.Count == 9, "clear removes snapshot (restore is a no-op)");

    PersistenceService.Configure(svc);
    A(ReferenceEquals(PersistenceService.Current, svc), "Current facade returns the configured instance");
});

// ── 7. IDurableQueue ────────────────────────────────────────────────────────────

await Scenario("DurableQueue: NullDurableQueue", async () =>
{
    var q = new NullDurableQueue<QueueItem>();
    await q.EnqueueAsync([new QueueItem("x")], default);
    A(await q.CountAsync(default) == 0, "null queue count is always 0");
    A((await q.DequeueAsync(10, default)).Count == 0, "null queue dequeues empty");
    await q.DisposeAsync();
});

await Scenario("DurableQueue: SqliteDurableQueue<T>", async () =>
{
    var factory = new SqliteConnectionFactory(Path.Combine(tmpRoot, "queue.db"));
    await using var q = new SqliteDurableQueue<QueueItem>(factory, new JsonQueueSerializer<QueueItem>(), TimeSpan.FromMinutes(5));

    await q.EnqueueAsync([new QueueItem("a"), new QueueItem("b")], default);
    A(await q.CountAsync(default) == 2, "count after enqueue");

    var head = await q.DequeueAsync(1, default);
    A(head.Count == 1 && head[0].Value == "a", "FIFO dequeue returns head");
    A(await q.CountAsync(default) == 1, "count decremented after dequeue");

    await using var q2 = new SqliteDurableQueue<QueueItem>(factory, new JsonQueueSerializer<QueueItem>(), TimeSpan.FromMilliseconds(1), "q_ttl");
    await q2.EnqueueAsync([new QueueItem("old")], default);
    await Task.Delay(40);
    await q2.PurgeExpiredAsync(default);
    A(await q2.CountAsync(default) == 0, "PurgeExpiredAsync removes expired items");
});

// ── 8. Serializer + IDistributedCache bridge ────────────────────────────────────

await Scenario("Serializer: MemoryPackPersistenceSerializer", async () =>
{
    var ser = MemoryPackPersistenceSerializer.Instance;
    var bytes = ser.Serialize(new Person("Se", 7));
    var round = ser.Deserialize<Person>(bytes);
    A(round is not null && round.Name == "Se" && round.Age == 7, "serialize/deserialize round-trip");
    await Task.CompletedTask;
});

await Scenario("Bridge: KeyValueStoreDistributedCache", async () =>
{
    var store = new MemoryKeyValueStore();
    IDistributedCache dc = new KeyValueStoreDistributedCache(store);

    await dc.SetAsync("d:1", Encoding.UTF8.GetBytes("v"), new DistributedCacheEntryOptions());
    var b = await dc.GetAsync("d:1");
    A(b is not null && Encoding.UTF8.GetString(b) == "v", "async round-trip");
    await dc.RemoveAsync("d:1");
    A(await dc.GetAsync("d:1") is null, "async remove");

    // Sync path: works on console (no SynchronizationContext); would deadlock on Blazor WASM.
    dc.Set("d:2", Encoding.UTF8.GetBytes("w"), new DistributedCacheEntryOptions());
    A(dc.Get("d:2") is { } g && Encoding.UTF8.GetString(g) == "w", "sync round-trip (console only)");
    Note("KeyValueStoreDistributedCache sync Get/Set/Remove use GetAwaiter().GetResult() — safe on console, will block/deadlock on Blazor WASM.");
});

// ── Summary ──────────────────────────────────────────────────────────────────────

try { Directory.Delete(tmpRoot, recursive: true); } catch { /* best effort */ }

var failed = results.Where(r => !r.Ok).ToList();
int passCount = results.Count(r => r.Ok);

Console.WriteLine("\n=====================================================");
Console.WriteLine($" SMOKE SUMMARY: {passCount}/{results.Count} checks passed, {failed.Count} failed, {notes.Count} notes");
Console.WriteLine("=====================================================");

if (failed.Count > 0)
{
    Console.WriteLine("\nFAILURES:");
    foreach (var f in failed)
        Console.WriteLine($"  - [{f.Scenario}] {f.Name}{(f.Detail is null ? "" : " — " + f.Detail)}");
}

if (notes.Count > 0)
{
    Console.WriteLine("\nNOTES:");
    foreach (var n in notes)
        Console.WriteLine($"  - {n}");
}

Console.WriteLine();
return failed.Count == 0 ? 0 : 1;

// ── Types under test ──────────────────────────────────────────────────────────────

[MemoryPackable]
public partial record Person(string Name, int Age);

public sealed record QueueItem(string Value);

public sealed class JsonQueueSerializer<T> : IQueueItemSerializer<T> where T : class
{
    public string Serialize(T item) => System.Text.Json.JsonSerializer.Serialize(item);
    public T? Deserialize(string data) => System.Text.Json.JsonSerializer.Deserialize<T>(data);
}

public sealed class CounterSnapshotProvider : ISnapshotProvider
{
    public int Count { get; set; }
    public string Discriminator => "smoke:counter";
    public int Version => 1;

    public ValueTask<byte[]?> CaptureAsync(CancellationToken ct = default)
        => new(BitConverter.GetBytes(Count));

    public ValueTask RestoreAsync(byte[] data, int storedVersion, CancellationToken ct = default)
    {
        if (data.Length >= 4) Count = BitConverter.ToInt32(data);
        return ValueTask.CompletedTask;
    }
}
