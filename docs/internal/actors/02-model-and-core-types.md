# 02 — Model and core types

Namespace `YFex.Actors` (package `YFex.Actors`, references YFex core only). Every type here is a value or a contract; the composite
directory is in `YFex.Actors.Directory` (§8).

## 1. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **actor** | one party that does something: a person, a service, a device, a job, an AI agent, the system | cashier João; POS terminal 3; job `sales-report#42` |
| **subject** | the actor's stable opaque id inside its kind; never a name or e-mail | account id `8f3a2c…`, installation id, job key |
| **home** | where the actor belongs in the tenancy hierarchy: a core `TenancyPath` (⚑AC13) | `0=farmaviva/1=loja7` |
| **origin** | the installation where the work was started — not part of the actor (⚑AC16) | Caixa 3's `InstallationId` |
| **real actor** | who is really behind the work when it differs from the actor | support behind "acting as João"; job behind "run as owner" |
| **relation** | how the real actor relates to the actor | `UserImpersonation`, `Delegation`, `AgentOnBehalf` |
| **stamp** | "who and when" stored in a record | `Created = (João, null, 2026-11-05T10:12)` |
| **display** | what a screen shows for an actor, resolved at read time | "João Silva", "Caixa 3 — Loja 7", "removido (#h:3f…)" |

## 2. `ActorKind` (⚑AC2, decided)

```csharp
public enum ActorKind : byte
{
    Anonymous = 0,   // nobody signed in (default)
    User = 1,        // a person with an account (staff, shopper)
    Service = 2,     // a backend or partner integration with its own credentials (⚑AC5: partners are services)
    Device = 3,      // an installation acting without a signed-in person (POS terminal, kiosk, scale)
    Job = 4,         // scheduled or queued work (YFex.Jobs)
    Agent = 5,       // an AI agent through the capability surface
    System = 6,      // the platform itself (migrations, maintenance, startup)
}
```

Closed set: libraries decide rendering, permissions and limits per kind. No `Group` (⚑AC6), no `External` (⚑AC5).

## 3. `ActorIdentity`

```csharp
public readonly record struct ActorIdentity(ActorKind Kind, string Subject, TenancyPath Home = default)
    : ILogValue<ActorIdentity>, ICanonicalValue<ActorIdentity>
{
    public const int MaxSubjectBytes = 128;
    public static ActorIdentity Anonymous => default;
    public bool IsAnonymous => Kind == ActorKind.Anonymous;

    // factories: every kind takes a home (⚑AC17)
    public static ActorIdentity User(string accountId, TenancyPath home = default);
    public static ActorIdentity Service(string name, TenancyPath home = default);         // "marketplace-webhook", "partner/acme"
    public static ActorIdentity Device(InstallationId installation, TenancyPath home = default);   // ⚑AC15, subject = Guid D text
    public static ActorIdentity Job(string jobKey, TenancyPath home = default);           // key shape owned by YFex.Jobs (⚑AC14)
    public static ActorIdentity Agent(string agentKey, TenancyPath home = default);
    public static ActorIdentity System(string component, TenancyPath home = default);     // "migrations", "offline.broker"

    public ActorIdentity WithHome(TenancyPath home);
    public bool TryGetInstallation(out InstallationId installation);                     // Device kind only

    public static bool TryReadCanonical(ReadOnlySpan<byte> source, out ActorIdentity value, out int read);
}
```

`TenancyPath` (core, namespace `YFex.Tenancy`, ⚑AC13) is the stored form of a tenancy position: canonical text
`{rank}={value}/…` (least specific first, ranks strictly increasing, `%25`/`%2F` escapes, ≤ 8 levels, ≤ 512 UTF-8 bytes), with
`IsWithin`, `Parent`, `Depth`, canonical bytes `length (LE16) · UTF-8` and a log value. Core owns the grammar; YFex.Tenancy's
`ScopeIdentity` parses with it and converts (`ToTenancyPath()`, `ScopeIdentity.From(path)`, `path.ToScopeIdentity()`).
Levels stay app-declared ranks: the framework has no `TenantId` or `StoreId` type.

