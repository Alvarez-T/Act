# 07 — Sessions, tokens and validation

## 1. Flow state between steps

A sign-in may take several requests (identify → password → second factor → verify phone). The intermediate state travels as a
**`SignInFlowToken`**: a `MessageToken` (ring `authentication/codes`, domain `yfex.authentication.flow.v1`, 10 min) whose payload is
canonical bytes: flow id, realm, account id (when known), methods already proven (`AuthenticationMethodSet`), pending step, security
stamp, installation id, client kind. Servers stay stateless between steps (a farm needs no sticky sessions); a replayed flow token
is caught because completing a step **consumes** the flow id (single-use set with the token's lifetime, 12 §2).

## 2. Session

```csharp
public sealed record SessionRecord(
    SessionId Id, AccountId Account, AccountRealm Realm, string? HomeScope,
    AuthenticationMethodSet Methods, AssuranceLevel Assurance, DateTimeOffset AuthenticatedAt,  // last full or step-up proof
    ClientKind Client,                          // Browser | Desktop | Mobile | Rpc
    InstallationId? Installation,               // bound installation (⚑AN16)
    ActorIdentity? RealActor,                   // impersonation (§7)
    long SecurityStamp,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt,
    SessionState State);                        // Active | SignedOut | Revoked(reason) | Expired
```

| Client | Idle | Absolute | Notes |
|---|---|---|---|
| browser | 30 min | 12 h (staff) / 30 days with "keep me signed in" (shoppers) | sliding on activity, cookie refreshed at most once per 5 min |
| desktop / POS | refresh token 12 h | 7 days | shared terminal locks after 5 min (10 §2) |
| mobile | 30 days | 90 days | |
| impersonation | 15 min | 1 h | §7 |

All numbers are per-realm options with bounds; Offline's offline session ≤ 12 h (⚑O12) is separate and Offline's.

### 2.1 Session profiles per application and the off-site rule (⚑AN26–AN27, 2026-10-20, design)

Optional. Without a profile the table above applies. A profile is chosen by the **application name of the bound installation**
(installations `InstallationKeyRecord.ApplicationName`), so a stock handheld and a shopper app can differ in the same realm.

```csharp
// (sketch)
auth.Sessions(s => s
    .ForApplication("farma.coletor", p => p
        .Lifetime(idle: TimeSpan.FromMinutes(30), absolute: TimeSpan.FromHours(12))   // e.g. one shift; any value, all optional
        .LockAfter(TimeSpan.FromMinutes(5))                                             // sent to the client (10 §2)
        .OffSite(after: TimeSpan.FromHours(4), minimum: AssuranceLevel.MultiFactor,     // optional
                 action: OffSiteAction.Reauthenticate)));                               // or Lock / SignOut
```

- **Presence** comes only from Installations: a `SitePresence` statement signed by the store's broker or the device's sponsor terminal,
  attached to the installation assertion and verified by central (installations 11 §5). The refresh records the verified time in the
  session and in the access token claim **`presence_at`** (Authorization reads it for `RequireSitePresence`, ⚑Z20).
- **Off-site rule:** when `now − presence_at > after` (or no presence ever), the next refresh answers `ReauthenticationRequired(minimum)`.
  The client shows the re-authentication screen and calls the existing step-up (09 §4.1). A successful step-up restarts the off-site
  clock. `Lock` locks the screen (unlock = PIN, 10 §2), and `SignOut` ends the session.
- Everything is configurable per profile: hours, the minimum level (`SingleFactor` lets a PIN satisfy it), the action. IP address, SSID
  and network fingerprint are **never** inputs.
- **Revoked installation (⚑AN28):** Installations' revocation event ends every session bound to the installation at once (refresh
  families revoked, `sid` added to the revoked-session set).

## 3. What the tokens and the principal say (⚑AN15)

**Access token** (JWT, `typ: at+jwt`, RFC 9068 shape), signed ES256 by ring `authentication/tokens`:

