# 03 — Permissions, caller context and evaluation

## 1. Catalogs (⚑Z2: no `Values.` — the catalog declares `Permission` properties)

```csharp
// ✍ Dev writes (once per module)
[PermissionCatalog]
public static partial class SalesPermissions
{
    [Permission("sales.discount.apply", Name = "Aplicar desconto")]
    public static partial Permission DiscountApply { get; }

    [Permission("sales.discount.override", Name = "Autorizar desconto acima do limite")]
    public static partial Permission DiscountOverride { get; }

    [Permission("sales.void", Name = "Cancelar venda", Description = "Cancela uma venda já finalizada")]
    public static partial Permission SaleVoid { get; }
}
```

```csharp
// ⚙ Generated
public static partial class SalesPermissions
{
    public static partial Permission DiscountApply    => s_discountApply;
    public static partial Permission DiscountOverride => s_discountOverride;
    public static partial Permission SaleVoid         => s_saleVoid;
    private static readonly Permission s_discountApply    = new(PermissionKey.Declare("sales.discount.apply"));   // interned → Ordinal
    private static readonly Permission s_discountOverride = new(PermissionKey.Declare("sales.discount.override"));
    private static readonly Permission s_saleVoid         = new(PermissionKey.Declare("sales.void"));

    /// <summary>Ids as constants — for attribute arguments only ([RequirePermission(SalesPermissions.Ids.SaleVoid)]).</summary>
    public static class Ids
    {
        public const string DiscountApply = "sales.discount.apply";
        public const string DiscountOverride = "sales.discount.override";
        public const string SaleVoid = "sales.void";
    }

    public static FrozenDictionary<PermissionKey, Permission> All { get; } = …;
    public static AuthorizationManifest Manifest { get; } = …;              // id → name, description, declaring member

    [ModuleInitializer] internal static void Register() => PermissionCatalogs.Register(Manifest);   // no manual cfg.AddCatalog(...)
}
```

| Where | Before | After |
|---|---|---|
| code (configuration, rules, checks) | `SalesPermissions.Values.SaleVoid` | `SalesPermissions.SaleVoid` |
| attribute arguments (view models, `[StateCommand]`, records) | `[RequirePermission(SalesPermissions.SaleVoid)]` (the const) | `[RequirePermission(SalesPermissions.Ids.SaleVoid)]` |

- Ids are **explicit** and never derived from the property name: they are persisted in tokens, role tables and snapshots, so renaming
  a property must not change them. AUTH001–AUTH004 keep checking duplicates, grammar, unknown attribute literals and misplaced
  attributes (now: `[Permission]` on something that is not a `static partial Permission` property of a `[PermissionCatalog]` class).
- `[Permission(...)]` replaces `[PermissionMeta]` (⚑Z4, "Meta" is an abbreviation). C# 13 partial properties (net11, LangVersion
  preview) — the property body is generated, so the declaration has no initializer.
- Library catalogs plug in the same way: `PrivacyPermissions` (privacy 08 §10), `AuditPermissions` (`audit.read`, `audit.read.all`),
  `LoggingPermissions` (`logging.verbose.enable`), `RuleAdminPermissions` (§6), `AuthorizationPermissions` (`tenant.impersonate`,
  `user.impersonate`, `authorization.grants.issue`, `authorization.roles.manage`).
- "Both options" for lower libraries (⚑Z2): Rules APIs take the typed `Permission` (`d.RequireApproval(SalesPermissions.DiscountOverride, …)`);
  runtime-authored JSON rule specs keep `"permission": "sales.discount.override"` and are resolved against declared ids at publish (§6).

## 2. Interned ordinals and `PermissionSet` (hot path, ⚑Z10)

Today `HasPermission` is a `FrozenSet<PermissionKey>` lookup (hash + ordinal string compare). Target: a **bit test**.

- `PermissionKey.Declare(string)` (`.Public`) validates, canonicalizes and **interns** the id in a process-wide, lock-free-read table
  (same shape as `ScopeValueInterner`/`RawType`), giving a dense `Ordinal`. Only catalogs call `Declare`.
- `PermissionKey.TryParse` keeps working but does **not** intern. It looks the id up among declared keys, so `Ordinal` is −1 for
  unknown ids. An attacker-supplied claim like `permission=x.y.z` can never grow the table, and an undeclared id is never granted.
  Unknown ids from tokens are dropped and counted (one diagnostic per id).
- Ordinals are **process-local** (catalog registration order differs per app). They are never persisted or sent. Snapshots and wire
  formats carry ids (05 §2).

