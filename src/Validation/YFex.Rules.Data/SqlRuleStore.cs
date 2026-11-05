using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using YFex.Data;
using YFex.Helpers;

namespace YFex.Rules.Data;

/// <summary>Options of <see cref="SqlRuleStore"/>.</summary>
public sealed class SqlRuleStoreOptions
{
    /// <summary>
    /// Table name (default <see cref="RuleStoreSchema.DefaultTable"/>; the dialect normalizes its casing, e.g. upper-case on Oracle). Its
    /// schema module is <see cref="RuleStoreSchema.ForTable"/>.
    /// </summary>
    public string TableName { get; set; } = RuleStoreSchema.DefaultTable;
    /// <summary>Retries of an append that lost a race for the next version number (unique key collision).</summary>
    public int MaxAppendAttempts { get; set; } = 5;
}

/// <summary>
/// Server <see cref="IRuleStore"/> over a relational database (05 §4.3): one row per published version, immutable.
/// <list type="bullet">
/// <item>Primary key <c>(rule_set, version)</c>: versions are numbered per rule set; two concurrent appends can't both get
///   the same number — the loser's insert fails and is retried (re-checking its base version).</item>
/// <item>Stream = (kind, code, tier path); <c>stream_hash</c> (<see cref="StableHash64"/> of the three) keeps the lookup index
///   small on every engine (SQL Server's 1 700-byte index key limit), the exact columns are still compared.</item>
/// <item>Optimistic concurrency per stream: the latest version of the stream must equal the editor's base version.</item>
/// <item>Dates as ISO text (<c>yyyy-MM-dd</c>, round-trip UTC timestamps) — sortable and identical on every engine.</item>
/// </list>
/// Commands are plain ADO.NET on the <see cref="YFexConnection"/> (no reflection); the dialect supplies the parameter prefix
/// and identifier casing. The table is the portable schema module <see cref="Schema"/> (<see cref="RuleStoreSchema"/>): apply it with
/// <see cref="EnsureSchemaAsync"/> or <c>ApplySchemaAsync</c>, or script it with <c>dialect.ScriptSchema(store.Schema)</c>.
/// </summary>
public sealed class SqlRuleStore : IRuleStore
{
    private readonly YFexConnectionFactory _connections;
    private readonly SqlRuleStoreOptions _options;
    private readonly TimeProvider _clock;

    public SqlRuleStore(YFexConnectionFactory connections, SqlRuleStoreOptions options, TimeProvider? clock = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        Schema = RuleStoreSchema.ForTable(_options.TableName);
    }

    /// <summary>The schema module of this store's table (<see cref="RuleStoreSchema.ForTable"/>).</summary>
    public SchemaModule Schema { get; }

    private const string Columns = "rule_set, version, kind, code, tier_path, stream_hash, effective_from, published_by, published_at, note, payload";

