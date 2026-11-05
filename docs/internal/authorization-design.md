# YFex.Authorization — System Design

> **2026-10-02:** most of this design is implemented. Current state, defects and the consolidation proposal are in [`authorization-consolidation/`](authorization-consolidation/README.md); read that first.

**Status:** Proposed (design/plan). See [`authorization-capabilities-overview.md`](authorization-capabilities-overview.md) for framing and cross-track reconciliations.
**Framework:** `net11.0` (adapters), `netstandard2.0` (generator); LangVersion preview. TFMs follow the repo `Directory.Build.props`.
**Scope:** one library owning authorization + auth primitives (permissions, roles, claims, policies, tokens) across four call surfaces — CQRS commands/queries, NavigatR routes, inbound HTTP endpoints, outbound HTTP clients.

---

## 1. Design principles & locked decisions

1. **Catalog = const-string ids + `[PermissionMeta]`.** Permission ids are `public const string` members carrying `[PermissionMeta(DisplayName, Description)]`. Being compile-time constants they are attribute-legal, so `[RequirePermission("orders.edit")]` takes a string literal.
2. **The raw string exists at exactly one place: the attribute-declaration boundary.** Every runtime get/interact surface — evaluator, registry, `IAuthContext`, `Requirement`/`Policy`, catalog accessors, manifest — takes and returns the **value object** (`Permission`/`PermissionKey`), never a raw string. The string→`Permission` wrapping happens in exactly two generated/startup sites (§5.4), never in application runtime code.
3. **Value objects have no implicit string conversions.** Per the `YFex.Length` rule (`src/YFex/Length.cs`), implicit conversions are allowed only when total and non-throwing; `string→identifier` is partial (validates/throws), so it is **explicit only**. Each value object provides `Parse`/`TryParse`/`ToString`/`.Value` + nested `TypeConverter` + nested `JsonConverter`.
4. **Migration uses temporary builder overloads, not implicit operators** (e.g. `RequireRoles(params string[])` beside `RequireRoles(params Role[])`).
5. **`.Requires(...)` is kept as an override-only escape hatch** on the fluent builders.
6. Framework conventions honored: source-generator over reflection, AOT-friendly core, `FrozenDictionary`/`FrozenSet` lookups, three-phase pipeline (registration → compilation → runtime), `[Union]`/`union` discriminated results, `Abstractions/Public` split.

---

## 2. Verified codebase facts this design builds on

CQRS (all under `src`):
- `YFex.Cqrs.Registration.AuthorizerDescriptor:{Kind:AuthorizerKind, InlinePredicate:Func<ClaimsPrincipal,object,bool>?, AuthorizerType:Type?, Roles:string[]?, PolicyName:string?}`; `AuthorizerKind {RequireAuthenticated, Roles, PolicyName, Inline, TypeRef}`.
- `CompiledMessagingRegistry.BuildCompositeAuthorizer` at `Cqrs/YFex.Cqrs/CompiledMessagingRegistry.cs:268`; the `switch` at `:277`; **`PolicyName`/`TypeRef` currently compile to `null` (`:289`) → silently unenforced.** Composed into `CommandPolicy.Authorize:Func<ClaimsPrincipal,object,bool>?` via `CompileCommandPolicy` (`:176`) / `CompileQueryPolicy` (`:190`).
- Runtime gate: `Messaging/YFex.Messaging.Rpc/LocalDispatcher.cs:133` (command-with-result) and `:197` (void) — `if (policy?.Authorize is not null && !policy.Authorize(ClaimsPrincipal.Current ?? new(), cmd!)) return QueueableResult.Fail("Unauthorized.")`.
- Fluent authorize surface: `Cqrs/YFex.Cqrs/Configuration/CommandConfigurationBuilder.cs:57-86` (`RequireAuthenticated`, `RequireRoles(params string[])`, `RequireAuthorization(string)`, `Authorize(Func<ClaimsPrincipal,TCommand,bool>)`, `Authorize<TAuthorizer>()`) and `:256-286`; mirrored on `QueryConfigurationBuilder`.

NavigatR:
- `RouteAttribute:{RouteName, RouteType, DisplayName, Parameter, ParameterRequired}` (`Navigation/YFex.NavigatR/RouteAttribute.cs`) — no permission field.
- `Navigator.cs`: prefetch off-UI-thread in `Task.Run` (`:113-139`, `OnPrefetch` at `:130`); nav core `ExecuteNavigationCoreAsync` (`:425-461`) resolves `vmType` via `ResolveViewModelType(route)` (`:428`), calls `OnNavigation` (`:435`), checks `ctx.IsDenied` (`:437`), returns `NavigationResult.Deny(ctx.DeniedReason)` (`:441`). Same pattern at `:473/546`.
- `NavigationResult` is a `union(NavigationSuccess, NavigationDenied, NavigationCancelled)` (`NavigationResult.cs:11`). `RouteRegistry.ResolveViewModel(IRoute)->Type`.
- Union support is a **per-assembly `UnionSupport.cs`** (copies in `Navigation/YFex.NavigatR/UnionSupport.cs`, `UI/YFex.UI.Abstractions/UnionSupport.cs`) — consuming unions needs no project reference.