```csharp
// (sketch) YFex.Authorization.Public — immutable, allocation-free reads
public readonly struct PermissionSet : IEquatable<PermissionSet>
{
    private readonly ulong[]? _words;                       // bit i = PermissionKey with Ordinal i
    public bool Contains(in PermissionKey key)
        => (uint)key.Ordinal < (uint)(_words?.Length ?? 0) * 64 && (_words![key.Ordinal >> 6] & (1UL << key.Ordinal)) != 0;
    public bool ContainsAll(in PermissionSet required);     // word-wise (required & ~granted) == 0
    public PermissionKey FirstMissing(in PermissionSet required);
    public Enumerator GetEnumerator();                      // ids for snapshots/diagnostics
    public ref struct Builder { … }                         // pooled; Add(in PermissionKey) / Build()
}
```

### 2.1 Real usage examples (what changes in practice)

The developer writes the same checks as today (`caller.HasPermission(SalesPermissions.SaleVoid)`); what changes is how they run
and the two rules "only declared ids count" and "nothing is created at runtime".

1. **Cashier logs in at the POS (pharmacy).** Token claims `role=caixa, permission=sales.void, permission=Sales.Discount.Apply,
   permission=admin.everything`; role map `caixa → sales.sell, sales.discount.apply`. Resolved **once** at login into
   `{sales.sell, sales.discount.apply, sales.void}` (a few bits). `Sales.Discount.Apply` canonicalizes to the declared id.
   `admin.everything` is declared nowhere → dropped, one warning "undeclared permission id in token", never granted.
2. **Every command of the shift.** ~300 sales/hour × 3–5 checks (dispatch, button `CanExecute`, menu) = one bit test each; no
   allocation, no claims scan per check (today `IsInRole` scans claims every time).
3. **Search bar / AI agent.** Typing "cancel" filters 1 000 capabilities before ranking: 1 000 bit tests (~10 µs). The AI only sees
   actions the user can take.
4. **Admin "Roles" screen.** The assignable permissions are the catalog manifest (id + display name); typing a new permission is not
   possible. Saving bumps the version → logged-in users reload on their next call (05).
5. **Rules typo.** A rule JSON with `"permission": "sales.discount.overide"` is rejected **at publish** (`authorization.permission.unknown`);
   a code-declared rule with a typo fails **at startup**. Today it is accepted and the approval can never be given.
6. **Server and client number bits differently.** `sales.void` may be bit 7 on the server and bit 12 on the client (catalogs load
   in another order). Never a problem: ordinals never leave the process; snapshots carry ids and each side maps them.

Not allowed (⚑Z10): a tenant inventing a **new** permission at runtime. Tenants configure *which roles get which permissions* (data,
fully dynamic); new permissions come with new code features. If runtime-defined screens (Composer) need their own permissions, a
later "runtime catalog" (admin-only, size-limited, own id prefix) can be added.

## 3. The caller context

`IAuthorizationContext` (02 §5) is built **once per (subject, scope, permission version)**. It is never built per check or per
access:

| Host | Who builds it | Where it is cached |
|---|---|---|
| RPC server | `CallContextMiddleware` (transport) through `IAuthorizationContextFactory` | on the connection identity, invalidated by version (logic moved from `CallContextResolver`) |
| ASP.NET Core | `YFexAuthorizationMiddleware` (new, async, after `UseAuthentication`) | `HttpContext.Items` for the request + cross-request `AuthorizationContextCache` keyed by (subject, scope, version, token hash) |
| Desktop / POS | the app's login/session service → `AmbientPrincipalAccessor.SetCurrent(context)` | the accessor |
| RPC client | `SnapshotPrincipalAccessor` from `PermissionSnapshot` (05 §3) | the accessor, refreshed by Fusion invalidation |

`IPrincipalAccessor.Current` stays synchronous and only *reads* the cached context. That fixes D6.

### 3.1 Permission sources (replace `IPermissionProvider` + `IApproverDirectory`)

```csharp
// (sketch) YFex.Authorization.Public
public interface IPermissionSource
{
    /// <summary>Adds what <paramref name="request"/>'s subject holds in its scope. Called once per (subject, scope, version).</summary>
    ValueTask ContributeAsync(PermissionResolution request, PermissionSet.Builder builder, CancellationToken cancellationToken);
}
public readonly record struct PermissionResolution(ActorIdentity Subject, ClaimsPrincipal? Principal, ScopeIdentity Scope, DateTimeOffset Now);
```

