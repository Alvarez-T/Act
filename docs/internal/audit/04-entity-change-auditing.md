# 04 — Entity change auditing (`[AuditChanges]`)

"Who changed the price of this product from 10 to 12?" is the most frequent audit question (logging 09 §2.2). It needs **field-level
before/after** captured where the authoritative write happens, with no reflection and no per-member attributes.

## 1. Who knows "before" and "after"

| Layer | Knows before? | Knows after? | Authoritative? | Role in audit |
|---|---|---|---|---|
| YFex.Data (SQL, server or local DB) | via a before-image (§3.2) | the row being written | **yes** | primary source (generated hooks, phase 5) |
| YFex.EventSourcing | the events **are** the change | events + folded state | **yes** (server stream) | events listed per command; optional state diff (§5) |
| YFex.Persistence (`[Persist]`, key-value snapshots on devices) | previous snapshot | new snapshot | for device-local settings only | optional (§9) |
| Database itself (triggers) | `OLD` | `NEW` | yes, including changes made outside the app | optional safety net (§4) |
| YFex.State.History (view models) | yes | yes | **no** (client, unsaved, forgeable) | not a source (§7) |

## 2. `[AuditChanges]` → generated `IAuditChanges<T>` (⚑A7)

### 2.1 Contract (core, `YFex.Auditing`)

```csharp
/// <summary>Generated for every [AuditChanges] type. Static abstract: no boxing, no virtual call, no reflection.</summary>
public interface IAuditChanges<TSelf> where TSelf : IAuditChanges<TSelf>
{
    static abstract AuditEntityDescriptor Entity { get; }                      // entity type name, fields (name, sensitivity, treatment)
    static abstract AuditTarget TargetOf(in TSelf value);                      // entity type + canonical key text
    static abstract void Created(in TSelf after, ref ChangeWriter writer);     // all audited fields as after-values
    static abstract void Updated(in TSelf before, in TSelf after, ref ChangeWriter writer);  // ONLY differing fields; nothing if equal
    static abstract void Deleted(in TSelf before, ref ChangeWriter writer);    // all audited fields as before-values
}

public sealed class AuditEntityDescriptor
{
    public string EntityType { get; }                          // "Produto"
    public ReadOnlySpan<FieldSpec> Fields { get; }             // YFex.Diagnostics FieldSpec: name + DataSensitivity
    public int Version { get; }                                // bumps when the audited field set changes (snapshot diff)
}

/// <summary>
/// Writes targets and changes into the current scope's pooled buffer, already in canonical form (02 §9).
/// Every overload is primitive or generic-constrained: nothing boxes. Values are classified by type (YFex.Privacy) and
/// protected by the engine on encode (08 §2), so generated code never decides encryption itself.
/// </summary>
public ref struct ChangeWriter
{
    public void Reference(in AuditTarget target);                          // ChangeKind.None
    public void BeginTarget(in AuditTarget target, ChangeKind kind);
    public void EndTarget();

    public void Changed(ReadOnlySpan<char> path, long before, long after);  // + int, short, byte, bool, decimal, double, Guid,
                                                                            //   DateTime, DateTimeOffset, DateOnly, TimeOnly overloads
    public void Changed(ReadOnlySpan<char> path, string? before, string? after, DataSensitivity sensitivity);
    public void ChangedValue<T>(ReadOnlySpan<char> path, in T before, in T after) where T : IRawValue<T>;        // Money, Quantity, ids…
    public void ChangedClassified<T>(ReadOnlySpan<char> path, in T before, in T after)
        where T : IRawValue<T>, global::YFex.Privacy.IClassifiedData<T>;                                         // Cpf, Email…
    public void ChangedEnum<TEnum>(ReadOnlySpan<char> path, TEnum before, TEnum after) where TEnum : unmanaged, Enum;  // name + value
    public void ChangedLarge(ReadOnlySpan<char> path, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after);      // Digest (§2.3)
    public void Set(ReadOnlySpan<char> path, …);                           // Created/Deleted single-sided overloads (same set)
    public void ItemAdded(ReadOnlySpan<char> path, …); public void ItemRemoved(ReadOnlySpan<char> path, …);
    public void ItemMoved(ReadOnlySpan<char> path, int fromIndex, int toIndex);
    public bool IsFull { get; }                                            // entry cap reached → continuation (§6)
}
```

### 2.2 ✍ Dev writes / ⚙ generated