Web (`C:\Dev\YFex\YFex.Web\src\Web`):
- **No inbound endpoint auth exists** — grep for `AddAuthentication`/`AddAuthorization`/`AddJwtBearer`/`TokenValidationParameters`/`[Authorize]`/`ClaimsPrincipal` across `src/Web` returned nothing. Only claims consumer is `YFex.Web.AspNetCore.Tenant.Strategies.ClaimTenantStrategy : ITenantResolveStrategy` (reads a tenant id).
- Outbound client auth: `YFex.Web.Credentials.ApiCredential` closed union (`ApiKeyCredential`/`BasicCredential`/`BearerTokenCredential`/`OAuthClientCredential:{ClientId,Secret,GrantType,Scope?,Resource?,CredentialsInBody}`); `YFex.Web.Auth.IAuthScheme.ApplyAsync(HttpRequestMessage, ApiCredential, ITenantInfo?, ct)`; `YFex.Web.Refit.Handlers.EndpointAuthHandler : DelegatingHandler` with `ApplySchemeAsync(AuthContext)` (CS8509-exhaustive credential switch); `YFex.Web.Tokens.ITokenStore.GetOrAcquireAsync(TokenCacheKey, Func<…,Task<OAuthToken>>, TimeSpan, ct)`/`InvalidateAsync`, `TokenCacheKey` (record struct), `OAuthToken` (record); impl `YFex.Web.Data.Tokens.FusionCacheTokenStore`; wiring `YFex.Web.Refit.Builder.AuthBuilderExtensions.AddOAuth/AddBasicCredentials/AddAuth` over `EndpointHttpClientBuilder : IHttpClientBuilder`.
- **`YFex.Web` references `YFex.Cqrs`** (`src/Web/YFex.Web/YFex.Web.csproj`) → the whole Web stack sits above CQRS; `YFex.Authorization.Public` is reachable from every Web project with no inversion.

Generator convention: CQRS generator (`Cqrs/YFex.Cqrs.SourceGenerator`) is an `IIncrementalGenerator`; its `README.md:202-206` states `ForAttributeWithMetadataName` is the 99× path for attribute-based detection — exactly our catalog/`[RequirePermission]` case.

Value-object exemplar: `src/YFex/Length.cs` (`readonly record struct` + `IComparable`/`IFormattable`/`IParsable`/`ISpanParsable` + `TryParse` + nested `LengthTypeConverter`/`LengthJsonConverter` + explicit conversions).

---

## 3. Project layout

```
YFex (core)
└── YFex.Authorization.Public          net11.0
    ├── YFex.Authorization             net11.0
    ├── YFex.Authorization.Generator   netstandard2.0   (analyzer/generator)
    ├── YFex.Authorization.AspNetCore  net11.0          (inbound host adapter)
    └── YFex.Authorization.Http        net11.0          (outbound transport adapter)
```

| Project | Responsibility | References | TFM |
|---|---|---|---|
| **`.Public`** | Value objects (`Permission`, `PermissionKey`, `Role`, `PolicyName`, `Claim`, `ClaimType`, `AccessToken`); `Requirement`, `Policy`; contracts `IPolicyEvaluator`, `IAuthContext`/`IAuthContext<T>`, `IPrincipalAccessor`, `IPermissionProvider`, `ITokenStore`, `TokenCacheKey`; `AuthorizationResult` union (+ own `UnionSupport.cs`); the `[PermissionCatalog]`, `[PermissionMeta]`, `[RequirePermission]` attributes; `AuthorizationManifest`/`PermissionMetaEntry`. Reflection-free, AOT-safe. | `YFex` core + BCL (`System.Security.Claims`) only | net11.0 |
| **`.Authorization`** | `IPolicyEvaluator` impl; `CompiledAuthorizationRegistry` + builders (`AuthorizationBuilder`, `IAuthorizationConfiguration`); catalog registry; default `IPrincipalAccessor`/`IPermissionProvider`; DI (`AddYFexAuthorization`). | `.Public` + `Microsoft.Extensions.DependencyInjection.Abstractions` | net11.0 |
| **`.Generator`** | `IIncrementalGenerator` (catalog value-object accessors + `AuthorizationManifest` + `IAuthorizationConfiguration` rows from `[RequirePermission]`) and analyzer (AUTH001–004). | Roslyn (`Analyzer` output, `ReferenceOutputAssembly=false`) | ns2.0 |
| **`.AspNetCore`** | Inbound: JWT bearer validation config, `ClaimsPrincipal → IAuthContext` bridge, `IPrincipalAccessor` over `IHttpContextAccessor`, endpoint filter + optional `AuthorizationPolicyProvider`. | `.Authorization` + ASP.NET + `Microsoft.AspNetCore.Authentication.JwtBearer`/`Microsoft.IdentityModel.*` | net11.0 |
| **`.Http`** | Outbound: principal/token-propagation `DelegatingHandler`; token-acquisition orchestration over `ITokenStore`. Refit/credential-union specifics remain in `YFex.Web.Refit`, which references this. | `.Authorization` + `Microsoft.Extensions.Http` | net11.0 |

Consumer edges (all point down into `.Public`): `YFex.Cqrs → .Public`; `YFex.NavigatR → .Public`; `YFex.Web.Refit → .Http`; `YFex.Web.AspNetCore → .AspNetCore`; `YFex.Web.Data.FusionCacheTokenStore` implements the migrated `ITokenStore`.

**Cycle check:** authz core references nothing in CQRS/NavigatR/Web; adapters reference only `.Authorization` + framework packages. No cycles. Splitting `.AspNetCore`/`.Http` out keeps heavy framework deps (ASP.NET, IdentityModel, HttpClient) off `.Public`.

**Caveat:** `.Public → YFex core` gives NavigatR (today refs only `YFex.UI.Public`) a transitive core edge. If undesirable, factor a BCL-only `YFex.Authorization.Abstractions` beneath `.Public` holding the value objects + `Requirement`/`Policy` + `IAuthContext`.

---

## 4. Value objects (final signatures)

