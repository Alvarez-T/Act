# 12 — Storage

## 1. Store contracts (engine)

| Contract | Holds | Hot? | Implementations |
|---|---|---|---|
| `IAccountStore` | accounts, contacts, status, security stamp | per sign-in | in-memory, `.Data` |
| `ICredentialStore` | password/PIN/site verifiers, sealed TOTP secrets, recovery-code verifiers, passkeys, operator codes | per sign-in | in-memory, `.Data` |
| `IExternalLoginStore` | `ExternalAccountKey` → account | per external sign-in | in-memory, `.Data` |
| `ISessionStore` | sessions, refresh families, revoked-session change feed | refresh + revocation | in-memory, `.Data` |
| `IChallengeStore` | challenges (code MAC, attempts, state), consumed flow ids | per code check | in-memory (single node), `.Data` |
| `IThrottleStore` | counters (11 §2) | per attempt | in-memory, `.Data` |
| `IScopedProviderStore` | per-scope identity providers (06 §6) | cached | `.Data` |

All take `CancellationToken`, return `ValueTask`, and expose **compare-and-set** operations where races matter (consume a challenge,
rotate a refresh generation, bump the security stamp) — the engine never does read-modify-write across two calls.

## 2. Tables (`YFex.Authentication.Data`, YFex.Data generated mapping, no Dapper)

Entities are 1:1 table shapes (model-layering rule). Sketch, PostgreSQL types; SQL Server/SQLite mappings follow YFex.Data.

```text
authentication_accounts        id uuid pk · realm text · status smallint · display_name text · culture text · home_scope text
                               security_stamp bigint · created_at · last_sign_in_at · deleted_at
authentication_contacts        id uuid pk · account_id fk · kind smallint (email|phone) · value_sealed bytea · lookup_key bytea (MAC)
                               verified_at · primary bool · sign_in bool · recovery bool · preferred_channel text
                               unique (realm, kind, lookup_key) where verified
authentication_credentials     id uuid pk · account_id fk · kind smallint · verifier bytea · pepper_key text · secret_sealed bytea
                               operator_code text (unique per realm) · created_at · last_used_at · last_totp_step bigint
authentication_external_logins account_id fk · provider text · issuer text · subject_lookup bytea (MAC) · subject_sealed bytea
                               linked_at · last_used_at · pk (provider, issuer, subject_lookup)
authentication_sessions        id uuid pk · account_id · realm · methods int · assurance smallint · authenticated_at · client smallint
                               installation_id uuid · real_actor text · security_stamp · created_at · last_seen_at
                               idle_expires_at · absolute_expires_at · state smallint · ip_sealed bytea · user_agent_sealed bytea
authentication_refresh_tokens  token_hash bytea pk · family_id uuid · generation int · session_id fk · installation_id · expires_at · used_at
authentication_challenges      id uuid pk · purpose text · realm · account_id · channel text · destination_key bytea · code_tag bytea
                               security_stamp · created_at · expires_at · attempts smallint · state smallint
authentication_consumed_flows  flow_id uuid pk · expires_at
authentication_throttle        key bigint pk · window_start · failures int · locked_until
authentication_providers       scope text · name text · descriptor jsonb (no secrets; secret references only) · pk (scope, name)
```

As built (P3b): `authentication_external_logins` has pk (realm, subject_lookup) — the MAC already covers provider, issuer and subject —
and `subject_sealed` nullable; `authentication_providers.descriptor` is portable text (JSON written by `ScopedProviderDescriptor`) plus
`updated_at` and `version` (compare-and-set).

## 3. Personal data at rest

- **Lookup without plaintext:** e-mail and phone are found by `lookup_key = MAC(realm ‖ canonical value)` (ring
  `authentication/lookup`, per-realm derived through `ScopedKeyRings`), and stored **sealed** (`SealedValue`) for display and sending.
  A database dump reveals neither addresses nor which realm an address belongs to.
- gov.br CPF subjects: same pattern (`subject_lookup` + `subject_sealed`).
- Session IP/user agent sealed, erased by the retention job after the configured period.
- Erasure (LGPD) = delete contact rows and sealed columns, keep the account tombstone (`deleted_at`, status `Deleted`).

## 4. Client side

The desktop client keeps: sealed refresh token, last realm, last operator code (not secret, convenience), the offline verifiers
**owned by Offline**. Everything through `IKeyValueStore` with `AddYFexStorageEncryption` (10 §4).

## 5. Farms

Several API nodes share the `.Data` stores. The revoked-session set is fed from `authentication_sessions` state changes (poll every
5 s by default, or the app's change feed / Messaging event); the validated-token cache is per node and bounded by the token
lifetime, so a revocation is effective on every node within the poll interval (documented maximum), and immediately on RPC through
`IAuthorizationSignals.RevokedAsync`.
