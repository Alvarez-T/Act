# 09 — Server side: YFex.Cqrs & YFex.Messaging

> Load for: **Phase 3** (sections 1–5, 7, 9–11) and **Phase 4** (sections 6, 8 — rules enforcement).
> Existing code: `YFex/src/Cqrs/YFex.Cqrs/Configuration/*ConfigurationBuilder.cs`, `Runtime/CommandPolicy.cs`,
> `Runtime/QueryPolicy.cs`, `CompiledMessagingRegistry.cs`, `Error.cs`, `ErrorType.cs`, `ConflictPolicy.cs`;
> `YFex/src/Messaging/YFex.Messaging.Rpc/LocalDispatcher.cs`, `FusionMessageBus.cs`, `HandlerInterfaces.cs`,
> `OutboxReplayer.cs`. Read `docs/internal/cqrs-internals.md` and `messaging-internals.md` first.
> **Phase 3 implementers:** read the "Phase 3 handoff" at the end of 13 first — the as-built code differs from what
> this file assumes (the server does not validate RPC calls today, the Fusion client drops the server `Error`,
> type-ref validators never run, the RPC wire format is unverified) and four new decisions (Q-A…Q-D) must be asked.
> Convention (YFex/CLAUDE.md): CQRS behavior is configured in `IAggregateConfiguration<T>`
> (baseline / server overrides / client overrides); records carry only marker interfaces.