All are `readonly record struct`, following `YFex.Length`. Converter bodies mirror `LengthTypeConverter`/`LengthJsonConverter` (string↔value, tolerant read). **No implicit string operators.**

### 4.1 `PermissionKey` — the interned identity/lookup key
```csharp
[TypeConverter(typeof(PermissionKeyTypeConverter))]
[JsonConverter(typeof(PermissionKeyJsonConverter))]
public readonly record struct PermissionKey
    : IComparable<PermissionKey>, IComparable, ISpanFormattable, ISpanParsable<PermissionKey>
{
    private readonly string _id;                 // canonical, lower, validated
    public string Value => _id ?? "";
    internal int Hash { get; }                   // stable FNV-1a(_id) — FrozenDictionary/FrozenSet key

    private PermissionKey(string canonical, int hash) { _id = canonical; Hash = hash; }

    public static PermissionKey Parse(string s, IFormatProvider? p = null)
        => TryParse(s, p, out var k) ? k : throw new FormatException($"'{s}' is not a valid permission id.");
    public static PermissionKey Parse(ReadOnlySpan<char> s, IFormatProvider? p = null) => Parse(s.ToString(), p);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out PermissionKey r);   // grammar: seg('.'seg)*
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out PermissionKey r);

    public int CompareTo(PermissionKey o) => string.CompareOrdinal(Value, o.Value);
    public int CompareTo(object? o) => o is PermissionKey k ? CompareTo(k) : 1;
    public bool Equals(PermissionKey o) => Hash == o.Hash && string.Equals(Value, o.Value, StringComparison.Ordinal);
    public override int GetHashCode() => Hash;
    public override string ToString() => Value;
    public string ToString(string? f, IFormatProvider? p) => Value;
    public bool TryFormat(Span<char> dst, out int written, ReadOnlySpan<char> f, IFormatProvider? p);

    public static explicit operator PermissionKey(string s) => Parse(s);   // explicit — validates
    public static explicit operator string(PermissionKey k) => k.Value;
}
```

### 4.2 `Permission` — the value object every runtime surface uses
```csharp
[TypeConverter(typeof(PermissionTypeConverter))]
[JsonConverter(typeof(PermissionJsonConverter))]
public readonly record struct Permission
    : IComparable<Permission>, IComparable, ISpanFormattable, ISpanParsable<Permission>
{
    public PermissionKey Key { get; }
    public string Value => Key.Value;
    public Permission(PermissionKey key) => Key = key;

    public static Permission Parse(string s, IFormatProvider? p = null) => new(PermissionKey.Parse(s, p));
    public static Permission Parse(ReadOnlySpan<char> s, IFormatProvider? p = null) => new(PermissionKey.Parse(s, p));
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out Permission r);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out Permission r);

    public int CompareTo(Permission o) => Key.CompareTo(o.Key);
    public int CompareTo(object? o) => o is Permission x ? CompareTo(x) : 1;
    public override string ToString() => Value;
    public string ToString(string? f, IFormatProvider? p) => Value;
    public bool TryFormat(Span<char> dst, out int w, ReadOnlySpan<char> f, IFormatProvider? p);

    public static explicit operator Permission(PermissionKey k) => new(k);
    public static implicit operator PermissionKey(Permission p) => p.Key;  // total & non-throwing → implicit OK
}
// Display/Description are NOT on the struct (keeps it pointer-sized). They live in AuthorizationManifest.
```

### 4.3 `Role`
```csharp
[TypeConverter(typeof(RoleTypeConverter))]
[JsonConverter(typeof(RoleJsonConverter))]
public readonly record struct Role
    : IComparable<Role>, IComparable, ISpanFormattable, ISpanParsable<Role>
{
    private readonly string _name;
    public string Value => _name ?? "";

    public static Role Parse(string s, IFormatProvider? p = null)
        => TryParse(s, p, out var r) ? r : throw new FormatException($"'{s}' is not a valid role.");
    public static Role Parse(ReadOnlySpan<char> s, IFormatProvider? p = null) => Parse(s.ToString(), p);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out Role r)
    { r = default; if (string.IsNullOrWhiteSpace(s)) return false; r = new Role(s.Trim()); return true; }
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out Role r);

    private Role(string name) => _name = name;
    public int CompareTo(Role o) => string.Compare(Value, o.Value, StringComparison.OrdinalIgnoreCase);
    public int CompareTo(object? o) => o is Role r ? CompareTo(r) : 1;
    public bool Equals(Role o) => string.Equals(Value, o.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
    public string ToString(string? f, IFormatProvider? p) => Value;
    public bool TryFormat(Span<char> dst, out int w, ReadOnlySpan<char> f, IFormatProvider? p);

    public static explicit operator Role(string s) => Parse(s);
    public static explicit operator string(Role r) => r.Value;
}
```

### 4.4 `PolicyName`
```csharp
[TypeConverter(typeof(PolicyNameTypeConverter))]
[JsonConverter(typeof(PolicyNameJsonConverter))]
public readonly record struct PolicyName
    : IComparable<PolicyName>, IComparable, ISpanFormattable, ISpanParsable<PolicyName>
{
    private readonly string _name;
    public string Value => _name ?? "";
    public static PolicyName Parse(string s, IFormatProvider? p = null)
        => TryParse(s, p, out var n) ? n : throw new FormatException($"'{s}' is not a valid policy name.");
    public static PolicyName Parse(ReadOnlySpan<char> s, IFormatProvider? p = null) => Parse(s.ToString(), p);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out PolicyName r);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out PolicyName r);
    private PolicyName(string name) => _name = name;
    public int CompareTo(PolicyName o) => string.CompareOrdinal(Value, o.Value);
    public int CompareTo(object? o) => o is PolicyName n ? CompareTo(n) : 1;
    public override string ToString() => Value;
    public string ToString(string? f, IFormatProvider? p) => Value;
    public bool TryFormat(Span<char> dst, out int w, ReadOnlySpan<char> f, IFormatProvider? p);
    public static explicit operator PolicyName(string s) => Parse(s);
    public static explicit operator string(PolicyName n) => n.Value;
}
```