```csharp
// ✍ once on the type — YFex.Data entity (an exact copy of the table, model-layering rule)
[AuditChanges, Table("PRODUTO")]
public sealed partial class ProdutoEntity
{
    public ProdutoId Id { get; set; }                 // key → target "Produto/{Id}"
    public string Descricao { get; set; } = "";
    public Money Preco { get; set; }
    public Percentual DescontoMaximo { get; set; }
    public SituacaoProduto Situacao { get; set; }     // enum
    public Ncm Ncm { get; set; }                       // value object, Public
    public DateTimeOffset AlteradoEm { get; set; }    // excluded by convention (timestamp of the row itself)
    public uint Versao { get; set; }                   // excluded by convention (concurrency token)
}
```

```csharp
// ⚙ generated (YFex.Audit.Generator) — sketch
partial class ProdutoEntity : global::YFex.Auditing.IAuditChanges<ProdutoEntity>
{
    public static global::YFex.Auditing.AuditEntityDescriptor Entity => __AuditProdutoEntity.Descriptor;

    public static global::YFex.Auditing.AuditTarget TargetOf(in ProdutoEntity v)
        => new("Produto", __AuditProdutoEntity.Key(v.Id));                         // canonical key text, cached formatter

    public static void Updated(in ProdutoEntity b, in ProdutoEntity a, ref global::YFex.Auditing.ChangeWriter w)
    {
        if (!string.Equals(b.Descricao, a.Descricao, global::System.StringComparison.Ordinal))
            w.Changed("Descricao", b.Descricao, a.Descricao, global::YFex.Privacy.DataSensitivity.Internal);
        if (b.Preco != a.Preco) w.ChangedValue("Preco", in b.Preco, in a.Preco);    // Money : IRawValue<Money>, no boxing
        if (b.DescontoMaximo != a.DescontoMaximo) w.ChangedValue("DescontoMaximo", in b.DescontoMaximo, in a.DescontoMaximo);
        if (b.Situacao != a.Situacao) w.ChangedEnum("Situacao", b.Situacao, a.Situacao);
        if (b.Ncm != a.Ncm) w.ChangedValue("Ncm", in b.Ncm, in a.Ncm);
        // AlteradoEm, Versao: excluded by convention (see AuditSchema.verified.txt)
    }
    // Created / Deleted: same members, single-sided
}
```

The `BeginTarget`/`EndTarget` pair is written by the caller (`AuditScope.Updated<T>`), so `Updated` stays a straight line of comparisons.
Equality uses the type's own `==` / `IEquatable<T>` (devirtualized for structs), ordinal for strings.

### 2.3 Member rules (convention, reviewed in the snapshot)

| Member | Treatment |
|---|---|
| numbers, bool, dates, Guid, enums | value (enum by name + value) |
| core values (`Money`, `Percentual`, `Quantity`, `Mass`, `Length`) and app value objects with `IRawValue<T>` | value |
| value objects classified by YFex.Privacy (`Cpf`, `Email`, `Telefone`…) | value, protected per sensitivity on encode (08 §2) |
| raw `string` ≤ 4 KiB | value; personal-looking names classified by Privacy convention (YFA0021) |
| raw `string` > 4 KiB, `byte[]`, `ReadOnlyMemory<byte>`, XML/JSON documents | **Digest** (SHA-256 + length): proves *which* content without storing it |
| collections of entities with a key (`List<ItemVenda>` where `ItemVenda` has an id) | diffed **by key**: `ItemAdded` / `ItemRemoved` / `ItemUpdated` (nested field diff, depth 2) |
| collections without a key | diffed by index; `ItemMoved` only for reorders of keyed items |
| nested records/DTOs | flattened paths (`Endereco.Cidade`), depth 2, then `Omitted` |
| concurrency tokens (`[Timestamp]`, `[ConcurrencyCheck]`, names `Versao`/`Version`/`RowVersion`/`ConcurrencyStamp`/`xmin`) | **excluded** |
| self-timestamps (`AlteradoEm`/`UpdatedAt`/`ModifiedAt`/`LastModified*`) | **excluded** (the entry has its own time) |
| `Secret`/`Financial` classified members | `Erased` / `Tokenized` — the entry says *that* it changed |
| `[NotAudited]` on a member | excluded (the rare exception; YFA0030 when the category forbids exclusions) |
| `[NotAudited]` on a type | the type is never captured (e.g. a cache entity) |

### 2.4 The reviewed snapshot