Rules:
- parameter order changes from `(Subject, Kind)` to `(Kind, Subject)` so `default` is anonymous by kind, not by empty subject (the
  Offline reader's guess goes away);
- subject: non-empty for every kind except `Anonymous` (where it must be empty), ≤ 128 UTF-8 bytes (⚑AC12), no control
  characters, no `@` (reserved for the home in the text form); its inner shape belongs to the kind's owner (`template#instance` is
  YFex.Jobs' rule, ⚑AC14);
- **identity = kind + subject + home** (⚑AC17): a subject is unique within its kind and home. Globally unique subjects (account ids,
  installation ids) need no home to be told apart; per-scope subjects (a system job that runs at every store, a per-tenant service
  name) do. Equality is ordinal on all three parts;
- where the work ran is **not** part of the actor: it is `ActorScope.Origin` / `ActorStamp.Origin` (⚑AC16);
- `ILogValue` writes the text form (§4); the subject is an opaque id (⚑AC8).

## 4. Text form and `ActorReference`

```text
kind ":" subject [ "@" home ]                user:8f3a2c@0=farmaviva/1=loja7   job:close-day@0=acme/1=loja7   anonymous:
```

```csharp
public static class ActorText
{
    public static string Format(in ActorIdentity actor);                 // lower-case kind names, stable forever
    public static bool TryParse(ReadOnlySpan<char> text, out ActorIdentity actor);
    public static bool TryFormat(in ActorIdentity actor, Span<char> destination, out int written);
}

/// A stored pointer to an actor in app records (ERP "assigned to", PDV "operator", Files "uploaded by").
/// Stored as the text form in one column; YFex.Data maps it (AC3).
public readonly record struct ActorReference(ActorIdentity Actor)
{
    public static implicit operator ActorReference(ActorIdentity actor) => new(actor);
    public override string ToString() => ActorText.Format(Actor);
}
```

The text form is the **only** string representation: Notifications' `Recipient.Reference("user:42")` and Jobs' `job:` strings use it.

## 5. Canonical bytes (⚑AC4 clean break)

`ActorIdentity : ICanonicalValue<ActorIdentity>` — `kind (1 byte) · subject length (1 byte) · subject UTF-8 · home` where the home is
`TenancyPath`'s canonical bytes (`length (2 bytes, little endian, 0 = none) · UTF-8`). `MaxCanonicalLength` = 1 + 1 + 128 + 2 + 512. Used by the Offline envelope and session (through
`CanonicalWriter.WriteValue`), by Audit's entry codec (through `TryWriteCanonical` into its buffer), and by signed tokens. One
golden test in YFex.Actors fixes the bytes.

## 6. `ActorScope` and `ActorContext`

```csharp
public sealed record ActorScope(ActorIdentity Actor, ActorIdentity? RealActor = null, ActorRelation Relation = ActorRelation.None,
    string? SessionId = null, InstallationId? Origin = null)                                   // ⚑AC16
{
    public ActorIdentity EffectiveRealActor => RealActor ?? Actor;
    public bool IsOnBehalf => Relation != ActorRelation.None;
    public static ActorScope Anonymous { get; }

    public static ActorScope Of(ActorIdentity actor, string? sessionId = null);
    public static ActorScope Delegated(ActorIdentity actor, ActorIdentity by);                 // job runs as owner
    public static ActorScope Impersonated(ActorIdentity actor, ActorIdentity by, bool tenantOnly = false);
    public static ActorScope OnBehalf(ActorIdentity person, ActorIdentity agent);              // agent acts for a person
}
```

`Origin` is the installation where the work was started (the terminal a sale was rung on, the machine a job runner runs on). The
writer that enters the scope sets it (desktop sign-in: the local installation; HTTP/RPC middleware: the calling installation from
its certificate; job runner: its own installation); `ActorContext` never guesses it. `Capture`/`Restore` carry it, so offline work
replayed at central keeps the terminal it came from; the replay itself is Offline provenance.

`ActorScope.From(IAuthorizationContext)` moves to YFex.Authorization as an extension (`caller.ToActorScope()`): the scope must not know
Authorization.

`ActorContext` keeps today's semantics (AsyncLocal scope over a process session, `Enter` / `SetSession`, single writer) and adds work
that crosses a **stored** boundary (outbox row, job queue, retry, offline replay):

