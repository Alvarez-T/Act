# 08 — Privacy, retention and access

Audit **consumes** YFex.Privacy ([../privacy/README.md](../privacy/README.md)). It never re-implements classification, masks, pseudonyms,
encryption, keys, consent, rights orchestration or legal holds. This file says which primitive audit uses where, and what is specific
to audit (reversibility for auditors, immutability, retention by chain).

## 1. Legal basis (why audit needs no consent)

Audit processing rests on **legal obligation** (fiscal/accounting retention, sector rules) and **legitimate interest / regular exercise of
rights** (security, fraud prevention, proving what happened) — LGPD art. 7 II, VI, IX. The app declares the purpose once in its
`[PurposeCatalog]` (privacy ⚑P8), e.g. `Purposes.AuditTrail` with basis `LegalObligation` and `Purposes.SecurityAudit` with
`LegitimateInterest`. Consent withdrawal never stops audit. Audit still minimises: it stores ids and protected values, not clear personal data.

## 2. Sensitivity → treatment for audit (⚑A21)

Logs must never need the value back. Audit **sometimes does** (an auditor must see which CPF was on the cancelled document). So the
defaults differ from logging:

| `DataSensitivity` (privacy ⚑P3, 7 values) | Logging default | **Audit default** | Read back by |
|---|---|---|---|
| `Public` | value | `Clear` | anyone with `audit.read` |
| `Internal` | value | `Clear` | anyone with `audit.read` |
| `Personal` (name, e-mail, phone) | hash/mask | `Encrypted` with the **subject key** + `Pseudonymized` side value for joins | `audit.reveal`; masked otherwise |
| `PersonalIdentifier` (CPF, RG) | hash/mask | `Encrypted` (subject key) + `Pseudonymized` | `audit.reveal` |
| `SensitivePersonal` (health, biometrics) | erase | `Encrypted` (subject key), **no pseudonym** (not even linkable) | `audit.reveal.sensitive` |
| `Financial` (card PAN, account) | erase | `Tokenized` (token-vault reference, privacy ⚑P14) when a token exists, else `Erased` | detokenization (audited by Privacy) |
| `Secret` (password, token, key) | erase | `Erased` — always, cannot be relaxed | nobody |

- The engine applies the treatment **on encode** (05 §1), from the field's classification (value-object type → convention → snapshot,
  privacy ⚑P5). Generated diff code only passes classified values; it never encrypts by itself.
- **Pseudonyms** use YFex.Privacy `Pseudonymizer` (HMAC-SHA256, HKDF per tenant + domain, privacy ⚑P11), so "every audit entry that touched
  this CPF" works without decrypting anything.
- **Free text** (reasons, string members) goes through YFex.Privacy `TextScrubber` (privacy ⚑P18): detected CPFs, e-mails, phones in a reason
  are replaced by the same treatment their value objects would get.
- Unclassified values that *look* personal are treated as `Personal` (fail closed, README constraint 7).
- `Digest` and `Omitted` (04 §2.3) are not personal-data treatments; a digest of a document that contains personal data is still a hash of
  high-entropy content (not brute-forceable like a bare CPF).

## 3. Subject keys and crypto-shredding (⚑A22)

**Which key encrypts a value?** The engine asks YFex.Privacy for the key of the **data subject** the value belongs to:

1. The target entity has a `[DataSubjectKey]` member (privacy ⚑P10, e.g. `CustomerId` on `ClienteEntity`) → that subject.
2. The captured message has a `[DataSubjectKey]` member → that subject.
3. Otherwise the per-**category field key** (privacy ⚑P12 "per-category field keys"), so the value is still protected and erasable by category.

**Erasure** (LGPD art. 18 VI) deletes the subject key in the YFex.Privacy vault. Audit entries are unchanged (ciphertext bytes stay, chains
stay valid); the values read back as "erased" with the erasure date. Pseudonyms stay unlinkable to a person once the tenant's pseudonym key
version is rotated if required.

**Erasure vs legal hold.** A customer can ask for erasure while their CPF must stay on fiscal audit for 5 years. Two possibilities (to settle
with the Privacy design, 01 §5):

| Option | How | Trade-off |
|---|---|---|
| **A (recommended)** | subject keys **scoped by retention class** (`subject × class`): erasure shreds the keys of classes not on hold now, and the hold's key is shredded automatically when the hold ends | precise; one more key per subject per class |
| B | one subject key; the rights orchestrator refuses erasure while any hold exists (privacy ⚑P21) and schedules it for the hold's end | simpler; the subject's non-fiscal data stays readable until then |

`YFex.Audit.Server` implements YFex.Privacy.Rights' **`IPersonalDataStore`** participant (privacy ⚑P20):

