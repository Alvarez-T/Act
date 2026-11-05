using System.ComponentModel.DataAnnotations;
using YFex.Data;

namespace YFex.Rules.Data;

/// <summary>
/// One row of <see cref="RuleStoreSchema.DefaultTable"/> as a YFex.Data <c>[DataTable]</c> row class (data-schema DS-P4 pilot): the table
/// <b>as it is now</b>. YFex.Data.Generator emits <c>Schema</c>, <c>Columns</c>, <c>BulkTable</c> and <c>BulkMapper</c>. The module's version 1
/// (<see cref="RuleStoreSchema.Table"/>) stays the frozen fluent definition; the tests keep both equal with <c>SchemaAssert.InSync</c>, so a
/// property added here fails them with the version to add.
/// </summary>
[DataTable(RuleStoreSchema.DefaultTable)]
[DataIndex("ix_" + RuleStoreSchema.DefaultTable + "_stream", nameof(RuleSet), nameof(StreamHash), nameof(Version))]
internal sealed partial record RuleDataRow(
    [property: Key, StringLength(200)] string RuleSet,
    [property: Key] long Version,
    RuleDataKind Kind,
    [property: StringLength(200)] string Code,
    [property: StringLength(2000)] string TierPath,
    long StreamHash,
    [property: StringLength(200)] string EffectiveFrom,     // yyyy-MM-dd
    [property: StringLength(200)] string? PublishedBy,
    [property: StringLength(200)] string PublishedAt,       // round-trip UTC ("O")
    [property: StringLength(2000)] string? Note,
    byte[] Payload);