| Claim | Value | Read by |
|---|---|---|
| `iss`, `aud` | configured issuer; audience per API (`yfex-farma`) | validator |
| `sub` | `AccountId` text | `CallContext.SubjectOf`, `ClaimType` → `NameIdentifier`, Tenancy projector user rung |
| `sid` | `SessionId` | revocation, `ActorScope.SessionId`, audit |
| `iat`, `exp`, `jti` | issue, +10 min, random 128-bit | validator; `exp` also by `CallContext.ExpiryOf` |
| `auth_time`, `acr`, `amr` | last proof; assurance (`yfex:aal1/2/3`); methods (RFC 8176 values + `yfex:*` for the rest) | step-up requirements (09 §4) |
| `scope_home` | canonical home scope (`0=farmaviva/1=loja-centro`) when the account has one | `ClaimTenantStrategy("scope_home")`, `ActorIdentity.HomeScope` |
| `realm` | canonical realm | multi-realm services |
| `installation` | `InstallationId` when bound | Authorization (scope pinning with ⚑I11), audit |
| `act` | `{ "sub": "<real actor>" }` only for impersonation | Authorization `RealActor` + `UserImpersonation` |
| app claims | through `IAccessTokenClaimsContributor` (explicit, size-limited to 512 bytes) | the app |

**Never in a token:** permissions, roles, e-mail, phone, name (privacy and staleness; Authorization resolves permissions by
subject + scope + version and caches them).

**Principal** built from a validated token or a cookie session (one builder, so HTTP, RPC and desktop agree — finding D3):
`ClaimsIdentity(authenticationType: "YFex", nameType: NameIdentifier)` with claims `NameIdentifier`=`sub`, `sub`, `sid`, `exp`,
`auth_time`, `acr`, `amr` (one per method), `scope_home`, `realm`, `installation`, and `act` as an actor identity
(`ClaimsIdentity.Actor`). The principal object is **cached with the validated token** (§5) so Authorization's context cache keys on a
stable instance.

## 4. Refresh tokens (⚑AN8, ⚑AN16)

- 32 random bytes, base64url, sent once; stored as `Hash256` (SHA-256 is enough: the token is high-entropy), with **family id**,
  generation, session id, binding (`InstallationId` or none), expiry.
- **Rotation**: every refresh returns a new refresh token and invalidates the old one. Presenting an **old generation** of a family
  (theft: two parties hold it) revokes the **whole family and the session** and emits `RefreshTokenReuseDetected`. A 10-second
  grace accepts the immediately previous generation once (lost response on a flaky shop network) and returns the *same* successor.
- **Installation binding**: a bound token is accepted only when the refresh request carries a valid installation assertion of that
  installation (Installations.Rpc `CallContext` installation, or the signed assertion header over HTTP, ⚑I11). A refresh token
  copied from CAIXA-02's disk to another PC is useless.
- Refresh also re-checks account status and `SecurityStamp`; a disabled account or changed password ends the family.

## 5. JOSE implementation and the hot path (⚑AN10)

`JsonWebToken` (internal to the engine; the external-token verifier of 06 §4 shares it):

```csharp
// Issue (cold-ish: once per sign-in/refresh): writes header+payload with Utf8JsonWriter into a pooled buffer,
// base64url into the destination span, signs through SigningKeyRing (ES256, IEEE P1363 64-byte signature).
int written = JsonWebToken.WriteAccessToken(in AccessTokenClaims claims, SigningKeyRing ring, Span<char> destination);

// Validate (hot: every request / RPC connect)
TokenValidation result = validator.Validate(ReadOnlySpan<char> token, in ValidationContext context);
```

Validator steps, all on spans:

1. **Cache probe first**: key = `string.GetHashCode(ReadOnlySpan<char>)` of the token text (BCL, per-process randomised Marvin, so an
   attacker cannot aim collisions; `System.IO.Hashing` would be a new package) → bounded cache (`ConcurrentDictionary` keyed by that
   hash, buckets hold the full token for `SequenceEqual`, clock-based eviction, 10 000 entries) of `AuthenticatedCaller` until the
   token's `exp`; on hit, compare the full token and check the **revoked-session set** → done. Target **< 150 ns, 0 B**.