```csharp
public static class ActorContext
{
    public static ActorScope? Current { get; }
    public static ActorScope? Session { get; }
    public static void SetSession(ActorScope? scope);
    public static Activation Enter(ActorScope scope);

    public static ActorSnapshot Capture();                     // Current, as a value (Anonymous when none)
    public static Activation Restore(in ActorSnapshot snapshot);
}

/// The actor of stored work: canonical bytes or text, persisted next to the work item.
public readonly record struct ActorSnapshot(ActorIdentity Actor, ActorIdentity? RealActor = null,
    ActorRelation Relation = ActorRelation.None, string? SessionId = null, InstallationId? Origin = null)
    : ICanonicalValue<ActorSnapshot>;
// bytes: actor · real presence [· real] · relation · session length (LE16) · session · origin presence [· installation id (16)]
```

`Restore` is a writer: only infrastructure (outbox dispatchers, job runners, Offline replay) calls it — covered by the analyzer (⚑AC9).

## 7. `ActorStamp` — "who did this" in records (⚑AC10)

```csharp
public readonly record struct ActorStamp(ActorIdentity Actor, ActorIdentity? RealActor, DateTimeOffset At,
    InstallationId? Origin = null)                              // ⚑AC16
{
    public static ActorStamp Now(TimeProvider clock);           // from ActorContext.Current, origin from the scope
}
```

An app record declares stamps; YFex.Data's generator fills them (insert: `Created` and `Changed`; update: `Changed`) when the row type
opts in. Stored as two text columns + a timestamp + an optional installation id (`created_by`, `created_by_real`, `created_at`,
`created_on`). Stamps are not an audit trail: they
answer "who touched this last" cheaply; Audit answers "what exactly happened".

## 8. The directory (names at read time)

Contract in `YFex.Actors`:

```csharp
public interface IActorDirectory
{
    ValueTask<ActorDisplay> DescribeAsync(ActorIdentity actor, CancellationToken cancellationToken = default);
    ValueTask DescribeManyAsync(ReadOnlySpan<ActorIdentity> actors, Span<ActorDisplay> results, CancellationToken cancellationToken = default);
}

public readonly record struct ActorDisplay(string Name, ActorState State, string? AvatarKey = null, string? Detail = null);

public enum ActorState : byte { Active = 0, Disabled = 1, Erased = 2, Unknown = 3 }

/// One per kind (or per subject prefix), registered by the library that owns the actors.
public interface IActorNameSource
{
    ActorKind Kind { get; }
    ValueTask<ActorDisplay?> FindAsync(ActorIdentity actor, CancellationToken cancellationToken);
}
```

`YFex.Actors.Directory`: the composite routes by kind, batches (`DescribeManyAsync` for history grids), caches (⚑AC7), and renders
fallbacks: unknown → `"user:8f3a…"` shortened, erased → localized "removido (#h:3f2a)" from a catalog key (Privacy's erasure hash), so
an erased person never re-appears by name.

| Source | Library | Name from |
|---|---|---|
| `User` | YFex.Authentication (`AccountNameSource`) | account display name; `Erased` after deletion |
| `Device` | YFex.Installations | installation label (`Caixa 3`) + site name |
| `Job` | YFex.Jobs | template display name + instance label |
| `Agent` | YFex.Capabilities (agent registry) | agent display name |
| `Service` / `System` | app configuration (`AddYFexActors(a => a.Name(ActorIdentity.Service("marketplace"), "Marketplace")))` | static |

Notifications' contact sources are the sibling of this contract (actor → contacts) and stay in Notifications.

## 9. Reading the actor from a principal

```csharp
public static class ActorClaims
{
    public static ActorIdentity FromPrincipal(ClaimsPrincipal? principal);   // NameIdentifier ?? sub → User; "yfex.kind" claim for others
    public static string? SubjectOf(ClaimsPrincipal? principal);
}
```

Replaces `CallContext.SubjectOf`, `AuthorizationContext.SubjectOf` and the Tenancy projector's argument. The principal shape itself
stays Authentication's (07 §3); this only reads it.

## 10. AOT and performance

No reflection; `ActorIdentity` is a 3-field struct (two references + a byte). `ActorText.TryFormat` and `TryWriteCanonical` are
allocation-free; `Format` allocates one string. `ActorContext.Current` stays one AsyncLocal read + one volatile read. Benchmarks in
AC0: parse/format, canonical write, `Capture/Restore`.
