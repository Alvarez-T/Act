# 09 — UI integration and correlation with YFex.Logging

UI is the **last** phase (same rule as validation and logging: YFex.UI needs its reusable table/grid design first). This file fixes the
integration points now so the server API and view models are shaped for them.

## 1. Questions the UI must answer

| Question | Where | API |
|---|---|---|
| "Who changed this product?" | edit page of any entity → History panel | `AuditQueries.GetEntityHistory` |
| "Who changed *prices* last week?" | audit explorer | `AuditQueries.Search` (`EntityType`, `FieldPath`, range) |
| "What did this user do today?" | user admin page / audit explorer | `Search` (`ActorId`, `IncludeAsRealActor`) |
| "What needs my review?" | admin dashboard | `GetReviewQueue` |
| "Is our audit intact?" | admin dashboard | `GetVerificationStatus` |
| "What happened on this POS while it was offline?" | POS screen, offline | local journal query (§7) |

## 2. The history panel ("who changed this?")

**👁 Result** (Produto edit page → History)

```
Histórico — Produto 7891234567895 (Dipirona 500mg)                                 ✔ verificado até 30/09
────────────────────────────────────────────────────────────────────────────────────────────────────────
⚠ Alterações locais não salvas (não auditadas):  Preço 12,00 → 12,50
01/10 10:41  João Silva            Produto.AlterarPreco      ok
             Preço           R$ 10,00 → R$ 12,00
             Motivo: "reajuste fornecedor"                                            #4f1c9a2e  ⛓ selado
01/10 09:02  sup-02 como João      Produto.AlterarDescricao  ok          (personificação de usuário)
             Descrição       "Dipirona 500" → "Dipirona 500mg"                       #9b0e11d4  ⛓ selado
30/09 18:15  Gerente Ana           RuleSet desconto          ok          aprovação A-77 (offline, caixa 3)  ⚑ revisar
             DescontoMaximo  10% → 15%
29/09 22:00  job estoque.reprocessar  Produto.Changed (implícito)   ok
             Situacao        Ativo → Inativo
28/09 14:03  João Silva            Produto.AlterarPreco      solicitado offline 14:03 · executado 16:40
```

- **Names are resolved at read time** (⚑A29) by `IAuditActorDirectory` (app-provided: users table, device registry, job catalog, agent names).
  Entries store only subject ids, so renaming or erasing a person never touches the chain. An erased person renders as "usuário removido (#h:…)".
- Values come as `RawValue` + treatment and are formatted on the client by type and culture. Protected values show their mask unless the
  viewer has `audit.reveal` and clicks "revelar" (which is itself audited, 08 §7).
- Offline-queued + executed entries with the same `IdempotencyKey` render as **one** line (03 §6.2). Intent + outcome render as one line (05 §4).
- Continuation entries (04 §6) are merged. Amendments (05 §2.3) render under the entry they amend.
- Field labels are localized through the YFex.Localization catalog layers used by validation messages (`audit.field.Produto.Preco` →
  "Preço"), falling back to the member name.

## 3. Components

```csharp
// (sketch) YFex.Audit.Client (view models; YFex.State + YFex.Mvvm, no UI framework reference)
public sealed partial class EntityHistoryViewModel : ViewModel
{
    [Observable] public partial AuditTarget? Target { get; set; }                   // set by the host page
    [Observable] public partial bool ShowAccessEntries { get; set; }               // meta-audit hidden by default
    [Live]       public partial AuditHistoryPage? Page { get; }                      // GetEntityHistory, refreshes when new entries are sealed
    [Observable] public partial UndoContext? LocalEdits { get; set; }               // optional: unsaved overlay (§4)
    [StateCommand] Task LoadMoreAsync(CancellationToken ct);
    [StateCommand] Task RevealAsync(AuditEntryId id, CancellationToken ct);       // needs audit.reveal; audited
    [StateCommand] Task ExportAsync(CancellationToken ct);                         // partial .yaudit for this entity (audit.export)
}
public sealed partial class AuditReviewQueueViewModel : ViewModel { /* GetReviewQueue, MarkReviewed */ }
public sealed partial class AuditExplorerViewModel : ViewModel { /* Search with AuditFilter, verification summary */ }
```