2. Miss: split on `.`, decode header/payload into `stackalloc byte[1024]` (pooled above that), `Utf8JsonReader` over the header:
   `typ` must be `at+jwt`, `kid` selects the key, `alg` must equal the key's algorithm (only ES256 for our tokens).
3. Verify the signature (`SignatureVerifier` from the ring's public key directory, cached per `kid`).
4. Payload: `iss`, `aud`, `exp`/`nbf` against `TimeProvider` (skew 30 s), `sid` not in the revoked-session set, `installation`
   matches the caller's installation when the endpoint knows it (`BindingMismatch` otherwise).
5. Build `AuthenticatedCaller` (principal + parsed facts) once, insert into the cache. Target **< 120 µs** (dominated by ECDSA verify),
   allocations only for the cached caller.

No LINQ, no `string.Split`, no `JsonDocument`, no reflection; claim names are `u8` literals compared with `ValueTextEquals`.

## 6. Revocation and sign-out

| Event | Effect |
|---|---|
| sign-out | session `SignedOut`; refresh family ended; `sid` added to the **revoked-session set** (in memory, replicated through the session store's change feed in a farm) for 10 min + skew (the longest access-token life) |
| admin revoke / account disabled / password reset / factor removed | all sessions of the account revoked the same way; `IAuthorizationSignals.RevokedAsync(subject)` → Messaging.Rpc closes the person's connections at once |
| key compromise | rotate `authentication/tokens` and retire the old key: every token signed by it fails `UnknownKey` |

**JWKS** (`MapYFexJsonWebKeys`) publishes the ring's active + previous public keys (P-256 `kty: EC`, `kid` = `KeyId` text) with
`Cache-Control: max-age=3600`, so other YFex services (another API, a reporting service) validate our tokens offline (⚑AN7: no
other OP endpoints).

## 7. Impersonation (⚑AN17)

```csharp
// Rita (vendor support) helps Ana. Authorization decides; Authentication only issues.
ImpersonationResult r = await impersonation.StartAsync(target: ana.Id, reason: "chamado 4711", ct);
// requires caller permission authentication.impersonate-user (+ an approval if the realm configured one),
// a MultiFactor session of Rita within 15 min, and Ana's realm in Rita's allowed realms.
```

- A **new** session for Ana's subject with `RealActor = Rita`, token claim `act.sub = Rita`, 15 min idle / 1 h absolute. Rita's own
  session is untouched; ending impersonation simply discards the impersonated tokens.
- Impersonated sessions **cannot** change credentials, contacts, factors, or sign out Ana's sessions (those endpoints check
  `RealActor is null`), and Authorization's `ImpersonationRestrictions` still apply.
- `ImpersonationStarted/Ended` events carry both actors (audit).

**As built (P3b):** `ImpersonationService.StartAsync(authorization, caller, target, reason, context)` / `EndAsync(impersonated)`;
`ImpersonationOptions` (`Lifetime` 15 min / 1 h, `MinimumAssurance` MultiFactor, `MaxAuthenticationAge` 15 min, `AllowAcross(from, to)`);
endpoints `POST impersonation/start { account, reason }` (fails closed without Authorization's `IAuthorizationContextFactory`) and
`POST impersonation/end`. Impersonated sessions get tokens only (no cookie). Authorization still has to read `ClaimsIdentity.Actor` into
`RealActor`/`UserImpersonation` (R4, 14 §2.1).

## 8. Messaging.Rpc (`YFex.Authentication.Rpc`)

```csharp
services.UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader("Authorization").WithTenant()))
        .AddYFexAuthenticationRpc();   // registers ICallTokenValidator + revocation observer
```

- `FromConnection()` (default): the WebSocket upgrade request went through our ASP.NET handler, so `HttpContext.User` is already our
  principal. `ICallTokenValidator` covers per-call header tokens: it runs the same validator and returns
  `new CallIdentity(principal, accessToken, scope)` only when `requestedScope` is the token's home scope or beneath it — a broader
  scope is Authorization's decision, so the validator passes the token's scope and lets the gate deny (never widens).
- Access-token expiry on a long-lived connection: the client refreshes at 80 % of the lifetime; apps send the token **per call**
  (`rpc.CallContext(ctx => ctx.SendToken(AuthenticationClient.RpcToken))`, server `FromConnection().OrFromHeader("Authorization")`),
  so no reconnect is needed (AN-P4). Expired identities already run as anonymous (CallContextOptions remarks).

## 9. HTTP endpoints (`MapYFexAuthentication(prefix)`)

| Method + path | Body / query | Result |
|---|---|---|
| `GET providers` | `realm`, `scope?`, `login_hint?` | public provider list (06 §6) |
| `POST sign-in/password` | identifier, password, `flow?` | `SignInResult` |
| `POST sign-in/operator` | operator code, PIN | `SignInResult` |
| `POST sign-in/code/start` · `…/complete` | identifier + channel · challenge + code | `ChallengeStartResult` · `SignInResult` |
| `POST sign-in/second-factor` · `…/second-factor/code` | flow, method, code (+ challenge, `rememberDevice`) · flow + code method | `SignInResult` (`secondFactorRequired` lists `allowed` and `mustEnrol`) · `ChallengeStartResult` (AN-P5) |
| `POST sign-in/enrolment/start` · `…/confirm` | flow (`mustEnrol`) · flow + enrolment + first code | enrolment (`keyUri`, `secret`) · `signedIn` + recovery codes (AN-P5) |
| `GET external/{provider}/start` · `GET/POST external/{provider}/callback` · `POST external/handoff` · `POST external/{provider}/id-token` | 06 §2 | redirect · `ExternalSignInResult` |
| `POST token/refresh` | refresh token (+ installation assertion) | new tokens |
| `POST sign-out` · `POST sessions/{id}/revoke` · `GET sessions` | — | — |
| `POST register` · `POST invitations/accept` | 08 §1 | `RegistrationResult` |
| `POST password/reset/start` · `…/complete` · `POST password/change` | 08 §3, 04 §3 | `ChallengeStartResult` · `RecoveryResult` |
| `POST contacts/{kind}/start` · `…/complete` · `DELETE contacts/{id}` | 08 §2 | |
| `GET factors` · `POST factors/authenticator/start` · `…/confirm` · `…/remove` · `POST factors/recovery-codes` | 09 §2, §5 (recent proof ≤ 5 min) | status · enrolment · `enrolled` + codes · `removed` · codes (AN-P5) |
| `POST step-up` · `POST step-up/code` | method, proof, `minimum` (`yfex:aal2`), `flow?`, `challenge?` · code method | `steppedUp` (same session, new access token) · `secondFactorRequired` · 401 · 429 (09 §4.1, AN-P5) |
| `POST device/start` · `POST device/approve` · `POST device/token` | realm/client · user code + approve (signed-in phone) · device code | device + user code · `approved`/`denied` · `pending` (400) / `signedIn` (10 §5, AN-P5) |
| `POST impersonation/start` · `…/end` | §7 | |
| `POST session/reauthenticate` | method (`password`, `operator-pin`, `offline-secret`), secret | `reauthenticated` · 401 · 429 — no new session (10 §2 unlock, AN-P4) |
| `POST credentials/offline-secret` | secret | `offlineSecretSet` · 400 policy · 401 sign-in too old · 403 not allowed (10 §3, AN-P4) |

JSON bodies are System.Text.Json source-generated (`AuthenticationJsonContext`); results are mapped to `200` with a discriminated body
(`"result": "secondFactorRequired"`), or `401`/`429` (`Retry-After`) — never `404` for an unknown account. Browser endpoints also
accept form posts and require the anti-forgery header or `SameSite` cookie + `Origin` check (11 §1).
