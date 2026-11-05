using System.Text;
using Microsoft.Data.Sqlite;
using YFex.Data;
using YFex.Data.Oracle;
using YFex.Data.Postgres;
using YFex.Data.Sqlite;
using YFex.Data.SqlServer;

namespace YFex.Rules.Data.Tests;

/// <summary>
/// The <see cref="IRuleStore"/> contract (05 §4.2–4.3), run against every implementation: per-rule-set version numbers,
/// optimistic concurrency per stream, incremental loads, lookups, lossless round trip, concurrent appends.
/// </summary>
public abstract class RuleStoreContract
{
    protected abstract IRuleStore CreateStore();

    private static readonly RuleTierPath Tenant = RuleTierPath.Of(RawValue.From("t1"));
    private static readonly RuleTierPath Store1 = RuleTierPath.Of(RawValue.From("t1"), RawValue.From("loja|1 ç"));   // separator + non-ASCII
    private static readonly DateOnly Day = new(2026, 9, 30);

    private static RuleDataKey Seats(RuleTierPath path) => new("pricing", RuleDataKind.Table, "pricing.seats", path);
    private static RuleDataKey Rate(RuleTierPath path) => new("pricing", RuleDataKind.Parameters, "pricing.volume", path);
    private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Versions_are_numbered_per_rule_set_across_streams()
    {
        var store = CreateStore();
        (await store.AppendAsync(Seats(Tenant), 0, Day, Json("{\"rows\":[]}"), "ana", null, default)).Version.Should().Be(1);
        (await store.AppendAsync(Rate(Tenant), 0, Day, Json("{\"rate\":10}"), "ana", null, default)).Version.Should().Be(2);
        (await store.AppendAsync(Seats(Store1), 0, Day, Json("{\"rows\":[]}"), "ana", null, default)).Version.Should().Be(3);
        (await store.AppendAsync(new("freight", RuleDataKind.Table, "freight.zones", Tenant), 0, Day, Json("{}"), null, null, default))
            .Version.Should().Be(1);
    }

    [Fact]
    public async Task A_stale_base_version_is_a_conflict_naming_the_actual_version()
    {
        var store = CreateStore();
        await store.AppendAsync(Seats(Tenant), 0, Day, Json("{\"rows\":[]}"), "ana", null, default);
        await store.AppendAsync(Rate(Tenant), 0, Day, Json("{\"rate\":10}"), "ana", null, default);   // other stream: no effect

        var act = async () => await store.AppendAsync(Seats(Tenant), 0, Day, Json("{\"rows\":[]}"), "bia", null, default);

        var e = (await act.Should().ThrowAsync<RuleStoreConflictException>()).Which;
        e.ExpectedVersion.Should().Be(0);
        e.ActualVersion.Should().Be(1);
        (await store.AppendAsync(Seats(Tenant), 1, Day, Json("{\"rows\":[]}"), "bia", null, default)).Version.Should().Be(3);
    }

