# 12 — UI state, State.History and the user's edit timeline

## 1. The problem

A screen changes dozens of properties per second: selection, scroll position, `IsBusy`, filters, computed totals, values loaded
from the server, and the values the user is actually typing. Logging all of them is noise and a privacy risk. Logging none
leaves you blind to "what did the user do before clicking Finalizar?".

The design separates them **automatically**, using signals already present in YFex.State, YFex.State.History,
YFex.Validation and YFex.Messaging code. The developer writes no logging code in view models.

## 2. Roles: inferred at compile time

```csharp
namespace YFex.Diagnostics;

/// Closed set: the engine decides retention/level per role.
public enum StateChangeRole : byte
{
    ScreenState = 0,   // local to the screen: selection, scroll, expanded panels, filters, IsBusy. Not logged
    UserInput   = 1,   // data the user is entering and will submit: edit timeline
    ServerData  = 2,   // values that came from queries/live/ApplyServer: covered by the query operation, not logged per property
    Derived     = 3,   // [Computed]: never logged
    UserAction  = 4,   // [StateCommand], undo/redo, navigation, dialog result: operations (trace roots)
}
```

| Signal already in your code | Role | How it is logged |
|---|---|---|
| `[Computed]` | `Derived` | never (the generator emits nothing) |
| `[Undoable]` property / `[UndoableCollection]` | `UserInput` | through **State.History** (§3): already coalesced, batched, undo-aware |
| property bound to a command field: `[ValidatesAs<TCommand>]` name match or `[MapsTo("…")]` | `UserInput` | setter hook, coalesced on the consumer (same 300 ms window as History) |
| `[Observable]` with none of the above | `ScreenState` | **nothing** (no code emitted; counted per screen in session stats) |
| `[Live]` property, `[ConstraintsFrom]` decisions, values set by `ApplyServer<T>` | `ServerData` | not per property; the query/live operation records it |
| `[StateCommand]` | `UserAction` | operation, trace root (04 §4) |
| Undo, Redo, savepoint, navigation boundary | `UserAction` | operation / mark (§3) |
| `[Route]` navigation | `UserAction` | navigation operation + breadcrumb |

Overrides (rare): `[StateLog(StateChangeRole.UserInput)]` on a property the inference got wrong, or `[NoLog]`.

### 2.1 Run-time refinement: who changed it?

The same `UserInput` property can be changed by the user (binding), by a command (clearing the form after saving), by the
server (`ApplyServer`) or by undo. The generated setter hook asks the ambient context (one static read) and records accordingly:

| Ambient at the moment of the set | Recorded as |
|---|---|
| no operation running, UI thread | **user edit** (the binding wrote it) |
| inside a `StateCommand` operation | side effect of that command (Debug, attached to the command; **not** an edit) |
| inside a `Query`/`Live` operation or `ApplyServer` | `ServerData` (dropped) |
| inside an undo/redo | recorded by History as undo/redo, not as a new edit |

That is how "what is local to the screen" and "what is part of a command" separate without annotations.

## 3. State.History integration

History already solves the hard parts of an edit timeline. Typing "Hello" coalesces into **one** delta (300 ms merge window).
`BeginUpdate()` batches become **one** group. Collection deltas describe item added/removed/moved. Savepoints mark "saved".
Navigation boundaries scope the page. Logging reuses that instead of hooking every setter.

**New seam (proposal, ⚑L16) in `YFex.State.History`:** an observer on `UndoManager`, zero cost when none is registered:

```csharp
namespace YFex.State.History;

public interface IUndoHistoryObserver
{
    void OnCommitted(UndoContext context, UndoHistoryEntry entry);   // after the merge window closes / the group ends
    void OnUndone(UndoContext context, UndoHistoryEntry entry);
    void OnRedone(UndoContext context, UndoHistoryEntry entry);
    void OnSavepoint(UndoContext context);                           // "saved": closes the current edit session
    void OnBoundary(UndoContext context, bool pushed);               // page entered/left
}
// registered once: undoManager.AddObserver(observer)  — AddYFexLogging registers the logging adapter automatically
```

- Undoable properties are recorded **only** through this observer (the setter hook skips them, so nothing is recorded twice).
- An entry is emitted when the merge window closes, but carries the **first** change time.
- Undo/redo are user actions: `Undo: Desconto 10 → 5`.

## 4. The edit timeline: what you get, and when it is shipped

- Edits are `UserInput` marks at **Debug**, written into a per-session **edit ring** (last 200 per screen) in the flight recorder
  (03 §5). They cost encoding only.