`AuditSchemaSnapshot.Verify(typeof(ProdutoEntity).Assembly)` (one test, `YFex.Audit.Testing`) writes/compares
`AuditSchema.verified.txt`:

```
# AuditSchema.verified.txt (committed; a diff shows up in code review)
entity Produto  ← ProdutoEntity  (YFex.Data PRODUTO)  version 3
  Id              ProdutoId        key
  Descricao       string           Internal           clear
  Preco           Money            Internal           clear
  DescontoMaximo  Percentual       Internal           clear
  Situacao        SituacaoProduto  Public             enum name+value
  Ncm             Ncm              Public             clear
  AlteradoEm      DateTimeOffset   —                  excluded (self-timestamp)
  Versao          uint             —                  excluded (concurrency token)

action Produto.AlterarPreco  (cqrs.command, category catalog → retention standard)
  target          Id → Produto                         convention (entity key)
  reason          Motivo                               configured
  NovoPreco       Money            Internal           clear
  Motivo          string           Internal           scrubbed text
```

Adding `string CpfComprador` to the entity changes the file to `CpfComprador string PersonalIdentifier (convention) encrypted(subject?)`,
so the reviewer sees it. Nobody has to remember an attribute.

## 3. YFex.Data (⚑A9)

### 3.1 Where the hook goes

YFex.Data has **no update API today** (raw SQL through `Execute`). Its adapter design (stage 2) adds generated row shapes
(`IDataRow<T>`: table, columns, key) and `BulkInsertAsync<T>` / `UpdateAsync<T>` / `MergeAsync<T>`. The audit hook is emitted **by the
YFex.Data generator** inside those generated writes, calling the core contract only:

```csharp
// (sketch) emitted by YFex.Data.Generator when T is [AuditChanges] — compile-time decision, no runtime check for other types
public static async ValueTask<int> UpdateAsync(this YFexConnection c, ProdutoEntity after, CancellationToken ct)
{
    if (!global::YFex.Auditing.AuditScope.IsInstalled)                  // static bool: YFex.Audit not registered → plain update
        return await __ProdutoRow.UpdateAsync(c, after, ct).ConfigureAwait(false);

    var before = await __ProdutoRow.UpdateReturningOldAsync(c, after, ct).ConfigureAwait(false);   // §3.2
    if (before is null) return 0;
    global::YFex.Auditing.AuditChanges.Updated(in before, in after);    // attaches to AuditScope.Current or creates an implicit entry (§8)
    return 1;
}
```

### 3.2 Before-image per dialect

| Dialect | Update | Delete | Merge (upsert) |
|---|---|---|---|
| PostgreSQL ≥ 18 | `UPDATE … RETURNING OLD.*` (one round trip) | `DELETE … RETURNING *` | `INSERT … ON CONFLICT … DO UPDATE … RETURNING OLD.*` (null `OLD` = created) |
| PostgreSQL < 18 | `SELECT … FOR UPDATE` then `UPDATE` in the same transaction | `DELETE … RETURNING *` | lock-and-read then upsert |
| SQL Server | `UPDATE … OUTPUT DELETED.*` | `DELETE … OUTPUT DELETED.*` | `MERGE … OUTPUT $action, DELETED.*` |
| Oracle | `SELECT … FOR UPDATE` then `UPDATE` | `DELETE … RETURNING … INTO` | lock-and-read then `MERGE` |
| SQLite | `SELECT` then `UPDATE` inside the write transaction (single writer → consistent) | `DELETE … RETURNING *` (3.35+) | read then upsert |

New dialect capability (YFex.Data, proposed to its owner): `IBeforeImageDialect { BeforeImageSupport Support { get; } … }`. When the
caller already holds the original row (loaded for editing), the generated `UpdateAsync(c, before, after, ct)` overload skips the
before-image read entirely; the row's key and concurrency token still guard the write.

### 3.3 What is not covered

- **Raw SQL** (`connection.Execute("UPDATE PRODUTO SET …")`) is invisible to the generator. YFA0050 flags the obvious cases; the trigger
  mode (§4) catches the rest when enabled. This is documented as pitfall 10 §2.4.
- **Bulk updates** of audited entities through `IBulkWriter` are inserts only (`Created` targets); one entry per write call, continuation
  entries beyond the cap (§6).

## 4. Optional database trigger mode (⚑A10)

For tables where the law or the customer requires "every change, even by a DBA", `YFex.Audit.Data` can install generated triggers
(Postgres, SQL Server):