    [Fact]
    public async Task Load_after_a_version_returns_newer_versions_ascending()
    {
        var store = CreateStore();
        for (int i = 0; i < 4; i++) await store.AppendAsync(Seats(Tenant), i, Day, Json($"{{\"v\":{i}}}"), null, null, default);

        (await store.LoadAsync("pricing", 0, default)).Select(r => r.Info.Version).Should().Equal(1, 2, 3, 4);
        (await store.LoadAsync("pricing", 2, default)).Select(r => r.Info.Version).Should().Equal(3, 4);
        (await store.LoadAsync("pricing", 4, default)).Should().BeEmpty();
        (await store.LoadAsync("other", 0, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Lookups_by_stream_and_by_version()
    {
        var store = CreateStore();
        await store.AppendAsync(Seats(Tenant), 0, Day, Json("{\"a\":1}"), null, null, default);
        await store.AppendAsync(Seats(Store1), 0, Day, Json("{\"b\":1}"), null, null, default);
        await store.AppendAsync(Seats(Tenant), 1, Day, Json("{\"a\":2}"), null, null, default);

        (await store.GetVersionsAsync(Seats(Tenant), default)).Select(i => i.Version).Should().Equal(3, 1);   // newest first
        (await store.GetAsync(Seats(Tenant), 1, default))!.Payload.ToArray().Should().Equal(Json("{\"a\":1}"));
        (await store.GetAsync(Seats(Tenant), 2, default)).Should().BeNull();                                   // v2 is Store1's
        (await store.GetByVersionAsync("pricing", 2, default))!.Info.Key.Should().Be(Seats(Store1));
        (await store.GetByVersionAsync("pricing", 9, default)).Should().BeNull();
    }

    [Fact]
    public async Task Every_field_round_trips()
    {
        var store = CreateStore();
        var path = RuleTierPath.Of(RawValue.From("t1"), RawValue.From(new Money(12.5m)), RawValue.From(42));
        var key = new RuleDataKey("pricing", RuleDataKind.Override, "pricing.volume", path);
        var payload = new byte[] { 0, 1, 2, 255, 254 };

        var appended = await store.AppendAsync(key, 0, new DateOnly(2027, 1, 31), payload, null, "note ç ✓", default);
        var loaded = (await store.LoadAsync("pricing", 0, default)).Single();

        loaded.Info.Key.Should().Be(key);
        loaded.Info.Key.TierPath.Should().Be(path);
        loaded.Info.EffectiveFrom.Should().Be(new DateOnly(2027, 1, 31));
        loaded.Info.PublishedBy.Should().BeNull();
        loaded.Info.Note.Should().Be("note ç ✓");
        loaded.Info.PublishedAt.Should().BeCloseTo(appended.PublishedAt, TimeSpan.FromMilliseconds(1));
        loaded.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task Concurrent_appends_on_different_streams_all_succeed_with_distinct_versions()
    {
        var store = CreateStore();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            (await store.AppendAsync(new("pricing", RuleDataKind.Table, "t" + i, Tenant), 0, Day, Json("{}"), null, null, default)).Version));

        var versions = await Task.WhenAll(tasks);

        versions.Order().Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
    }

    [Fact]
    public async Task Concurrent_appends_on_one_stream_from_the_same_base_let_exactly_one_win()
    {
        var store = CreateStore();
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            try { await store.AppendAsync(Seats(Tenant), 0, Day, Json("{}"), null, null, default); return true; }
            catch (RuleStoreConflictException) { return false; }
        }));

        (await Task.WhenAll(tasks)).Count(won => won).Should().Be(1);
        (await store.GetVersionsAsync(Seats(Tenant), default)).Should().ContainSingle();
    }
}

public sealed class InMemoryRuleStoreTests : RuleStoreContract
{
    protected override IRuleStore CreateStore() => new InMemoryRuleStore();
}

public sealed class SqliteRuleStoreTests : RuleStoreContract, IDisposable
{
    private readonly List<string> _files = [];

    protected override IRuleStore CreateStore()
    {
        string file = Path.Combine(Path.GetTempPath(), "yfex-rules-" + Guid.NewGuid().ToString("N") + ".db");
        _files.Add(file);
        var connections = new YFexConnectionFactory(() => new SqliteConnection($"Data Source={file};Pooling=False"), SqliteDialect.Instance);
        using (YFexConnection connection = connections.Create())
            connection.ApplySchemaAsync(RuleStoreSchema.Module).GetAwaiter().GetResult();
        var store = new SqlRuleStore(connections, new SqlRuleStoreOptions { MaxAppendAttempts = 50 });
        store.EnsureSchemaAsync().GetAwaiter().GetResult();          // already applied: no-op
        return store;
    }

    [Fact]
    public void The_schema_is_valid_on_every_engine()
    {
        SchemaAssert.Valid(RuleStoreSchema.Module, Engines);
        SchemaAssert.Valid(RuleStoreSchema.ForTable("tenant_rules"), Engines);
    }

    /// <summary>Version 1 must never change once applied anywhere (the migrator refuses drift): add a version instead.</summary>
    [Fact]
    public void Version_1_is_pinned()
    {
        RuleStoreSchema.Module.Name.ToString().Should().Be("yfex.rules");
        RuleStoreSchema.Module.LatestVersion.Should().Be(SchemaVersionNumber.First);
        RuleStoreSchema.Module[SchemaVersionNumber.First].Checksum.ToString().Should().Be(PinnedV1);
    }