### 4.5 `ClaimType` and `Claim`
```csharp
[TypeConverter(typeof(ClaimTypeTypeConverter))]
[JsonConverter(typeof(ClaimTypeJsonConverter))]
public readonly record struct ClaimType
    : IComparable<ClaimType>, IComparable, ISpanFormattable, ISpanParsable<ClaimType>
{
    private readonly string _type;                    // normalized: short-name ↔ well-known URI
    public string Value => _type ?? "";
    public static ClaimType Parse(string s, IFormatProvider? p = null);      // throws on empty
    public static ClaimType Parse(ReadOnlySpan<char> s, IFormatProvider? p = null);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out ClaimType r);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out ClaimType r);
    public bool Equals(ClaimType o) => string.Equals(Value, o.Value, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public int CompareTo(ClaimType o) => string.Compare(Value, o.Value, StringComparison.OrdinalIgnoreCase);
    // ToString/TryFormat → Value; explicit operator ClaimType(string)
}

[TypeConverter(typeof(ClaimTypeConverter))]
[JsonConverter(typeof(ClaimJsonConverter))]
public readonly record struct Claim
    : IComparable<Claim>, IComparable, ISpanFormattable, ISpanParsable<Claim>
{
    public ClaimType Type { get; }
    public string Value { get; }                       // claim value (kept ordinal)
    public Claim(ClaimType type, string value) { Type = type; Value = value ?? ""; }

    public int CompareTo(Claim o) { var c = Type.CompareTo(o.Type); return c != 0 ? c : string.CompareOrdinal(Value, o.Value); }
    public override string ToString() => $"{Type.Value}={Value}";   // canonical wire form
    public string ToString(string? f, IFormatProvider? p) => ToString();
    public bool TryFormat(Span<char> dst, out int w, ReadOnlySpan<char> f, IFormatProvider? p);
    public static Claim Parse(string s, IFormatProvider? p = null);          // splits first '='
    public static Claim Parse(ReadOnlySpan<char> s, IFormatProvider? p = null);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out Claim r);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out Claim r);

    // BCL bridge (BCL-only, allowed in .Public):
    public static Claim From(System.Security.Claims.Claim c) => new(ClaimType.Parse(c.Type), c.Value);
    public System.Security.Claims.Claim ToClaim() => new(Type.Value, Value);
}
```

### 4.6 `AccessToken` (generalizes `YFex.Web.Auth.OAuthToken`)
```csharp
[JsonConverter(typeof(AccessTokenJsonConverter))]
public readonly record struct AccessToken : ISpanFormattable, ISpanParsable<AccessToken>
{
    public string Value { get; }                       // raw bearer/JWT
    public DateTimeOffset? ExpiresAt { get; }
    public string Scheme { get; }                      // default "Bearer"
    public AccessToken(string value, DateTimeOffset? expiresAt = null, string scheme = "Bearer");
    public bool IsExpired(TimeSpan skew = default) => ExpiresAt is { } e && DateTimeOffset.UtcNow >= e - skew;
    public string ToAuthorizationHeader() => $"{Scheme} {Value}";
    public bool Equals(AccessToken o) => Scheme == o.Scheme && string.Equals(Value, o.Value, StringComparison.Ordinal);
    public override int GetHashCode() => HashCode.Combine(Scheme, Value);
    public override string ToString() => $"{Scheme} ****";        // REDACTED — never log the value
    public string ToString(string? f, IFormatProvider? p) => ToString();
    public bool TryFormat(Span<char> dst, out int w, ReadOnlySpan<char> f, IFormatProvider? p);   // redacted
    public static AccessToken Parse(string s, IFormatProvider? p = null);   // "Scheme value"
    public static AccessToken Parse(ReadOnlySpan<char> s, IFormatProvider? p = null);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? p, out AccessToken r);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? p, out AccessToken r);
}
```

---

## 5. Catalog, `[PermissionMeta]`, generator & analyzer

### 5.1 Authoring shape (app code)
```csharp
[PermissionCatalog]
public static partial class Permissions
{
    [PermissionMeta(DisplayName = "Edit orders", Description = "Modify a pending order")]
    public const string OrdersEdit = "orders.edit";

    [PermissionMeta(DisplayName = "View orders")]
    public const string OrdersView = "orders.view";
}
```
Usage at the attribute boundary (the only place the string appears):
```csharp
[RequirePermission(Permissions.OrdersEdit)]        // const string literal — attribute-legal
public sealed class EditOrderCommand : ICommand { … }
```

### 5.2 Attributes (in `.Public`)
```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PermissionCatalogAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field, Inherited = false)]           // Field = const string member
public sealed class PermissionMetaAttribute : Attribute
{ public string? DisplayName { get; set; } public string? Description { get; set; } }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class RequirePermissionAttribute : Attribute
{ public string Permission { get; } public RequirePermissionAttribute(string permission) => Permission = permission; }
```

### 5.3 Generator pipeline (`YFex.Authorization.Generator`, `IIncrementalGenerator`)
Uses `ForAttributeWithMetadataName` (the fast path per `Cqrs/YFex.Cqrs.SourceGenerator/README.md:202-206`):