- When a `UserAction` starts (e.g. `Caixa.Finalizar`), its operation gets a summary (`edits=7 since=10:39:58 screen=Caixa`) and
  a reference to that ring segment.
- The edits **are shipped with the trace only** when the action fails, is slow, is `[Audited]`, a scoped verbose grant is active,
  or the user presses "Enviar diagnóstico". Otherwise they are discarded when the screen closes.

**👁 Result** (LoggY, trace of a failed `Caixa.Finalizar`)

```
Screen Caixa · session 3fa2 · joao @ acme/loja7
10:39:58.120  edit      Cliente        ∅ → #h:9f2c41d07a3be1c2
10:40:03.441  edit      Itens          + Item(Dipirona 500mg ×2)
10:40:05.002  edit      Itens          + Item(Soro 500ml ×1)
10:40:11.730  edit      Desconto       0 → 10                    (coalesced: 3 changes)
10:40:12.904  undo      Desconto       10 → 5
10:40:14.010  edit      FormaPagamento Dinheiro → Cartao
10:40:16.300  action    Caixa.Finalizar  faulted 48.0ms  → trace 4bf92f35 (server: DbUpdateException @ Estoque.Baixar)
              screen state changes not logged: 41 (selection 18, scroll 20, panels 3)
```

## 5. Dev writes vs generated

**✍ Dev writes** (nothing logging-related)

```csharp
[Route(typeof(CaixaRoute)), ValidatesAs<Venda.Commands.FinalizarCommand>]
public partial class CaixaViewModel : PageViewModel
{
    [Observable, Undoable] public partial decimal Desconto { get; set; }        // UserInput (Undoable)
    [Observable]           public partial Cpf? Cliente { get; set; }            // UserInput (matches FinalizarCommand.Cliente)
    [Observable]           public partial ItemVenda? Selecionado { get; set; }  // ScreenState
    [Observable]           public partial bool PainelAberto { get; set; }       // ScreenState
    [Computed]             public decimal Total => Itens.Sum(…);                 // Derived
    [Live]                 public partial EstoqueDto? Estoque { get; }          // ServerData

    [StateCommand] async Task FinalizarAsync(CancellationToken ct) => …;        // UserAction
}
```

**⚙ Generated** (State generator; only `Cliente` gets a hook, since `Desconto` goes through History and the others are not logged)

```csharp
public partial global::YFex.Entity.Cpf? Cliente
{
    get => _cliente;
    set
    {
        if (!__SetField(ref _cliente, value, __Prop.Cliente)) return;          // existing State logic
        if (global::YFex.Diagnostics.StateChanges.IsEnabled(__CaixaState.Cliente))   // one byte load; false when logging is off
            global::YFex.Diagnostics.StateChanges.Record(this, __CaixaState.Cliente, in value);   // role resolved per §2.1; Cpf hashed
    }
}

// descriptor table emitted once per view model (also part of the reviewed LogSchema snapshot)
file static class __CaixaState
{
    internal static readonly global::YFex.Diagnostics.StateDescriptor Cliente =
        global::YFex.Diagnostics.StateDescriptor.Register<CaixaViewModel, global::YFex.Entity.Cpf?>(
            "Caixa.Cliente", global::YFex.Diagnostics.StateChangeRole.UserInput, maps: "FinalizarCommand.Cliente");
}
```

```
# LogSchema.verified.txt (excerpt)
CaixaViewModel (screen Caixa)
  Desconto      decimal     UserInput    via history
  Cliente       Cpf?        UserInput    setter, hash       → FinalizarCommand.Cliente
  Selecionado   ItemVenda?  ScreenState  not logged
  PainelAberto  bool        ScreenState  not logged
  Total         decimal     Derived      not logged
  Estoque       EstoqueDto  ServerData   via live operation
  Finalizar     command     UserAction   operation (root)
```

## 6. Privacy and cost

- Values follow the type's `[Sensitive]` (Cpf hashed), strings are scrubbed, and password-like properties (name heuristic or
  bound to a password box) are erased. **No keystrokes**: only coalesced values, because History merges them.
- Edits stay on the device unless one of the §4 conditions holds. The edit timeline counts as operational diagnostics of the
  app's own data, not behaviour tracking. Behaviour (clicks per minute, active windows) remains YFex.Profiling's, consent-gated.
- Cost: `ScreenState`/`Derived` properties get **no emitted code**. A `UserInput` setter costs one byte load when disabled.