Built-in sources (all in the runtime project): `RolePermissionMapSource` (today's `RolePermissionMap`, roles resolved once),
`PermissionClaimSource` (`permission` claims), `ScopedClaimSource` (`scoped_role`/`scoped_permission`, today's per-scope logic),
`TemporaryGrantSource` (06 §2). `YFex.Authorization.Data` later adds a SQL role-assignment source. `IPermissionDirectory`
(approver re-check, 06 §1) runs **the same sources** with `Principal = null`. Sources that need the token (claims) contribute nothing
for an absent user, so a claims-only deployment rejects offline approvals exactly like today.

## 4. Requirements and compiled policies

`Requirement` becomes a non-boxing `[Union]` tagged struct (D18). The cases mirror today's kinds plus what the consolidation adds:

| Case | Decided on the sync path? | Source of the requirement |
|---|---|---|
| `Authenticated` | yes | `.RequireAuthenticated()` |
| `Permission(PermissionKey)` | yes (bit test) | `.RequirePermission(p)`, `[RequirePermission]` |
| `AnyPermission(PermissionSet)` | yes | `.RequireAnyPermission(a, b)` |
| `Role(RoleList)` (any of) | yes | `.RequireRoles(...)` |
| `Claim(Claim)` | yes | `.RequireClaim(c)` |
| `Resource(ResourceRequirement)` — sync predicate over (caller, message/resource) | yes, when the resource is supplied | `.Authorize((caller, message) => …)`, `.Authorize<TRequirement>()` with a sync `IAuthorizationRequirement<T>` |
| `AsyncResource(…)` | no → hidden in visibility; decided by `AuthorizeAsync` | `.Authorize<TRequirement>()` with `IAsyncAuthorizationRequirement<T>` |
| `Policy(PolicyName)` | no → hidden; decided by `AuthorizeAsync` through `INamedPolicyHandler` | `.RequirePolicy(name)` |
| `Approval(Permission)` | yes (token present + valid) → else `ApprovalRequired` | `.RequireApproval(p)` (06 §1) |
| `Justification(JustificationKind)` | yes (reason attached) → else `JustificationRequired` | reveal of sensitive data (07 §1) |

`Policy` (a list of requirements) is compiled once at registry build into a `CompiledPolicy`:

```csharp
// (sketch) YFex.Authorization.Public
public sealed class CompiledPolicy
{
    public PolicyKey Key { get; }                     // message type, route type, (view model, command member), capability key
    internal PermissionSet RequiredAll;               // every Permission requirement folded into one mask
    internal bool RequiresAuthentication;
    internal Requirement[] Remaining;                 // roles, claims, resources, approvals… evaluated in this order
    public bool IsSyncOnly { get; }                   // no Policy/AsyncResource → AuthorizeAsync completes synchronously
    public bool IsEmpty { get; }
}
```

The fast path for the common case (authentication + permissions) is:
`!RequiresAuthentication || caller.IsAuthenticated` → `caller.Permissions.ContainsAll(RequiredAll)` → done. No allocation, no
delegate call.

## 5. The verdict (replaces `AuthorizationResult`, D15/D18)

```csharp
// (sketch) [Union] tagged struct — same shape as YFex.Rules.Verdict
[Union] public readonly struct AuthorizationVerdict : IUnion
{
    // cases: Allowed | Unauthenticated | Forbidden(Denial) | ApprovalRequired(ApprovalRequirementList) | JustificationRequired(JustificationKind)
    public AuthorizationVerdictKind Kind { get; }
    public bool IsAllowed { get; }
}
public readonly record struct Denial(DenialKind Kind, Permission Missing, PolicyName Policy, string? Code);
public enum DenialKind : byte { MissingPermission = 1, MissingRole = 2, MissingClaim = 3, PolicyDenied = 4, ResourceDenied = 5, RestrictedWhileImpersonating = 6, JustificationRequired = 7 }
```

### 5.1 The Cqrs `Error` becomes a `[Union]` (⚑Z11, Phase 2b)

Today `YFex.Cqrs.Error` is a flat struct: an `ErrorType` enum plus optional fields (`Violations`, `Approvals`,
`DecisionFingerprint`) that each mean something for one type only. Adding `Forbidden` that way would add a `Denial` field present
on every error. Decision: `Error` becomes a `[Union]` tagged struct (same pattern as `YFex.Rules.Verdict`: an `ErrorKind` tag +
per-case `TryGetValue`), so each case carries only its own data and adding a case (Privacy's `ConsentRequired` is next) is safe.

```csharp
// (sketch) YFex.Cqrs
public enum ErrorKind : byte { None = 0, NotFound = 1, Failed = 2, Unauthenticated = 3, Forbidden = 4, ValidationProblem = 5, Conflict = 6, ApprovalRequired = 7 }
public readonly record struct NotFound(string Message);
public readonly record struct Failed(string Message);
public readonly record struct Unauthenticated(string Message);
public readonly record struct Forbidden(Denial Denial);                                   // Denial from YFex.Authorization.Public
public readonly record struct ValidationProblem(ViolationList Violations);
public readonly record struct Conflict(string Message);
public readonly record struct ApprovalRequired(ApprovalRequestList Approvals, ulong DecisionFingerprint, ViolationList Violations);

[Union] public readonly struct Error : IUnion
{
    public ErrorKind Kind { get; }
    public string Message { get; }                       // always filled (display fallback), like today
    public bool TryGetValue(out Forbidden value);        // … one per case
    public static Error From(in AuthorizationVerdict verdict);
    public static Error Validation(in ValidationResult result);
    public static Error ApprovalRequired(in ApprovalRequestList approvals, ulong fingerprint, in ViolationList violations = default);
}
```

```csharp
// Client — before (one Unauthorized for everything, optional fields on every error)
if (result.Error?.Type == ErrorType.Unauthorized) toast("Unauthorized.");

// Client — after
switch (result.Error?.Kind)
{
    case ErrorKind.Unauthenticated: await session.LoginAgainAsync(); break;
    case ErrorKind.Forbidden when result.Error.Value.TryGetValue(out Forbidden f):
        toast($"Requer: {catalogs.DisplayName(f.Denial.Missing)}"); break;                   // "Requer: Cancelar venda"
    case ErrorKind.ApprovalRequired when result.Error.Value.TryGetValue(out ApprovalRequired a):
        await approvalDialog.ShowAsync(a.Approvals, a.DecisionFingerprint); break;
    case ErrorKind.ValidationProblem when result.Error.Value.TryGetValue(out ValidationProblem v):
        viewModel.ApplyServer(v.Violations); break;
}
```

| Verdict | HTTP | Cqrs `Error` case | Client reaction |
|---|---|---|---|
| `Unauthenticated` | 401 | `Unauthenticated` | re-login |
| `Forbidden(denial)` | 403 + `missing` | `Forbidden(Denial)` | "Requer: Cancelar venda" (display name from the manifest) |
| `ApprovalRequired(list)` | 403 + approvals | `ApprovalRequired` (same case the rules stage uses) | same supervisor-approval dialog as rule overrides |
| `JustificationRequired` | 403 + kind | `Forbidden(Denial { Kind = JustificationRequired })` | ask for a reason, retry |

Migration cost: `ErrorType` is referenced 114 times in 24 files (Cqrs, Messaging, Messaging.Rpc + formatters, Rules.Admin, tests)
— mostly mechanical. `ErrorType.Unauthorized` → `ErrorKind.Unauthenticated`, `ErrorType.Fail` → `Failed`. The MemoryPack wire
formatter (`RpcResultFormatters`) writes the tag byte + the case payload (wire v3; clients and servers ship together). Done as its
own step (2b) after the gate (2a), so each step stays green on its own.

## 6. Startup validation (fixes D5)

`CompiledAuthorizationRegistry.Build` validates **every** permission named by every policy against the merged catalogs and throws
one `AuthorizationConfigurationException` listing all unknown ids. Now it also covers Cqrs configurations (exported, 04 §2),
routes, view-model commands, Rules sets (Rules.Admin exports `IRuleSetHost` permissions: directives, table `PublishPermission`,
row `ApprovalPermission`) and RuleAdmin options. Runtime-authored rules (`PublishRule`) reject an unknown permission id with a
validation violation `authorization.permission.unknown` at publish time.

```csharp
// ✍ Dev writes (Rules.Admin, after) — no strings in options
[PermissionCatalog]
public static partial class RuleAdminPermissions
{
    [Permission("rules.tables.publish", Name = "Publicar tabelas de regras")]  public static partial Permission TablesPublish { get; }
    [Permission("rules.parameters.publish", Name = "Publicar parâmetros")]     public static partial Permission ParametersPublish { get; }
    [Permission("rules.dynamic.publish", Name = "Publicar regras dinâmicas")]  public static partial Permission RulesPublish { get; }
    [Permission("rules.override", Name = "Sobrepor regras")]                   public static partial Permission Override { get; }
}
```

## 7. Analyzers (generator project)

| Id | Fires when |
|---|---|
| AUTH001–004 | duplicate id, malformed id, unknown `[RequirePermission]` literal, misplaced `[Permission]` (§1) |
| (AUTH005) | reserved — `[RequirePermission]` on CQRS records is allowed for now (⚑Z6) |
| AUTH006 | `Permission.Parse("literal")` / `PermissionKey.Parse("literal")` in source: use the catalog property (`SalesPermissions.SaleVoid`) |
| AUTH007 | `[RequirePermission]` on a method that is not a `[StateCommand]` |

## 8. Performance budgets (acceptance in 09)

| Operation | Budget |
|---|---|
| `HasPermission` / `PermissionSet.Contains` | ≤ 2 ns, 0 B |
| Message gate, permissions-only policy, cached context | ≤ 25 ns, 0 B (incl. `ValueTask` completed synchronously) |
| `IsVisible` for 1 000 capabilities (search filter) | ≤ 15 µs, 0 B |
| Context resolution (claims-only sources, 20 roles, 200 permissions) | once per (subject, scope, version); ≤ 20 µs |