- Pipeline A — `ForAttributeWithMetadataName("YFex.Authorization.Public.PermissionMetaAttribute")`: collects `(constName, idValue, DisplayName, Description, declaringType)`. Emits, into the `[PermissionCatalog]` partial class, a nested value-object accessor plus the frozen catalog:
```csharp
// GENERATED
public static partial class Permissions
{
    public static class Values                        // value-object accessors — return Permission
    {
        public static readonly Permission OrdersEdit = new(PermissionKey.Parse("orders.edit"));   // WRAP SITE #1
        public static readonly Permission OrdersView = new(PermissionKey.Parse("orders.view"));
    }
    public static FrozenDictionary<PermissionKey, Permission> All { get; } = /* built from Values */;
    public static AuthorizationManifest Manifest { get; } = new(new PermissionMetaEntry[]{
        new(Values.OrdersEdit.Key, "Edit orders", "Modify a pending order", "Permissions.OrdersEdit"),
        new(Values.OrdersView.Key, "View orders", null, "Permissions.OrdersView"),
    });
}
```
- Pipeline B — `ForAttributeWithMetadataName("YFex.Authorization.Public.RequirePermissionAttribute")`: collects each decorated type/method + its literal string. Emits an `IAuthorizationConfiguration` that adds registry rows keyed by the decorated symbol's `Type` (§6). It resolves the literal to a `Permission` **through the catalog** at build (WRAP SITE #2, §5.4).

`AuthorizationManifest`/`PermissionMetaEntry` live in `.Public` and are consumed by both the registry and the capability-manifest track (display/description/keywords for AI/search).

### 5.4 The singular string→`Permission` boundary
The raw string is converted to a value object in exactly two generated/startup locations, never in app runtime:
1. **WRAP SITE #1 — generated catalog** (`Permissions.Values.*` + `Permissions.All`): each const id → `Permission` once at static init.
2. **WRAP SITE #2 — registry build** (`CompiledAuthorizationRegistry.Build`, §6): each `[RequirePermission("…")]` literal → `Permission` via `Permissions.All` lookup, once at startup. A literal absent from the catalog is an analyzer error (AUTH003) and, defensively, a startup throw.

Everything downstream (`IPolicyEvaluator`, `IAuthContext`, `Requirement`, `Policy`, `.Requires(...)`, capability manifest) is value-object-only.

### 5.5 Analyzer diagnostics
- **AUTH001** duplicate permission id (two consts resolve to the same value).
- **AUTH002** malformed id — fails `PermissionKey` grammar (`seg('.'seg)*`).
- **AUTH003** `[RequirePermission("x")]` where `"x"` is not a declared catalog id (this makes a misspelled/forgotten permission a **build break** even though the argument is a raw literal — the analyzer resolves the constant value and checks catalog membership).
- **AUTH004** `[PermissionMeta]` on a non-`const`/non-`string` member, or on a member outside a `[PermissionCatalog]` type.

---

## 6. `CompiledAuthorizationRegistry` (build + frozen tables)

Mirrors `CompiledMessagingRegistry` (build once at startup, `FrozenDictionary` at runtime, zero runtime reflection).
```csharp
public sealed class CompiledAuthorizationRegistry
{
    public FrozenDictionary<Type, Policy> ByMessageType   { get; }  // CQRS commands/queries + inbound handlers
    public FrozenDictionary<Type, Policy> ByViewModelType { get; }  // NavigatR routes
    public FrozenDictionary<PermissionKey, Permission> Permissions { get; }
    public AuthorizationManifest Manifest { get; }

    public static CompiledAuthorizationRegistry Build(
        IEnumerable<IAuthorizationConfiguration> configs,     // generated (attribute) + hand-written (fluent)
        AuthorizationManifest[] catalogManifests);            // one per catalog assembly
}
```
Build phases: (1) **Collect** rows from all `IAuthorizationConfiguration` (attribute-generated + fluent); (2) **Merge** multiple requirements per type into one `Policy` (mirrors `CommandAccumulator.Merge` at `CompiledMessagingRegistry.cs:395`); (3) **Freeze** to `FrozenDictionary`. WRAP SITE #2 lives in Collect.

`Requirement`/`Policy`/contracts:
```csharp
public enum RequirementKind { Authenticated, Roles, Permission, Claim, Delegate, PolicyRef }

public readonly record struct Requirement
{
    public RequirementKind Kind { get; init; }
    public Permission Permission { get; init; }
    public Role[]?    Roles      { get; init; }
    public Claim?     Claim      { get; init; }
    public PolicyName PolicyRef  { get; init; }
    public Func<IAuthContext, bool>? Predicate { get; init; }   // sync, context-only
    public static Requirement RequireAuthenticated();
    public static Requirement RequireRoles(params Role[] roles);
    public static Requirement Require(Permission p);
    public static Requirement RequireClaim(Claim c);
    public static Requirement RequirePolicy(PolicyName n);
    public static Requirement Custom(Func<IAuthContext,bool> f);
}
public sealed record Policy(PolicyName Name, Requirement[] Requirements, bool RequireAll = true);

public interface IAuthContext
{
    ClaimsPrincipal Principal { get; }
    AccessToken? Token { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(in Role role);
    bool HasPermission(in Permission permission);      // FrozenSet lookup (see §8)
    bool HasClaim(in Claim claim);
}
public interface IAuthContext<out TResource> : IAuthContext { TResource Resource { get; } }
public interface IPrincipalAccessor { IAuthContext Current { get; } }

public interface IPolicyEvaluator
{
    bool IsAllowed(in Permission permission, IAuthContext ctx);   // cheap sync — visibility
    bool IsAllowed(Policy policy, IAuthContext ctx);
    ValueTask<AuthorizationResult> AuthorizeAsync<TResource>(
        Policy policy, IAuthContext<TResource> ctx, CancellationToken ct = default);
}

public readonly union AuthorizationResult(Allowed, Denied)
{
    public static Allowed Allow() => new();
    public static Denied  Deny(Permission missing = default, string? reason = null) => new(missing, reason);
}
public record Allowed();
public record Denied(Permission Missing = default, string? Reason = null);
```