```csharp
services.AddYFexAuditServer(s => s.UsePostgres(cs).Triggers(t => t.For<ProdutoEntity>().For<PrecoTabelaEntity>()));
```

- The trigger writes `(table, operation, key, to_jsonb(OLD), to_jsonb(NEW), session context)` into `audit.out_of_band` (insert-only).
- App writes set a transaction-local flag (`SET LOCAL yfex.audit_app_write = 'on'`, pushed the same way YFex.Data pushes row-security
  session values), so triggers **skip** writes the app already audited with its typed diff. Only out-of-band writes are recorded.
- The sequencer converts each out-of-band row into a canonical entry: action `"{Entity}.ChangedOutOfBand"`, actor `System` with the
  database user name as id, flags `OutOfBand | NeedsReview`. The review queue shows them (09 §5).
- Cost: one trigger per audited table; the app path pays one `current_setting` per row.

## 5. YFex.EventSourcing (⚑A11)

Events are already the immutable record of change. Audit adds **who** and **why**, and links each command's events to one entry.

```csharp
// proposed addition to YFex.EventSourcing (MemoryPack: appended members are compatible)
[MemoryPackable]
public sealed partial record EventRecord(EventId Id, StreamVersion Version, string EventTypeName, byte[] Payload,
    DateTimeOffset At, GlobalPosition? GlobalPos = null)
{
    public EventMetadata? Metadata { get; init; }
}

[MemoryPackable]
public sealed partial record EventMetadata(
    Guid? AuditEntryId,          // the entry of the command that produced the event
    string? ActorId, byte ActorKind, string? RealActorId,
    string? TenantPath,
    byte[]? TraceId, ulong OperationId,  // causation: the operation that appended
    Guid? CausationEventId);     // event that triggered this command (process managers, projections that emit)
```

- The `[AggregateCommandHandler]` generated body fills `Metadata` from `AuditScope.Current` / `ActorContext.Current` / `TraceContext`.
  With Marten, the provider maps it to Marten's `CorrelationId`/`CausationId`/headers.
- An **audited** aggregate command produces one entry: target = the stream (`AggregateType/Id`), fields = the emitted events captured by
  their own descriptors (`events[0] Pedido.ItemAdicionado {produto, quantidade}`), and, if the aggregate **state** type is
  `[AuditChanges]`, the generated `Updated(before, after)` over the state folded before and after the new events (the session has both).
- Local device streams can be rewritten by `ReplaceAsync` during rebase, so device streams are never evidence; the server stream and the
  entries are.
- Optional `YFex.Audit.EventSourcing` package: helpers for the above + the Marten sealed-stream store (07 §5).

## 6. Large change sets

- Caps per entry: **256 targets**, **64 KiB** canonical size. `ChangeWriter.IsFull` tells generated loops to stop.
- Overflow continues in **continuation entries** (`AuditEntryFlags.Continuation`, `ParentId` = first entry, same action/actor/trace).
  The history panel and exports merge them back. A price import of 10 000 products → 1 + 39 entries, all in the same transaction.

## 7. Why State.History is not a source (⚑A12)

`UndoCaptureDelta` holds boxed old/new values of **view-model** properties on the client: they may never be saved, may be undone,
may differ from what the server stores after validation/rules, and can be forged by a modified client. Audit records what the
**authoritative write** did. State.History is used for:

- the **"unsaved local edits"** overlay at the top of the history panel (09 §4), clearly labelled as not audited;
- the logging edit timeline (logging 12), shipped with failing/slow/audited traces as **diagnostics**, not as audit.

## 8. Implicit entries (⚑A8)

A write of an `[AuditChanges]` entity with no audited scope open (a job, a background sync, an unaudited command, a migration script
that uses the generated API) is **never lost**: `AuditChanges.Updated(...)` opens an implicit scope with action `"{Entity}.Changed"`,
category `data-change`, the ambient actor (`ActorContext` → `IAuditActorSource` → `System`), flag `Implicit`. If the same transaction
later completes an audited scope, the implicit one is merged into it instead.

## 9. YFex.Persistence (optional)

Device-local configuration (`[Persist]` state objects: printer settings, POS parameters) can opt into `[AuditChanges]`. The
`PersistenceService` save path compares the previous snapshot with the new one through the same generated `Updated` and writes into the
device journal. Low priority (phase 5), because these values are usually not records of business transactions.
