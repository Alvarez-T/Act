using YFex.Data;
using SchemaTable = YFex.Data.TableSchema;   // YFex.Rules.TableSchema (rule tables) is another type

namespace YFex.Rules.Data;

/// <summary>
/// The table of <see cref="SqlRuleStore"/> as a portable YFex.Data <see cref="SchemaModule"/>: one row per published version, primary key
/// <c>(rule_set, version)</c>, the stream index <c>(rule_set, stream_hash, version)</c>. The connection's dialect writes the DDL for its
/// engine. Apply it with <c>connection.ApplySchemaAsync(RuleStoreSchema.Module)</c> (or <see cref="SqlRuleStore.EnsureSchemaAsync"/>),
/// script it for a DBA with <c>dialect.ScriptSchema(RuleStoreSchema.Module)</c>, or register it in a host with
/// <c>services.AddYFexSchemaModule(RuleStoreSchema.Module)</c> (applied at startup only with <c>AddYFexSchemaMigration</c>).
/// </summary>
public static class RuleStoreSchema
{
    /// <summary>The default table name (<see cref="SqlRuleStoreOptions.TableName"/>).</summary>
    public const string DefaultTable = "yfex_rule_data";

    private const string ModulePrefix = "yfex.rules";

    private static readonly Lock Gate = new();
    private static SchemaModule[] _others = [];

    /// <summary>Module <c>yfex.rules</c>: the table <see cref="DefaultTable"/>. Version 1's checksum is pinned in the tests.</summary>
    public static SchemaModule Module { get; } = Create(DefaultTable);

    /// <summary>
    /// The module of a store whose table is <paramref name="table"/>: <see cref="Module"/> for <see cref="DefaultTable"/>, otherwise
    /// <c>yfex.rules.&lt;table&gt;</c> (lower-case, <c>_</c> written as <c>-</c>), so two stores with their own tables keep separate
    /// version lines in one database. The same table always returns the same instance.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="table"/> is not a portable identifier, or too long for a module name.</exception>
    public static SchemaModule ForTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (string.Equals(table, DefaultTable, StringComparison.OrdinalIgnoreCase)) return Module;
        SchemaModuleName name = ModuleName(table);
        lock (Gate)
        {
            foreach (SchemaModule known in _others)
                if (known.Name == name) return known;
            SchemaModule module = Create(table);
            _others = [.. _others, module];
            return module;
        }
    }

    /// <summary>The table itself, named <paramref name="table"/> (its key and index names follow it).</summary>
    public static SchemaTable Table(string table = DefaultTable) => SchemaTable.Create(table, t => t
        .Column("rule_set", ColumnType.String(200))
        .Column("version", ColumnType.Int64)
        .Column("kind", ColumnType.Int16)
        .Column("code", ColumnType.String(200))
        .Column("tier_path", ColumnType.String(2000))
        .Column("stream_hash", ColumnType.Int64)
        .Column("effective_from", ColumnType.String(200))          // yyyy-MM-dd
        .Column("published_by", ColumnType.String(200), nullable: true)
        .Column("published_at", ColumnType.String(200))            // round-trip UTC ("O")
        .Column("note", ColumnType.String(2000), nullable: true)
        .Column("payload", ColumnType.Binary())
        .PrimaryKey("rule_set", "version")
        .Index("ix_" + table + "_stream", "rule_set", "stream_hash", "version"));

    private static SchemaModule Create(string table)
        => new SchemaModule(ModuleName(table)).Version(1, "Published rule data (validation 05 §4.3)", v => v.CreateTable(Table(table)));

    private static SchemaModuleName ModuleName(string table)
        => string.Equals(table, DefaultTable, StringComparison.OrdinalIgnoreCase)
            ? SchemaModuleName.Parse(ModulePrefix)
            : SchemaModuleName.Parse(string.Concat(ModulePrefix, ".", table.ToLowerInvariant().Replace('_', '-')));
}