Fluent builder (both authoring styles feed one registry; `.Requires(...)` is the override escape hatch; migration overloads present):
```csharp
public sealed class RequirementBuilder<T>
{
    public RequirementBuilder<T> RequireAuthenticated();
    public RequirementBuilder<T> RequireRoles(params Role[] roles);
    public RequirementBuilder<T> RequireRoles(params string[] roles);   // TEMP migration overload
    public RequirementBuilder<T> RequirePermission(Permission p);
    public RequirementBuilder<T> RequireClaim(Claim c);
    public RequirementBuilder<T> RequirePolicy(PolicyName n);
    public RequirementBuilder<T> Requires(Func<IAuthContext, bool> predicate);   // OVERRIDE-only escape hatch
}
// b.ForMessage<EditOrderCommand>().RequirePermission(Permissions.Values.OrdersEdit).RequireRoles((Role)"Manager");
// b.ForRoute<OrderDetailViewModel>().RequirePermission(Permissions.Values.OrdersView);
// b.ForEndpoint<EditOrderRequest>().Requires(ctx => ctx.HasClaim(new Claim((ClaimType)"dept","orders")));
```

---

## 7. The four gates (concrete wiring / insertion points)

### Gate 1 — CQRS (execution-time)
Modify `CompiledMessagingRegistry.BuildCompositeAuthorizer` (`Cqrs/YFex.Cqrs/CompiledMessagingRegistry.cs:268`): translate each `AuthorizerDescriptor` (`AuthorizerKind`) into a `Requirement` — `Roles(string[])→Role[]`, `PolicyName(string)→PolicyName`, `Inline→Delegate`, `TypeRef→PolicyRef/Delegate`, `RequireAuthenticated→Authenticated` — build one `Policy`, and return the existing predicate shape:
```csharp
// replaces the switch at :277; PolicyName/TypeRef (:289) now resolve instead of returning null
var policy = TranslateToPolicy(descriptors);
return (user, _) => _evaluator.IsAllowed(policy, _principalFactory(user));
```
`CommandPolicy.Authorize:Func<ClaimsPrincipal,object,bool>?` is unchanged, so **`LocalDispatcher.cs:133` and `:197` are untouched**. This is also the fix for the unenforced `PolicyName`/`TypeRef` gap. New builder overloads `RequirePermission(Permission)`/`RequireRoles(params Role[])` are added to `CommandConfigurationBuilder`/`QueryConfigurationBuilder` beside the existing methods.

### Gate 2 — NavigatR (pre-navigation + prefetch)
Add optional `Permission` support to routes via the fluent `b.ForRoute<TVm>()` path (avoids the known-broken `NavigatRRegistration.RegisterAll` partial-method mechanism, per `Navigation/AGENTS.md`); optionally add a `Permission` string field to `RouteAttribute.cs`. Inject `IPolicyEvaluator` + `CompiledAuthorizationRegistry` + `IPrincipalAccessor` into `Navigator`. Insert a **sync** check after `ResolveViewModelType` and before resolving/creating the VM:
- Nav core `Navigator.cs:428` (before `OnNavigation` at `:435`):
```csharp
var vmType = prefetchedEntry?.ResolvedViewModelType ?? ResolveViewModelType(route);
if (_authRegistry.ByViewModelType.TryGetValue(vmType, out var policy)
    && !_policyEval.IsAllowed(policy, _principal.Current))
    return NavigationResult.Deny($"Requires {policy.Requirements[0].Permission.Value}");
```
- Prefetch `Task.Run` at `Navigator.cs:117` (before `OnPrefetch` at `:130`): same check; on deny, cancel the prefetch token instead of running `OnPrefetch`.
Reuses the existing `NavigationDenied` variant; `INavigable`/`context.Deny()` remain for per-instance/data-driven denials.

### Gate 3 — Inbound HTTP (`.AspNetCore`)
- Configure JWT bearer (`AddAuthentication().AddJwtBearer(...)`); on validation, build `IAuthContext` from the validated `ClaimsPrincipal` (via `Claim.From`) plus an `AccessToken` from the raw bearer; register `IPrincipalAccessor` over `IHttpContextAccessor`.
- Endpoint filter (`IEndpointFilter`) looks up the endpoint's command/request type in `ByMessageType` and calls `AuthorizeAsync`; `Denied → 403` with `Missing` reason. Optionally expose an `AuthorizationPolicyProvider` so `[Authorize("orders.edit")]` bridges to the same evaluator.
- A command type carrying `[RequirePermission]` is gated identically whether dispatched internally (Gate 1) or received over HTTP (Gate 3) — one policy, two transports.

### Gate 4 — Outbound HTTP (`.Http`, consumed by `YFex.Web.Refit`)
- **Pre-flight**: caller-side `IPolicyEvaluator.IsAllowed(Permissions.Values.X, _principal.Current)` before issuing a user-action call.
- **Propagation `DelegatingHandler`**: reads `_principal.Current.Token` (`AccessToken`) and sets `request.Headers.Authorization = token.ToAuthorizationHeader()`, or performs on-behalf-of exchange via the migrated `ITokenStore.GetOrAcquireAsync(TokenCacheKey, …)`. Insert **before** `YFex.Web.Refit.Handlers.EndpointAuthHandler` in the handler chain (registered in `AuthBuilderExtensions`), so user-context propagation and service-credential schemes compose.
- Service-to-service (no user principal) keeps the existing `ApiCredential` union + `EndpointAuthHandler.ApplySchemeAsync` (CS8509-exhaustive), now consuming the shared `AccessToken`/`ITokenStore`/`TokenCacheKey`.