| Rights step | Audit participant does |
|---|---|
| Locate | find entries whose targets/fields carry the subject's pseudonym or `[DataSubjectKey]` value (indexed in `audit.targets`/`field_changes` projections by pseudonym) |
| Export (portability/access) | a **partial `.yaudit` export** (07 §8) of the subject's entries, revealed, with Merkle inclusion proofs |
| Correct | nothing: audit is history ("the value *was* X"); a correction is a new business action, audited as such |
| Erase | nothing to delete; confirms the key shred (done by the vault) and records `Privacy.SubjectErased` (category `privacy`) |
| Holds | reports which of the subject's audit classes are under legal hold (fiscal), shown in the response with legal basis and end date |

## 4. Legal holds

Holds are **owned by YFex.Privacy.Rights** (privacy ⚑P21: declared by modules, enforced by a sweeper). Audit only queries them:

- Purge (§5) asks "is `(tenant, class, day)` under a hold?" before deleting a chain.
- A fiscal module declares its hold once (e.g. `p.Retain(category: "fiscal", years: 5)` in the Privacy configuration); the audit retention
  class `fiscal-5y` and the Privacy hold must agree — `AddYFexAuditServer` validates at startup that every class's `keep` ≥ the longest hold
  declared for the categories mapped to it, and fails fast otherwise.
- Litigation holds (a court order on one tenant) are added at run time in Privacy and immediately block purges of the affected chains.

## 5. Retention (⚑A23)

```csharp
// (sketch) server configuration — retention classes are app-declared names (open set)
.Retention(r => r
    .Class("standard",  hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(730))
    .Class("security",  hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(1_825))
    .Class("fiscal-5y", hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(1_855), requireExport: true, requireComplete: true)
    .Map("fiscal", "fiscal-5y"))
```

| Setting | Meaning |
|---|---|
| `hot` | stays in the Postgres tables, searchable (all of it, by default) |
| `keep` | total retention; after it the chain day may be purged |
| `requireExport` | a successful WORM export (06 §8) must exist before purge |
| `requireComplete` | members of messages in this class may not be excluded with `[NotAudited]` / `.Ignore` (YFA0030) |

**Purge job** (`audit.retention.purge`, YFex.Jobs, daily) for each `(tenant, class, day)` past `keep`:

1. Day sealed and `Verified`; no open gap involving its entries; export present when required; **no legal hold** (Privacy).
2. Write a **tombstone** (`audit.tombstones`): final index, final hash, Merkle root, purge time, policy, signed with the anchor key.
3. With `yfex.audit_retention = on`, delete the chain's rows (entries, targets, field changes, seals, anchors) — or drop the partition when it
   contains only purgeable chains.
4. Write `Audit.ChainPurged` (category `administration`, class `security`), so the purge itself is audited in a chain that outlives it.

The next day's genesis (06 §4) still verifies against the tombstone's final hash, so continuity survives retention.

## 6. Permissions

Names are configurable (app-declared, YFex.Authorization); the defaults:

| Permission | Allows |
|---|---|
| `audit.read` | read entries of the caller's tenant scope (prefix: a store manager sees `acme/loja7/*`), values masked |
| `audit.read.all` | operator: all tenants (platform staff); each use is audited with the target tenant |
| `audit.reveal` | decrypt `Personal`/`PersonalIdentifier` values in one entry view or export |
| `audit.reveal.sensitive` | decrypt `SensitivePersonal` values |
| `audit.export` | produce `.yaudit` packages |
| `audit.verify` | run verification on demand, see verification details |
| `audit.review` | mark review-queue items as reviewed, close gaps (with reason) |
| `audit.admin` | retention configuration, signing key rotation |

A data subject never gets `audit.read`: their access goes through the rights request (§3, partial export).

## 7. Meta-audit: reading the audit is audited (⚑A24)

- Each `AuditQueries.*` execution writes **one** access entry (category `access`, class `security`): actor, filter (with protected values
  pseudonymized), result count, page. Not one entry per row.
- Each **reveal** writes its own entry with the entry id and the revealed field paths (never the values).
- Each export writes an entry with the manifest hash; each verification run writes its summary.
- Meta-entries are produced by the server through the same `IAuditTrail`, so they are chained and tamper-evident like everything else.
  They are excluded from the default history views (filter `Category != access`) to avoid "audit of the audit of the audit" noise.

## 8. Data residency and minimisation

- The server resolves tenant → region like logging (logging 08 §3); audit stores are per region; exports carry the region in the manifest.
- Entries never contain display names, e-mails or IPs in clear: actors are subject ids; IP addresses, when captured for security entries, are
  `PersonalIdentifier`-treated (encrypted + pseudonym).
- Device ids are pseudonymous installation ids (shared with logging's `NodeIdentity.DeviceId`).