> **As built (Phase 3, 2026-09-29) — differences from the text below:**
> - Stage 1 = `MessagePipeline.CheckShape<T>` (ValidationSlot, runtime fluent rules as fallback); queries are first coerced
>   (`MessagePipeline.Coerce`, generated `T.Coerce`). Stage 5 = `MessageValidator<T>` on `CommandPolicy/QueryPolicy.Validation`
>   (the `Func<object,…> Validate` members are gone). Both live in `YFex.Cqrs/Runtime/MessagePipeline.cs`.
> - Server entry (D25): `ValidatingHandlerInvoker` (stages 1 + 2 + 5); rejections are thrown as `ErrorResultException` and turned
>   into the typed result by the generated contract impl.
> - Stage 2 server-side (D29, 2026-09-30): the caller + tenant are an implicit `CallContext` (YFex.Messaging.Rpc) resolved per RPC
>   call from pluggable sources (`FromConnection()` default, `OrFromHeader`, `OrFromSession`, `.WithTenant()`); every dispatcher
>   authorizes `CallContext.Current ?? IPrincipalAccessor.Current`. For RPC, stage 2 runs in `CallContextMiddleware` BEFORE Fusion's
>   compute cache (a compute query must not serve one caller's cached value to another) and generated compute queries carry a
>   server-filled `variant` argument (per tenant, + user for user/session/custom scopes). Configs are unchanged: `.RequirePermission(...)`
>   declared once in the baseline is checked client-side (UX, `FusionMessageBus`) and enforced server-side.
> - RPC contracts (D30): the CQRS generator also emits the Fusion proxies (contract, `I{Aggregate}RpcClient` view, impl) —
>   apps declare nothing in source. Generated commands are regular RPC methods (no CommandR `[CommandHandler]`).
> - `.Validation(...)` semantics: `Severity`/`Code` modify the previous rule; `When`/`Profile` apply to every rule before them.
>   `Each<TItem>` returns the `ValidationBuilder` (no `EachRuleBuilder`). `With<T>` and `Range(coerce: true)` are compiled-only.
> - Obsolete validators: `UseValidator<TValidator>()` (not `Validate<TValidator>()` — overloads can't differ by constraint).
> - FluentValidation adapter call site: `.UseFluentValidator(new V())` (compiler limitation, see 13).
> - Wire (D26): Fusion default format verified = MemoryPack `mempack6`; formatters in `YFex.Messaging.Rpc/Serialization`.
>   ProblemDetails (§5) deferred to the ASP.NET host. `SyncFailure.Violations` carries replay rejections (§8).
> - Handlers (§9): `IValidatedCommandHandler<TCmd,TResult>` / `<TCmd>` resolved by the typed invoke; `IRuledCommandHandler` = Phase 4.

---

## 1. Pipeline stages (order is normative)

```
Receive (dispatcher: LocalDispatcher / FusionMessageBus / Wolverine adapter)
 1. Shape       generated Validate via ValidationSlot<T> (sync; free on success)      → ValidationProblem
 2. Authorize   existing authorizers (RequirePermission/Roles/…)                     → Unauthorized
 3. Gather      ISituationLoader<TCmd, TS>.LoadAsync (only when .UseSituation)       → NotFound/Fail
 4. Rules       RuleSetHost<TS> → Evaluate → Denied / NeedsApproval / fingerprint     → ValidationProblem / ApprovalRequired / conflict
 5. Context     IContextRule<TCmd>[] sequentially (async I/O: stock, uniqueness)      → ValidationProblem
 6. CustomLogic tenant overrides (YFex.CustomLogic.Cqrs) — unchanged position before the handler
 7. Handler     receives Valid<TCmd> (+ Decision<TS> when opted in, §9)
```

Why this order:
- **Shape before auth** is safe (pure, reveals nothing) and cheap.
- **Authorize before gather/context rules**: a uniqueness check ("email already exists") or loading
  a customer can leak existence to an unauthorized caller.
- **Rules before context rules**: rules are pure/cheap; context rules do I/O.

## 2. Fluent configuration API (D14, ⚑P2)

Added to `CommandConfigurationBuilder<TCommand>`, `CommandConfigurationBuilder<TCommand, TResult>`
and `QueryConfigurationBuilder<TQuery, TResult>`:

```csharp
/// Shape rules for the message (same semantics as attributes; parsed at compile time by
/// YFex.Validation.Generator when arguments are constants — 03 §4). Must live in the BASELINE
/// configuration (shared by client and server) or you get client/server drift (11 §2).
public TBuilder Validation(Action<ValidationBuilder<TMessage>> rules);

/// Async server context rule (DI-constructed through a generated factory — no Activator).
public TBuilder Validate<TRule>() where TRule : class, IContextRule<TMessage>;
/// Inline context rule.
public TBuilder Validate(Func<TMessage, ViolationCollector, CancellationToken, ValueTask> rule);

/// Enables stages 3–4: load a situation, evaluate a rule set, enforce the decision.
/// `map` translates situation field paths → message paths when names differ (default: identical names).
public TBuilder UseSituation<TSituation, TLoader>(Action<SituationPathMap>? map = null)
    where TSituation : ISituation<TSituation> where TLoader : class, ISituationLoader<TMessage, TSituation>;
public TBuilder Enforce<TSituation>(RuleSetDefinition<TSituation> ruleSet, EnforceOptions? options = null)
    where TSituation : ISituation<TSituation>;

/// What to do when the message carries a decision fingerprint (IDecisionBound) that differs from the
/// server's evaluation — typical for offline replay (§8).
public TBuilder OnDecisionMismatch(DecisionMismatchPolicy policy);
```

```csharp
/// Builder used inside .Validation(v => …). Member lambdas are plain Func (AOT-safe) + the
/// expression text via [CallerArgumentExpression] for the path (same trick as YFex.UI.Markup .Bind()).
public sealed class ValidationBuilder<T>
{
    public FieldRuleBuilder<T, TMember> Field<TMember>(Func<T, TMember> member, [CallerArgumentExpression(nameof(member))] string expr = "");
    public EachRuleBuilder<T, TItem> Each<TItem>(Func<T, IReadOnlyList<TItem>> member, [CallerArgumentExpression(nameof(member))] string expr = "");
    public ValidationBuilder<T> Invariant(InvariantFn<T> invariant, [CallerArgumentExpression(nameof(invariant))] string expr = "");
}
public delegate void InvariantFn<T>(in T value, ref ViolationWriter w);

public sealed class FieldRuleBuilder<T, TMember>
{
    public FieldRuleBuilder<T, TMember> Required(); public FieldRuleBuilder<T, TMember> NotEmpty();
    public FieldRuleBuilder<T, TMember> Length(int min, int max); public FieldRuleBuilder<T, TMember> Length(int max);
    public FieldRuleBuilder<T, TMember> Range(TMember min, TMember max, bool coerce = false);
    public FieldRuleBuilder<T, TMember> Pattern([StringSyntax("Regex")] string pattern);
    public FieldRuleBuilder<T, TMember> Email();
    public FieldRuleBuilder<T, TMember> With<TRule>();                       // IValueRule / ITextRule / IAsyncValueRule
    public FieldRuleBuilder<T, TMember> With<TRule, TArg>(TArg arg);
    public FieldRuleBuilder<T, TMember> When(Func<T, bool> condition, [CallerArgumentExpression(nameof(condition))] string expr = "");
    public FieldRuleBuilder<T, TMember> Severity(Severity s);
    public FieldRuleBuilder<T, TMember> Code(RuleCode code);
    public FieldRuleBuilder<T, TMember> Profile(string profile);
}
```

### 2.1 Example

```csharp
public sealed class CustomerBaseline : IAggregateConfiguration<Customer>          // shared (client + server)
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
        => b.Command<Customer.Commands.CreateCustomer, CustomerId>()
            .Validation(v =>
            {
                v.Field(c => c.Name).Required().Length(2, 80);
                v.Field(c => c.Email).Required().Email();
                v.Field(c => c.Document).Required().With<CpfOrCnpj>();
                v.Invariant(static (in Customer.Commands.CreateCustomer c, ref ViolationWriter w) =>
                { if (c.Age < 18 && c.Document.Length == 14) w.Add(Customer.Commands.CreateCustomer.Fields.Document, "customer.minor-cnpj"); });
            })
            .RequirePermission(Perms.Customers.Create);
}

public sealed class CustomerServerOnly : IAggregateConfiguration<Customer>        // server override (I/O)
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
        => b.Command<Customer.Commands.CreateCustomer, CustomerId>()
            .Validate<EmailNotRegistered>();
}
```

## 3. Where each thing goes (config layers)

| Layer | Put here | Never put here |
|---|---|---|
| **Baseline** (client+server) | `.Validation(...)`, `.UseSituation`, `.Enforce`, `.RequirePermission` | I/O context rules |
| **Server overrides** | `.Validate<TContextRule>()` (stock, uniqueness, FK) | shape rules (clients wouldn't know them → drift) |
| **Client overrides** | UX-only additions (e.g. a warning-severity rule) | loosening any rule (analyzer YFV0023) |

## 4. Runtime: typed slots instead of `Func<object, …>`

```csharp
/// Static generic holder filled by a generated [ModuleInitializer] in every assembly that has
/// validatable messages. A function pointer: no delegate allocation, no boxing, no casts.
///   ValidationSlot<CreateCustomer>.Set(&CreateCustomer.Validate);
public static unsafe class ValidationSlot<T>
{
    private static delegate*<in T, ref ViolationWriter, bool> s_validate;
    public static bool IsSet => s_validate != null;
    public static void Set(delegate*<in T, ref ViolationWriter, bool> fn) => s_validate = fn;
    public static bool Validate(in T value, ref ViolationWriter w) => s_validate == null || s_validate(in value, ref w);
}
```

Dispatchers are generic (`CommandAsync<TCommand, TResult>`), so stage 1 is
`ValidationSlot<TCommand>.Validate(in cmd, ref w)` — no policy lookup. Context rules: the compiled
policy stores `ContextRuleFactory<TCommand>[]` as `object`; the generic dispatcher reinterprets once
with `Unsafe.As<ContextRuleFactory<TCommand>[]>(policy.ContextRules)` (justified unsafe, README #4).
`CommandPolicy.Validate`/`QueryPolicy.Validate` (`Func<object,…>`) are removed.

```csharp
/// Generated per registered context rule type: creates the rule from DI without Activator.
public sealed class ContextRuleFactory<T>(Func<IServiceProvider, IContextRule<T>> create)
{
    public IContextRule<T> Create(IServiceProvider sp) => create(sp);
}
```

Existing API kept (adapted):
- `Validate(Func<TCommand, CancellationToken, ValueTask<ValidationResult>>)` → wrapped as a context rule.
- `Validate<TValidator>() where TValidator : ICommandValidator<TCommand>` → `[Obsolete]`, adapted.
- `UseFluentValidator<T>()` → moved to **`YFex.Cqrs.FluentValidation`** (extension method with the
  same signature; converts FluentValidation failures to `Violation` with `Detail` = message,
  `Code` = FluentValidation error code). YFex.Cqrs loses the FluentValidation package reference.

## 5. Errors on the wire

```csharp
// Error.cs changes
public readonly struct Error
{
    public ErrorType Type { get; }                      // null-safe (fix default(Error) NRE; no LINQ)
    public string Message { get; }                      // first violation code / text — backward compatible
    public ViolationList Violations { get; }            // structured (new)
    public ApprovalRequestList Approvals { get; }       // for ErrorType.ApprovalRequired (new)
    public static Error Validation(in ValidationResult result);
    public static Error ApprovalRequired(in ApprovalRequestList approvals, ulong fingerprint);
}
public enum ErrorType { None, NotFound, Fail, Unauthorized, ValidationProblem, Conflict, ApprovalRequired /* new */ }
```

- `LocalDispatcher`/`FusionMessageBus`: replace `FormatErrors(...)` with `Error.Validation(result)`
  (`CacheableQueryResult<T>.ValidationProblem(...)` and the command results get an overload taking `Error`).
- RPC: YFex.Messaging.Rpc serializes with **MemoryPack** — add formatters for `Violation`,
  `ViolationList`, `FieldPath`, `RawValue`, `RuleCode` (source-generated, AOT-safe).
- HTTP (ASP.NET, when used): ProblemDetails with both
  `errors` (ASP.NET-compatible `{ "Name": ["text…"] }`, messages resolved with the request culture for
  non-YFex clients) **and** `violations` (structured, for YFex clients):

```json
{ "type": "validation", "status": 400,
  "violations": [
    { "path": "Installments", "code": "rules.limit.max", "severity": "error", "args": [1] },
    { "path": "Lines[3].Quantity", "code": "stock.out", "severity": "error", "args": [2] } ],
  "errors": { "Installments": ["Pix: somente 1x"], "Lines[3].Quantity": ["Estoque insuficiente (2 disponíveis)"] } }
```

Clients localize from `violations` (10) and map paths to fields (`ApplyServer`, 07 §7).

## 6. Rules enforcement stage (stage 4)

```csharp
public sealed class EnforceOptions
{
    public TraceLevel Trace { get; init; } = TraceLevel.Off;   // Applied/Full when auditing
    public bool RequireFingerprint { get; init; }              // IDecisionBound messages must carry one
}

/// Messages that carry the client's decision (offline-capable commands). The client fills these from
/// Decision.RulesetVersion / Fingerprint when it built the command.
public interface IDecisionBound
{
    RulesetVersion RulesetVersion { get; }
    ulong DecisionFingerprint { get; }
    OverrideToken? Approval { get; }                           // manager approval, when the decision needed one
}
```

Algorithm:
1. `s = await loader.LoadAsync(cmd, ct)`; `d = host.Resolve(scope(s), asOf(s)).Evaluate(s, options)`.
2. `Denied` → `Error.Validation(d.Violations mapped situation→message paths)`.
3. `NeedsApproval` → valid `OverrideToken` for every required permission and **this** fingerprint?
   yes → continue; no → `Error.ApprovalRequired(d.Approvals, d.Fingerprint)`.
4. `IDecisionBound` and `cmd.DecisionFingerprint != d.Fingerprint` → apply `DecisionMismatchPolicy` (§8).
5. Continue; `d` is available to the handler (§9) and its `Adjustments` must be persisted by it (06 §2).

**Override tokens:** issued by `RuleAdmin.Commands.ApproveDecision(permission, fingerprint, situationHash)`
executed with the approver's credentials; HMAC-signed server-side (approver id, permission,
fingerprint, expiry). Offline approval (manager PIN on a disconnected POS) = device-signed token,
verified and flagged on replay — **open question for implementation** (security review needed).

## 7. Queries

- `Validation(...)` on queries works the same; `Range(..., coerce: true)` **clamps** (paging) instead
  of failing; clamping happens before the cache key is computed.
- An invalid query **never reaches the cache/Fusion** and is never cached.
- Queries can `.UseSituation(...).Enforce(...)` to **return** decisions for UIs that don't evaluate
  locally (web, thin clients):

```csharp
public sealed partial record GetSaleOptions(SaleSituation Situation) : IQuery<SaleOptionsView>, ICacheable;
// handler: evaluates and returns limits/options/explanation; cache key = situation hash + ruleset version;
// invalidated by RulesetPublished (05 §4.4) through the existing invalidation groups / Live state.
```

## 8. Offline outbox & fingerprints

- **Enqueue time (client, offline):** stage 1 (shape) runs before enqueueing; invalid commands are
  returned as `Error` immediately and **never enqueued**. `IDecisionBound` commands carry the local
  decision's version + fingerprint.
- **Replay (server):** normal pipeline. As-of evaluation uses the command's business date, so a sale
  made before a table change is evaluated with the old version → same fingerprint → accepted.
- **Mismatch** (the device used a stale version, or tampering):

```csharp
public enum DecisionMismatchPolicy : byte
{
    AcceptAndFlag,  // keep the CLIENT's decision (honour what the customer saw), write an audit/sync-failure entry — retail default
    Escalate,       // treat as sync failure → existing ISyncFailureLog (same as ConflictPolicy.Escalate)
    Reject,         // return ValidationProblem with the server's violations
    UseServer,      // silently apply the server decision (only for internal, non-customer-facing flows)
}
```

- **Rejections after replay** surface through the existing outbox failure path (`ISyncFailureLog`)
  with the structured `Error` (violations), so the app's "sync issues" screen can show them with
  messages and paths.

## 9. Handlers (⚑P7 — opt-in, existing interfaces unchanged)

```csharp
// YFex.Messaging.Rpc/HandlerInterfaces.cs — additions
public interface IValidatedCommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(Valid<TCommand> command, CancellationToken ct);
}
public interface IRuledCommandHandler<TCommand, TResult, TSituation>
    where TCommand : ICommand<TResult> where TSituation : ISituation<TSituation>
{
    Task<TResult> HandleAsync(Valid<TCommand> command, Decision<TSituation> decision, CancellationToken ct);
}
// Alternative for existing handlers: inject scoped IDecisionAccessor<TSituation> { Decision<TSituation>? Current { get; } }
```

The handler-registration generator (`YFexLocalHandlerRegistrations`, `LocalHandlerInvoker`) learns the
new shapes; `Valid<T>` is created by the pipeline after stage 1 (internal factory in YFex.Validation
exposed to YFex.Cqrs via `[InternalsVisibleTo]` or a dedicated `ValidationPipeline.Seal(in T)` API).

## 9.1 Events
Validate at **trust boundaries** only: the producer validates before publishing, and messages entering
from outside the process (RPC/HTTP/outbox replay) pass the pipeline. Internal hops (in-process
subscribers of an already-validated event) do not re-validate.

## 10. Tenancy
Scope keys for rule resolution come from the **situation** (`[Scope]` fields filled by the loader from
YFex.Tenancy / Cqrs.Tenancy context) — never from ambient state inside rules — so decisions stay
replayable.

## 11. Tests (Phase 3/4)
- Stage order (auth before context, rules before context) with spies.
- Structured violations over LocalDispatcher, FusionMessageBus (MemoryPack round-trip), HTTP ProblemDetails.
- Invalid query not cached; coerce before cache key.
- Offline: invalid never enqueued; replay same fingerprint accepted; mismatch per policy; failure log entry.
- Approval: missing token → ApprovalRequired; valid token for other fingerprint → rejected.
- YFex.Cqrs has no FluentValidation reference; adapter package tests.