---

## 8. Permission → claims resolution (decision)

**Decision: resolve once at principal construction, cache on the `IAuthContext`; feed that resolution from a single `IPermissionProvider`.** This is effectively "claims-at-login" semantics with a centrally-defined role→permission map, and keeps the sync fast-path allocation-free.

- `IPermissionProvider` (in `.Public`, impl in `.Authorization`): `IReadOnlySet<PermissionKey> PermissionsFor(ClaimsPrincipal principal)` — expands roles/claims into permission keys (config- or DB-backed). Called **once** when an `IAuthContext` is built (inbound: after JWT validation; internal: at login/session start), not per check.
- The concrete `IAuthContext` stores a `FrozenSet<PermissionKey>`; `HasPermission(in Permission)` is an O(1) `FrozenSet` lookup — safe to call per-candidate in a Search/AI hot loop.
- Direct permission claims in the token are honored: if the token already carries permission claims, `IPermissionProvider` unions them with role-expansion. Supports both token-embedded-permissions and role-mapping deployments without changing check-time code.

Rejected: resolving permissions per `HasPermission` call (would require a service call / allocation on every visibility check — incompatible with filter-before-rank).

---

## 9. Sync visibility vs async gate

- `IsAllowed(...)` (sync): evaluates only context-resolvable requirements — `Authenticated`, `Roles`, `Permission`, `Claim`, `Delegate`. Pure, side-effect-free, `FrozenSet`-backed. **Unevaluable requirements (`PolicyRef`, resource-based) are treated as _hidden_ (conservative)** so menus/search/AI never surface an action the user may not take. Search/AI use this to **filter-before-rank** (never rank-then-filter).
- `AuthorizeAsync(...)` (async, authoritative): evaluates everything including `PolicyRef` (resolved via DI-registered policy handlers) and resource-based requirements; returns the `AuthorizationResult` union. The true gate for CQRS/HTTP execution.
- Consistency rule: any requirement kind evaluable synchronously must yield the same verdict in both paths; only `PolicyRef`/resource kinds differ (hidden in sync, decided in async).

---

## 10. Token / `AccessToken` / `ITokenStore` migration from `YFex.Web`

- Move `ITokenStore` + `TokenCacheKey` from `YFex.Web.Tokens` into `.Public` (transport-neutral). `YFex.Web.Data.Tokens.FusionCacheTokenStore` implements the moved interface — namespace change only, behavior unchanged.
- Generalize `YFex.Web.Auth.OAuthToken` → `.Public` `AccessToken`. Keep `OAuthToken` initially as a thin record convertible to `AccessToken` (so `IOAuthTokenSource`/`OAuthTokenSource<TAuthApi>` compile), deprecate later.
- `IAuthScheme`, the `ApiCredential` union, and `EndpointAuthHandler` stay in `YFex.Web.Refit` (transport-coupled), referencing `.Http`/`.Public` primitives.
- Secrets (`ISecretProtector`/`AesSecretProtector`) and persistence stay in Web/Data; `.Public` never holds plaintext secrets; `AccessToken.ToString()`/`TryFormat` redact.

---

## 11. DI registration surface

```csharp
// .Authorization
services.AddYFexAuthorization(cfg =>
{
    cfg.AddCatalog(Permissions.Manifest);              // one per catalog assembly (generated)
    cfg.AddConfigurations(GeneratedAuthConfig.All);    // attribute-generated IAuthorizationConfiguration
    cfg.AddConfiguration<OrdersAuthorization>();       // hand-written fluent config
    cfg.UsePermissionProvider<RoleMapPermissionProvider>();   // role→permission expansion
});
// registers: IPolicyEvaluator, CompiledAuthorizationRegistry (built once), IPermissionProvider,
//            a default IPrincipalAccessor (ambient) — overridden by host adapters.

// .AspNetCore (inbound)
services.AddYFexAuthorizationAspNetCore(o => o.ConfigureJwtBearer(...));   // JWT + IHttpContextAccessor-backed IPrincipalAccessor
app.UseYFexAuthorization();                                                // wires the endpoint filter

// .Http (outbound) — composes with existing Refit builder
endpointClientBuilder.AddPrincipalPropagation();     // inserts the propagation DelegatingHandler before EndpointAuthHandler
```
CQRS wiring: `CompiledMessagingRegistry` build gains an injected `IPolicyEvaluator` used by `BuildCompositeAuthorizer`; CQRS DI resolves it from `.Authorization`.

---

## 12. Phased rollout / migration order

1. **`.Public` + `.Generator`**: value objects, attributes, catalog generator, analyzer AUTH001–004. No behavior change; introduces `Permissions.Values.*` + `AuthorizationManifest`.
2. **`.Authorization`**: `IPolicyEvaluator`, `CompiledAuthorizationRegistry`, builders, `IPermissionProvider`, DI. Unit-tested standalone.
3. **CQRS adapter (Gate 1)**: reroute `BuildCompositeAuthorizer` through `IPolicyEvaluator`; add `RequirePermission`/`Role[]` overloads. **Explicitly ship the `PolicyName`/`TypeRef` enforcement fix** (previously `null` at `CompiledMessagingRegistry.cs:289`) — call out in release notes as a behavior change: configs using `.RequireAuthorization("…")`/`.Authorize<T>()` now actually deny. Audit existing configs first.
4. **Token migration (§10)**: move `ITokenStore`/`TokenCacheKey`/`AccessToken`; repoint `FusionCacheTokenStore` and Refit.
5. **`.AspNetCore` (Gate 3)**: greenfield inbound JWT + endpoint filter (nothing to break — no inbound auth exists today).
6. **`.Http` (Gate 4)**: principal-propagation handler; pre-flight helpers.
7. **NavigatR (Gate 2)**: fluent `ForRoute<T>()` route permissions + Navigator insertion; optional `RouteAttribute.Permission`.
8. **Deprecate migration overloads** (`RequireRoles(params string[])`, `OAuthToken`) once callers move to value objects.

