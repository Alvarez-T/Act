# 03 — Capabilities everywhere: search bar, command bars, AI, forms

> Decisions ⚑SK3, ⚑SK5, ⚑SK6, ⚑SK13–⚑SK15, ⚑SK26–⚑SK27, ⚑SK40–⚑SK41. All code is a *proposed* API.

A capability is "something that can be run". Today the manifest describes and gates it, but nothing runs it except Jobs'
own invoker. This file adds the missing half: a stable identity, one invoke path, context for view-model commands, and the
standard CRUD/form commands.

## 1. Surfaces and what each needs

| Surface | Lists | Runs | Needs |
|---|---|---|---|
| Universal search box / command palette (`YSearchBox`, `YCommandPalette`) | catalog hits + record hits + record actions | the chosen hit | search provider (built), invoker (⚑SK6), active surfaces (⚑SK14) |
| Page command bar (`YCommandBar`) | capabilities of the active page | button click | `ByOwner(vmType)` (built), invoker, `CanExecute` from the view model |
| AI agent (YFex.Automation, ⚑SK27) | `ListActions` (built) | tool call with JSON arguments | stable id (⚑SK5), schema (built), invoker, `find_record` tool (02 §6) |
| Forms / CRUD (⚑FM23, ⚑FM44) | New, Save, Delete… | same as command bar | standard CRUD capabilities (⚑SK15) |
| Admin API, scripts | any | JSON | invoker |

## 2. Stable capability id (⚑SK5)

`CapabilityKey.ToString()` is `"{Kind}:{OwnerType.Name}#{Member}"`: two `CreateCommand` types in different modules collide,
and the text cannot be turned back into a key. Frecency, AI tool names, audit records and telemetry need a stable text id.

```csharp
// before (built)
key.ToString()                       // "CqrsCommand:ApproveOrder"
// after (proposed): canonical, parseable through the registry, authored override when a type is renamed
CapabilityId id = key.Id;            // "cqrs.command/acme.orders.approve-order"  (kind prefix + kebab full name)
registry.TryGet(id, out CapabilityKey key);
// rename protection, optional:  .Capability(id: "orders.approve")  on the profile / CQRS builder
```

Cons: ids derived from full names change with a namespace move unless an id is authored; the AI tool-name length limit
(64 chars on some providers) needs a short form (`orders.approve`), so the authored id is recommended for every capability
exposed to AI.

## 3. One invoke path (⚑SK6)

```csharp
// YFex.Capabilities.Public (⚑SK3)
public interface ICapabilityInvoker
{
    ValueTask<CapabilityResult> InvokeAsync(CapabilityKey key, CapabilityArguments arguments,
        IAuthorizationContext caller, InvocationOrigin origin, CancellationToken ct);
}
public readonly struct CapabilityArguments { /* Json(string) | Typed(object message) | None */ }
public enum InvocationOrigin : byte { User, SearchBox, CommandBar, Agent, Api }   // → Actors origin (⚑SK26)

// [Union] non-boxing struct: Completed(json?) | Accepted(deferred, offline queued) | Invalid(ValidationResult)
//                            | Denied(AuthorizationVerdict) | NeedsConfirmation | NeedsApproval | Failed(error)
public readonly struct CapabilityResult { /* … */ }

// Each source that can run its capabilities registers one handler; the invoker dispatches by CapabilityKind.
public interface ICapabilityHandler
{
    bool Handles(CapabilityKind kind);
    ValueTask<CapabilityResult> InvokeAsync(in CapabilityInvocation invocation, CancellationToken ct);
}
```

| Kind | Handler | Package | Notes |
|---|---|---|---|
| `CqrsCommand`, `CqrsQuery` | builds the message from JSON (generated `JsonTypeInfo` per message, request ⚑SK40) and dispatches it | `YFex.Capabilities.Cqrs` | the CQRS pipeline re-checks policy, validates, applies rules and approvals, audits |
| `Job` | adapts the built `JobCapabilityInvoker` | `YFex.Jobs.Capabilities` | better placement: the Jobs invoker becomes this handler, its result mapped to `CapabilityResult` |
| `View`, `Route` | navigates through NavigatR (`IRoute` from the route type + arguments) | `YFex.Capabilities` | |
| `ViewModelCommand` | runs the command on the **live** instance from active surfaces (§4) | `YFex.Capabilities` | needs `[StateCommand]` parameters for arguments (request to State, ⚑SK41) |
| Messaging RPC, API endpoint | later, with their sources (⚑SK37, ⚑SK38) | | |

`ICapabilityAgentApi.CanInvoke` stays the pre-check that decides whether to ask the user (confirmation / approval). The
invoker never replaces the server gate: the dispatched message is authorized again by its own pipeline.

