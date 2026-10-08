# 07 — What runs where

## 1. Matrix per component

Columns: **C** = central. **B** = site broker while central is unreachable. **T·c** = terminal in `Central` mode. **T·s** = terminal in
`Site` mode. **T·a** = terminal alone (`Standalone`).

| Component | C | B | T·c | T·s | T·a |
|---|---|---|---|---|---|
| **App server handlers** | ✔ (only here) | ✘ never | — | — | — |
| **CQRS dispatch** | `LocalDispatcher` + handlers | generic broker services only (no app messages) | `FusionMessageBus` → central peer | `OnOffline<T>` handler locally, after broker coordination; `Unavailable` for central-only | `OnOffline<T>` locally with own slice/claims; `Unavailable` otherwise |
| **Shape validation** (YFex.Validation, generated) | ✔ | — | ✔ (before sending) | ✔ | ✔ — same generated code, deterministic everywhere |
| **Rules** (YFex.Rules) | latest version | — (holds the signed guardrails it needs for approval caps) | ✔ (UI constraints) | ✔ latest signed version + store overrides from the feed | ✔. Envelopes cite `RulesetVersion` + fingerprint |
| **Authorization** | authoritative, current data | answers from the site authorization bundle (signed, 72 h) | snapshot for UI only, server decides | evaluates with the snapshot; the user's session comes from the broker | snapshot decides (signed, 72 h) minus `OfflineRestrictions` |
| **Sign-in** | identity provider | verifies against the site credential-verifier bundle; issues site sessions | IdP + cached-verifier refresh | via broker (PIN allowed) | cached verifier (passwords only) |
| **Approvals** | HMAC tokens | **site-signed** tokens, site daily caps | — | requested from the broker | device-signed (D34), capped, flagged |
| **Budgets** (`OfflineAllowance`) | ledger of record; grants per site | holds the site budget, reserves for terminals, cuts slices | — | reserves via the broker | spends its own slice |
| **Unique claims** | authoritative | store-wide claims until central confirms | — | claims via the broker | provisional claims |
| **Site feed** | — | orders and relays `SharedWithSite` envelopes (doesn't interpret them) | — | sends its shared changes; runs `ISiteFeedProjection<T>` for others' | applies its own; shares later |
| **Offline queue** | idempotency store (Phase 0, F3) + reconciliation pipeline | **site journal**: custody of terminal envelopes, forwards to C | outbox empty (normally) | outbox → custody to B on receipt | outbox (custody until B or C receipt) |
| **Client caches** (`ICacheable`) | — (source of truth) | ✘ none | local cache | local cache | local cache (stale flags shown) |
| **Trusted clock** | source of anchors | anchor for the LAN (signed time) | anchored by C | anchored by B | high-water + monotonic only |
| **Logging** (YFex.Logging) | ingest server | **relay** (`AddYFexLoggingRelay`, logging 05 §6) | spool → C | spool → B relay | spool on disk |
| **Audit** (YFex.Audit) | inbox + sequencer | **relay** (forwards signed batches unchanged, audit 05 §6) + its own journal | journal → C | journal → B | journal (installation chain, device-signed heads) |
| **Tenancy scope** | any scope (by caller) | pinned to its site | pinned to its site | pinned | pinned |
| **Events / live state** | Fusion compute + server-pushed events | pushes broker events to terminals (approval issued, budget changed, feed entry) | live | feed + broker events | local only. Events are notifications, not facts: they are **not** queued. Facts travel as commands |
| **Jobs** (YFex.Jobs) | hub | ✘ (no app jobs on the broker) | runs its own `FailOpen` jobs | same | same; `FailClosed` jobs wait |
| **Privacy** (YFex.Privacy) | vault, subject keys | no subject keys; cannot read sealed personal fields | — | seals new personal data for C (intake key, privacy 05 §5.5) | same |
| **Fiscal (NFC-e)** and other external services | — | — | direct | direct if internet is up (situation 2b) | contingency emission, transmission queued with **priority** ([08](08-sync-reconciliation-conflicts.md) §6) |

## 2. What the site broker process hosts

```
 PC-GERENTE — Windows service "YFex Site Broker" (generic YFex.Offline.Broker host; Kestrel :7443, TLS 1.3, client certificates required)
 ├─ Sessions: verify code + PIN/password (site verifier bundle), issue terminal-bound session tokens
 ├─ Permission answers: site authorization bundle evaluated by the YFex.Authorization library
 ├─ Approvals: site-signed tokens, daily caps
 ├─ Coordination: budget reservations + terminal slices, unique claims
 ├─ Custody: site journal of signed envelopes, receipts, forwarding to central in site-sequence order
 ├─ Site feed: orders and pushes SharedWithSite envelopes to terminals (opaque relay)
 ├─ Bundle distribution: manifest, revocations, rule data, snapshots for terminals (re-served signed bytes, never re-signed)
 ├─ Relays: logging spool relay, audit batch relay (end-to-end signed: the relay needs no trust)
 ├─ Time anchor, LAN beacon, pairing endpoint (when enabled)
 └─ Client of central (same RPC client stack as a terminal, role SiteBroker)
 No app assembly is referenced (architecture test). The manager's desktop app on the same PC is an ordinary terminal.
```

**Hosting (⚑O20):** a Windows service, so the store keeps working when the manager logs off or closes the app. The service runs under
its own low-privilege account, and its installation keys are machine keys in the TPM. Because the host is generic, it is installed
once per site and updated only when the YFex.Offline protocol changes, not with every app release.

## 3. Central unreachable, internet up (situation 2b)

External integrations never route through central for their availability decision. Each integration declares what it needs:

| Integration | Needs | Without central, internet up | Without internet |
|---|---|---|---|
| NFC-e authorization (SEFAZ) | internet | normal emission from the terminal | contingency emission; transmission queued with priority and its legal deadline tracked |
| Card payment (TEF / PIN pad) | acquirer link | normal | depends on the acquirer (usually unavailable) |
| Government program sales that need an online authorization per sale (e.g. Farmácia Popular) | internet + the program's system | available if the integration runs on the terminal | **unavailable** (`Error.Unavailable("requires internet")`) |
| E-mail/SMS notifications | internet | queued, sent later through central | queued |

YFex.Offline reports `Reachability`. An integration adds its own probe (e.g., SEFAZ status service) to decide. YFex.Offline does not
know these services; the app or the fiscal module does.

## 4. Data the broker keeps (and does not)

| Kept on the broker | Never kept |
|---|---|
| its own installation keys (TPM), the tenant trust anchor | the tenant CA root key, bundle-signing keys, the online approval HMAC key |
| signed bundles for the site (manifest, revocations, rule data and guardrails, site authorization, credential verifiers, budgets) | subject keys, the tenant KEK, the intake private key |
| site journal (envelopes in custody, receipts), budget ledger, unique claims, site feed | app business data, app caches, database credentials |
| its own audit journal and log spool, relayed batches until acknowledged | other sites' data |

Terminals keep their own `ICacheable` caches warm while online (the app decides what, e.g. catalog + prices + stock of this store). The
broker does not mirror business data.