Each phase compiles with zero required source changes in existing CQRS configs / Refit registrations (except the intentional Gate-1 enforcement fix in phase 3).

---

## 13. Testing strategy

- **Value objects**: round-trip `Parse`/`TryParse`/`ToString`/`.Value`; equality/ordering/hash (case-insensitivity for `Role`/`ClaimType`); `TypeConverter` and `JsonConverter` (System.Text.Json) round-trips; explicit-conversion throw-on-invalid; `AccessToken.ToString()` redaction (assert the raw value never appears).
- **Generator (snapshot)**: golden-file tests over `Permissions.Values.*`, `All`, `Manifest`; incremental-cache correctness (equal inputs → cached, per `EquatableArray` discipline).
- **Analyzer**: AUTH001 (dup id), AUTH002 (bad grammar), AUTH003 (unknown `[RequirePermission]` literal → must flag misspelling), AUTH004 (misplaced `[PermissionMeta]`) — assert diagnostic id + location; assert clean code produces none.
- **Registry build**: string→`Permission` wrapping only at WRAP SITE #2; merge of multiple requirements per type; frozen-table membership; unknown literal → startup throw.
- **Evaluator**: `IsAllowed` vs `AuthorizeAsync` verdict-consistency on shared requirement kinds; `PolicyRef`/resource hidden-in-sync but decided-in-async; `FrozenSet` `HasPermission` correctness.
- **Gate 1 (CQRS)**: `LocalDispatcher` returns `Unauthorized` `QueueableResult.Fail` when denied; **regression test proving `PolicyName`/`TypeRef` now enforce** (previously bypassed).
- **Gate 2 (NavigatR)**: denied route returns `NavigationDenied` before `OnPrefetch`/`OnNavigation` run (assert the VM is never constructed / prefetch cancelled).
- **Gate 3 (inbound)**: `WebApplicationFactory` integration — valid token + permission → 200; missing permission → 403 with `Missing`; unauthenticated → 401.
- **Gate 4 (outbound)**: propagation handler attaches `AccessToken`; token-exchange via `ITokenStore` cached (single acquire under concurrency); credential-union path unchanged.
- **Permission→claims**: `IPermissionProvider` resolution runs once per context (assert call count), role-expansion + token-embedded-permission union.

---

## 14. Seam with the capability-manifest track

Intact and value-object-based: the per-ViewModel capability manifest references permissions by the **generated value-object accessor** (`Permissions.Values.OrdersEdit`, a `Permission`), calling `.RequirePermission(Permission)` into this catalog. The shared surface is: (a) the single `[RequirePermission("…")]` attribute (const-string at the declaration boundary, value object everywhere else), (b) the `Permission` symbol via `Permissions.Values.*`, and (c) `AuthorizationManifest`/`PermissionMetaEntry` for display/description/search-keyword/AI text. Search/AI consume `IPolicyEvaluator.IsAllowed(in Permission, IAuthContext)` for filter-before-rank. See [`capabilities-design.md`](capabilities-design.md).

---

## 15. Open risks / decisions to confirm

- **JWT validation is reflection-heavy** (`Microsoft.IdentityModel`): acceptable on the ASP.NET host; core (`.Public`/`.Authorization`/`.Generator`) stays reflection-free/AOT-safe.
- **On-behalf-of outbound**: may need a new `OnBehalfOfCredential` variant in the `ApiCredential` union — CS8509 will flag `EndpointAuthHandler.ApplySchemeAsync` (the closed-hierarchy guarantee working as intended).
- **`ClaimsPrincipal.Current` replacement**: migrate `LocalDispatcher.cs:133/197` onto injected `IPrincipalAccessor`, with a back-compat adapter reading the ambient value during transition.
- **Catalog accessor name** (`Permissions.Values.X`): confirm this is the symbol the capability track references (vs. a flat generated `Perm.X`).
- **`Role`/`ClaimType` canonicalization** locked as ordinal-ignore-case / short-name↔URI — confirm against the eventual JWT claim shapes.

### Critical files for implementation
- `YFex.Cqrs/src/YFex.Cqrs/CompiledMessagingRegistry.cs` (Gate 1 adapter; `PolicyName`/`TypeRef` fix — lines 268/277/289)
- `YFex.Messaging/src/YFex.Messaging.Rpc/LocalDispatcher.cs` (gate call sites 133/197; move to `IPrincipalAccessor`)
- `YFex.NavigatR/src/YFex.NavigatR/Navigator.cs` (Gate 2 insertion points 117/428)
- `C:\Dev\YFex\YFex.Web\src\Web\YFex.Web.Refit\Handlers\EndpointAuthHandler.cs` (Gate 4 handler ordering; principal propagation)
- `C:\Dev\YFex\YFex.Web\src\Web\YFex.Web\Tokens\ITokenStore.cs`, `Auth\OAuthToken` (→ `AccessToken`), `Credentials\ApiCredential.cs` (token/credential migration)
- `src/YFex/Length.cs` (value-object convention exemplar)