```xml
<!-- YFex.UI (Avalonia first; Blazor by the convert-to-blazor skill) -->
<y:YEntityHistory Target="{Binding Produto.Id, Converter={x:Static audit:AuditTargets.FromKey}}" />
```

- `YEntityHistory` is a templated control over `EntityHistoryViewModel`; it uses the YFex.UI design tokens and the reusable list/grid.
- **No per-page boilerplate:** a page view model that already has `[ValidatesAs<TCommand>]` exposes its target through the same convention as
  03 §2.3 (`AuditTargets.FromKey` converts any entity-key value object into an `AuditTarget` using the generated entity name). A generic
  "History" toolbar button can be added once in the app shell for any page that exposes an entity key.

## 4. Unsaved local edits overlay (State.History)

When the page uses YFex.State.History, the panel shows the current `UndoContext.UndoHistory` above the audited list, labelled **"not saved,
not audited"**. It disappears at the savepoint (`SetSavepoint` after a successful save). This is display only (04 §7): nothing from
State.History is ever written to the audit trail.

## 5. Review queue and badges

| Badge | Meaning | Source |
|---|---|---|
| ⛓ selado | entry sealed in a verified chain | seals + verifications |
| ⏳ pendente | received, not sealed yet (seconds) / on device, not shipped | inbox / local journal |
| ⚑ revisar | `NeedsReview`: flagged rule decision, offline device approval, out-of-band DB change, coalesced denials above a threshold | flags |
| ⚠ resultado desconhecido | two-phase intent without outcome (dangling) | `intents_open` |
| ✖ verificação falhou | chain broken at or before this entry | verifications |
| ⌁ lacuna | the producing device has an open gap | gaps |

The review queue lists `NeedsReview` entries, dangling intents and open gaps; `MarkReviewed` / `CloseGap` require `audit.review` and are audited.

## 6. Correlation with YFex.Logging (present or absent)

**With YFex.Logging installed:**

- The entry's `TraceId`/`OperationId` are the logging trace's ids (shared `TraceContext`, YFex core).
- At `Complete`, the engine calls `global::YFex.Diagnostics.Operations.Mark(AuditMarks.Recorded, entryId)` (core probe; no reference to
  YFex.Logging). LoggY shows `audited #4f1c9a2e` on the operation, linking to the audit explorer when configured.
- The history panel's entry menu shows **"open trace"** when an `IAuditTraceLinkProvider` is registered (the logging server URL pattern).
  Only users with logging permissions follow it.
- Audit failures (journal full, spill, verification failure, gaps) are also reported as `Critical` log records and alert hooks.

**Without YFex.Logging:**

- Trace ids are still generated per root audited operation (05 §1), so entries of one command across services still correlate.
- Audit failures go to the audit self-log (stderr / Windows Event Log), the review queue and the verification status. Nothing depends on logging.

```
// 👁 LoggY trace, logging installed
14:02:11.476 INF [cmd] Produto.AlterarPreco server   ok  24.1ms  acme/loja7 u:joao   ┐ trace 4bf92f35…
14:02:11.481 DBG [sql] UPDATE produto RETURNING OLD   ok   3.2ms rows=1             │
14:02:11.483     ◆ audited #4f1c9a2e (catalog, 1 target, 1 change)                   ┘
```

## 7. Offline devices (POS)

- `ILocalAuditQuery` (YFex.Audit) reads the device journal: the POS can show "what happened on this terminal today" with badges ⏳ (not shipped)
  / ⛓ (sealed by the server) even while offline.
- The local view is limited to the device's own entries (it holds nothing else) and to `LocalRetention` (05 §3.2).
- When the journal approaches its cap (05 §3.1), the POS shows a persistent warning "conecte para sincronizar a auditoria".