    /// <summary>
    /// DS-P4 pilot: the generated schema of <see cref="RuleDataRow"/> equals version 1 written out by hand, so its DDL is unchanged, and the
    /// module stays in sync with the row class (a new property fails here, printing the version to add).
    /// </summary>
    [Fact]
    public void The_row_class_describes_the_table_the_module_creates()
    {
        RuleDataRow.Schema.Equals(RuleStoreSchema.Table()).Should().BeTrue();
        SchemaAssert.InSync(RuleStoreSchema.Module, RuleDataRow.Schema);
        RuleDataRow.Columns.StreamHash.Value.Should().Be("stream_hash");
        RuleDataRow.BulkTable.ColumnCount.Should().Be(11);
    }

    // Recorded when version 1 was written (DS-P3, 2026-10-23).
    private const string PinnedV1 = "a3ca2e6acb504360a512b532f1af72cba3f5999c56eba511f7b2b434724dc716";

    /// <summary>Cross-check with the hand-written Oracle and SQL Server DDL this module replaced (DS-P3): the same types and keys.</summary>
    [Fact]
    public void The_generated_ddl_matches_the_hand_written_one()
    {
        string oracle = string.Join('\n', OracleDialect.Instance.ScriptSchema(RuleStoreSchema.Module));
        foreach (string column in (string[])["rule_set varchar2(200 char) not null", "version number(19) not null", "kind number(5) not null",
                     "code varchar2(200 char) not null", "tier_path varchar2(2000 char) not null", "stream_hash number(19) not null",
                     "effective_from varchar2(200 char) not null", "published_by varchar2(200 char) null", "published_at varchar2(200 char) not null",
                     "note varchar2(2000 char) null", "payload blob not null", "constraint pk_yfex_rule_data primary key (rule_set, version)"])
            oracle.Should().Contain(column);
        oracle.Should().Contain("create index ix_yfex_rule_data_stream on yfex_rule_data (rule_set, stream_hash, version)");
        string sqlServer = string.Join('\n', SqlServerDialect.Instance.ScriptSchema(RuleStoreSchema.Module));
        sqlServer.Should().Contain("tier_path nvarchar(2000) not null").And.Contain("payload varbinary(max) not null");
    }

    [Fact]
    public async Task A_store_with_its_own_table_has_its_own_module()
    {
        RuleStoreSchema.ForTable("tenant_rules").Name.ToString().Should().Be("yfex.rules.tenant-rules");
        RuleStoreSchema.ForTable("TENANT_RULES").Should().BeSameAs(RuleStoreSchema.ForTable("tenant_rules"));
        RuleStoreSchema.ForTable(RuleStoreSchema.DefaultTable).Should().BeSameAs(RuleStoreSchema.Module);

        string file = Path.Combine(Path.GetTempPath(), "yfex-rules-" + Guid.NewGuid().ToString("N") + ".db");
        _files.Add(file);
        var connections = new YFexConnectionFactory(() => new SqliteConnection($"Data Source={file};Pooling=False"), SqliteDialect.Instance);
        var store = new SqlRuleStore(connections, new SqlRuleStoreOptions { TableName = "tenant_rules" });
        store.Schema.Should().BeSameAs(RuleStoreSchema.ForTable("tenant_rules"));
        await store.EnsureSchemaAsync();
        var key = new RuleDataKey("loja", RuleDataKind.Parameters, "limites", RuleTierPath.Of(RawValue.From("t1")));
        RuleDataInfo info = await store.AppendAsync(key, 0, new DateOnly(2026, 1, 1), new byte[] { 1 }, "ana", null, default);
        (await store.GetAsync(key, info.Version, default)).Should().NotBeNull();
        await using YFexConnection connection = connections.Create();
        (await connection.GetSchemaStatusAsync([RuleStoreSchema.Module, store.Schema])).Should().SatisfyRespectively(
            other => other.PendingCount.Should().Be(1, "the default table was never created here"),
            own => own.IsUpToDate.Should().BeTrue());
    }

    private static readonly IQueryDialect[] Engines =
        [SqliteDialect.Instance, PostgresDialect.Instance, SqlServerDialect.Instance, OracleDialect.Instance];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _files)
            try { File.Delete(f); } catch (IOException) { }
    }
}