    // ── Schema ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <see cref="Schema"/> (creates the table on first use; a no-op once applied). Runs YFex.Data's migrator, which records the
    /// version in <c>yfex_schema_history</c> and takes the module's migration lock.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var c = _connections.Create();
        await c.ApplySchemaAsync(Schema, cancellationToken: ct).ConfigureAwait(false);
    }

    // ── IRuleStore ──────────────────────────────────────────────────────────

    public async ValueTask<RuleDataInfo> AppendAsync(RuleDataKey key, long baseVersion, DateOnly effectiveFrom, ReadOnlyMemory<byte> payload,
        string? publishedBy, string? note, CancellationToken ct)
    {
        string tierPath = RuleTierPaths.Format(key.TierPath);
        long hash = StreamHash(key.Kind, key.Code, tierPath);
        for (int attempt = 1; ; attempt++)
        {
            await using var c = _connections.Create();
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await c.Inner.BeginTransactionAsync(ct).ConfigureAwait(false);
            string table = Table(c);

            long latest;
            await using (var q = Command(c, tx, $"SELECT MAX(version) FROM {table} WHERE rule_set = {P(c, "s")} AND stream_hash = {P(c, "h")} "
                + $"AND kind = {P(c, "k")} AND code = {P(c, "c")} AND tier_path = {P(c, "p")}"))
            {
                Add(c, q, "s", key.RuleSet);
                Add(c, q, "h", hash);
                Add(c, q, "k", (short)key.Kind);
                Add(c, q, "c", key.Code);
                Add(c, q, "p", tierPath);
                latest = AsLong(await q.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            if (latest != baseVersion) throw new RuleStoreConflictException(key, baseVersion, latest);

            long version;
            await using (var q = Command(c, tx, $"SELECT MAX(version) FROM {table} WHERE rule_set = {P(c, "s")}"))
            {
                Add(c, q, "s", key.RuleSet);
                version = AsLong(await q.ExecuteScalarAsync(ct).ConfigureAwait(false)) + 1;
            }

            var info = new RuleDataInfo(key, version, effectiveFrom, publishedBy, _clock.GetUtcNow(), note);
            try
            {
                await using (var insert = Command(c, tx, $"INSERT INTO {table} ({Columns}) VALUES ({P(c, "s")}, {P(c, "v")}, {P(c, "k")}, {P(c, "c")}, "
                    + $"{P(c, "p")}, {P(c, "h")}, {P(c, "ef")}, {P(c, "by")}, {P(c, "at")}, {P(c, "n")}, {P(c, "b")})"))
                {
                    Add(c, insert, "s", key.RuleSet);
                    Add(c, insert, "v", version);
                    Add(c, insert, "k", (short)key.Kind);
                    Add(c, insert, "c", key.Code);
                    Add(c, insert, "p", tierPath);
                    Add(c, insert, "h", hash);
                    Add(c, insert, "ef", FormatDate(effectiveFrom));
                    Add(c, insert, "by", publishedBy);
                    Add(c, insert, "at", FormatTimestamp(info.PublishedAt));
                    Add(c, insert, "n", note);
                    Add(c, insert, "b", payload.ToArray());
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return info;
            }
            catch (DbException) when (attempt < _options.MaxAppendAttempts)
            {
                // Lost the race for `version` (another append of the same rule set committed first): retry — the stream
                // check above turns it into a conflict if that append was on OUR stream.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<ImmutableArray<RuleDataRecord>> LoadAsync(string ruleSet, long afterVersion, CancellationToken ct)
    {
        await using var c = _connections.Create();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var q = Command(c, null, $"SELECT {Columns} FROM {Table(c)} WHERE rule_set = {P(c, "s")} AND version > {P(c, "v")} ORDER BY version");
        Add(c, q, "s", ruleSet);
        Add(c, q, "v", afterVersion);
        return await ReadRecordsAsync(q, ct).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<RuleDataInfo>> GetVersionsAsync(RuleDataKey key, CancellationToken ct)
    {
        string tierPath = RuleTierPaths.Format(key.TierPath);
        await using var c = _connections.Create();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var q = Command(c, null, $"SELECT {Columns} FROM {Table(c)} WHERE rule_set = {P(c, "s")} AND stream_hash = {P(c, "h")} "
            + $"AND kind = {P(c, "k")} AND code = {P(c, "c")} AND tier_path = {P(c, "p")} ORDER BY version DESC");
        AddStream(c, q, key, tierPath);
        var records = await ReadRecordsAsync(q, ct).ConfigureAwait(false);
        var b = ImmutableArray.CreateBuilder<RuleDataInfo>(records.Length);
        foreach (var r in records) b.Add(r.Info);
        return b.MoveToImmutable();
    }

    public async ValueTask<RuleDataRecord?> GetAsync(RuleDataKey key, long version, CancellationToken ct)
    {
        string tierPath = RuleTierPaths.Format(key.TierPath);
        await using var c = _connections.Create();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var q = Command(c, null, $"SELECT {Columns} FROM {Table(c)} WHERE rule_set = {P(c, "s")} AND version = {P(c, "v")} "
            + $"AND stream_hash = {P(c, "h")} AND kind = {P(c, "k")} AND code = {P(c, "c")} AND tier_path = {P(c, "p")}");
        AddStream(c, q, key, tierPath);
        Add(c, q, "v", version);
        var records = await ReadRecordsAsync(q, ct).ConfigureAwait(false);
        return records.IsEmpty ? null : records[0];
    }

    public async ValueTask<RuleDataRecord?> GetByVersionAsync(string ruleSet, long version, CancellationToken ct)
    {
        await using var c = _connections.Create();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var q = Command(c, null, $"SELECT {Columns} FROM {Table(c)} WHERE rule_set = {P(c, "s")} AND version = {P(c, "v")}");
        Add(c, q, "s", ruleSet);
        Add(c, q, "v", version);
        var records = await ReadRecordsAsync(q, ct).ConfigureAwait(false);
        return records.IsEmpty ? null : records[0];
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Stable 64-bit id of a stream (kind, code, tier path) — the index key; collisions are harmless (exact columns compared).</summary>
    internal static long StreamHash(RuleDataKind kind, string code, string tierPath)
    {
        var h = StableHash64.Create(0x52554C45);   // "RULE"
        h.Append((int)kind);
        h.Append(code);
        h.Append(tierPath);
        return unchecked((long)h.Finish());
    }

    private string Table(YFexConnection c) => c.Dialect.NormalizeIdentifier(_options.TableName);

    private static string P(YFexConnection c, string name) => c.Dialect.ParameterPrefix + name;

    private static DbCommand Command(YFexConnection c, DbTransaction? tx, string sql)
    {
        var cmd = c.Inner.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void Add(YFexConnection c, DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        // ODP.NET binds ":name" placeholders to parameters named WITHOUT the colon; the others accept the prefixed name.
        p.ParameterName = c.Dialect.ParameterPrefix == ":" ? name : c.Dialect.ParameterPrefix + name;
        p.Value = value ?? DBNull.Value;
        if (value is byte[]) p.DbType = DbType.Binary;
        else if (value is null) p.DbType = DbType.String;
        cmd.Parameters.Add(p);
    }

    private static void AddStream(YFexConnection c, DbCommand q, RuleDataKey key, string tierPath)
    {
        Add(c, q, "s", key.RuleSet);
        Add(c, q, "h", StreamHash(key.Kind, key.Code, tierPath));
        Add(c, q, "k", (short)key.Kind);
        Add(c, q, "c", key.Code);
        Add(c, q, "p", tierPath);
    }

    private static async Task<ImmutableArray<RuleDataRecord>> ReadRecordsAsync(DbCommand q, CancellationToken ct)
    {
        var b = ImmutableArray.CreateBuilder<RuleDataRecord>();
        await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            // Columns order = Columns constant.
            var key = new RuleDataKey(r.GetString(0), (RuleDataKind)Convert.ToByte(r.GetValue(2), CultureInfo.InvariantCulture), r.GetString(3),
                RuleTierPaths.Parse(r.GetString(4)));
            var info = new RuleDataInfo(key, Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                DateOnly.ParseExact(r.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.IsDBNull(7) ? null : r.GetString(7),
                DateTimeOffset.ParseExact(r.GetString(8), "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                r.IsDBNull(9) ? null : r.GetString(9));
            b.Add(new RuleDataRecord(info, (byte[])r.GetValue(10)));
        }
        return b.ToImmutable();
    }

    private static long AsLong(object? scalar) => scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