Cons: one more contract and a handler per source; the JSON path needs per-message `JsonTypeInfo` (AOT); a typed path
(`Typed(message)`) exists for in-process callers like the command bar.

## 4. View-model commands are context-bound (⚑SK14)

A `ViewModelCommand` capability (`OrderEditorViewModel#SaveCommand`) can only run on an open screen. Proposal: an
`IActiveSurfaces` service fed by the NavigatR page lifecycle (`YPageHost` activation, docked pages, dialogs) lists the live
view-model instances, most recent first.

- Search box / palette: view-model commands appear only for active owners (ranked up for the focused page); routes and CQRS
  commands appear everywhere.
- AI agent: `ListActions(activeOwner)` describes the screen the user is on; invoking runs against that instance.
- Without an active instance, a view-model command is not offered (no "navigate first, then run").

Rejected: listing every view-model command globally (unrunnable hits); dropping them from search (loses "salvar" on the
open screen).

## 5. Form and CRUD commands as capabilities (⚑SK15, Forms ⚑FM44)

The CRUD base (`CrudViewModel<TRow, TModel, TId>`, forms 02, ⚑FM40) owns `New`, `Save`, `Delete`, `Duplicate`, `Export`,
`Refresh`. Writing a capability profile for each of them on every screen is the boilerplate this ecosystem avoids.

```csharp
// before: per screen, by hand
public sealed class CustomersCapabilities : ICapabilityProfile<CustomersViewModel>
{
    public void Configure(ICapabilityBuilder<CustomersViewModel> vm)
    {
        vm.Describe("Clientes", ai: "Lista e cadastro de clientes.");
        vm.Command(x => x.NewCommand).SideEffect(SideEffect.ReadOnly).Keywords("novo cliente", "cadastrar cliente");
        vm.Command(x => x.SaveCommand).SideEffect(SideEffect.Mutating).Requires(Permission.Parse("customers.edit"));
        vm.Command(x => x.DeleteCommand).SideEffect(SideEffect.Irreversible).Requires(Permission.Parse("customers.delete"));
        // … Duplicate, Export, Refresh
    }
}

// after: nothing per screen. A CRUD capability source contributes the standard set for every CrudViewModel:
//   display     "{verb} {record label}"  from catalog keys: Crud.New = "Novo {0}", Record.customer = "cliente"
//   keywords    standard verb synonyms per culture (novo, cadastrar, incluir / new, add …) + the record label
//   side effect New/Refresh/Export ReadOnly, Save Mutating, Delete Irreversible (confirmation from the gate)
//   policy      the policy of the CQRS message the command dispatches (Create/Update/Delete commands): no new permission
//   owner       the CRUD view model (context-bound, §4); "Novo cliente" from anywhere = Route capability + New on open
```

A screen can still add or override entries with a normal profile (`vm.Command(x => x.ExportCommand).Hidden()`).

The **command bar** binds to the page's capabilities (`ByOwner`) filtered by visibility; enabled state comes from the
command's `CanExecute`, not from Capabilities. **Search** finds "novo cliente" globally: the route capability of the
Customers screen plus a "then run New" follow-up — the only allowed "navigate then run" case, and only for the CRUD `New`
(read-only side effect).

Cons: the CRUD source must know which message each standard command dispatches (`Save` → create or update): the base class
declares it (`protected override ICommand Save(...)` already returns the message type; the generator or a static map
records the types).

## 6. Record actions — verb + subject (⚑SK13)

```
 query "histórico maria"
   verb dictionary = capability keywords ("histórico") → capabilities whose subject is CustomerId or OrderId
   subject = rest ("maria") → IRecordLookup<CustomerId>.SearchAsync
   result  "Histórico — Maria Souza" → RunCapability(CustomerHistory, { customerId: "customer:42" })
```

Capabilities indexes capabilities whose one required parameter implements `IRecordId<T>` (from `ParameterDoc.Type` or the
validation shape): `IAiDescribeRegistry.BySubject(Type idType)`. Built once at `Build`, no per-query scan.

## 7. AI agent (⚑SK27)

YFex.Automation runs AI agent steps. Proposal: its tool catalog for app actions is the capability manifest — `ListActions`
(per active owner and system-wide), `ParametersSchema` as the tool input schema, `CanInvoke` before asking the user, the
invoker to run, and `find_record` (02 §6) to resolve names. Automation keeps its desktop/input tools; it does not keep a
second registry of app actions.

## 8. Audit and actors (⚑SK26)

An invocation carries `InvocationOrigin`. The invoker opens an Actors origin scope ("agent on behalf of user U",
"search box") so the CQRS/Jobs pipeline that executes it stamps and audits the change with that origin. Capabilities writes
no audit records of its own.
