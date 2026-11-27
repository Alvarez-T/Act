# YFex.Inference — typed judgments from rules or models (proposal)

> Status: **proposal** (2026-11-18), no code, no repo. Decisions **⚑IF1–⚑IF43**, all pending (prefix `IF` checked unused in
> `docs/`; ⚑IF35–⚑IF43 and the mirror rows in other plans added 2026-11-18, §8). Research and the Search fit are in [search-capabilities/05](../search-capabilities/05-decision-models.md)
> (⚑SK45–⚑SK54). Working name; the name itself is ⚑IF1.

| File | Holds |
|---|---|
| README | summary, packages, ecosystem fit, requests, decisions, phases, per-OS, mirror rows in other plans |
| [01-use-cases](01-use-cases.md) | PDV / ERP scenarios by area (deterministic part, question, what the user sees, ⚑), UI/UX patterns, feedback loop, what it simplifies, where not to use it |

## 1. In one paragraph

Several libraries need the same small thing from AI: give it a text or a JSON *state* and a **closed question** (yes/no, one
of these options, a score on these levels, or "where in this text are names / health terms"), and get back **probabilities**,
not prose. Today they ask chat models for free text and parse it (`AiDecisionTrigger` checks `StartsWith("yes")`,
`AiSemanticWorm` asks for "one short word"). The new *decision models* (Jev, Microsoft-Decision-1.1 and open ones such as
GLiNER2.5-Decide, JevK5 and AnyJev) answer exactly this shape. YFex.Inference gives one provider-neutral contract with three
kinds of provider: **rules only** (always present, deterministic), **local** (in-process ONNX), and **remote** (decision APIs,
a self-hosted model server, or a chat agent as the last fallback). It **abstains** when nothing may or can answer, so every
consumer works without a model. A judgment is **evidence**: YFex.Rules and the app decide.

```csharp
services.AddYFexInference(i => i
    .Rules<ColumnShapeJudge>()                               // deterministic first
    .Onnx("gliner2.5-multi-decide", ModelFile.Verified(path, sha256))   // local, optional package
    .Http("microsoft-decision", web => web.Endpoint("foundry")) // remote, optional, per-tenant policy
    .Policy(InferencePurpose.DataCatalogue, p => p.LocalOnly().Deadline(TimeSpan.FromMilliseconds(500))));

JudgmentResult r = await judge.JudgeAsync(new(InferenceInput.Text(note), [new YesNoQuestion("personal", "Does it name a person?")],
                                              InferencePurpose.DataCatalogue), ct);
if (r.Outcome is InferenceOutcome.Answered && r.Get("personal").Yes >= 0.7) { … }    // Abstained → the rules-only path
```

## 2. Package map (proposed)

```
 YFex.Inference.Public      contracts and passive data: JudgmentQuestion union (YesNo, Choice, Score), Distribution,       → core
                            Judgment, JudgmentResult, InferenceOutcome, SpanLabel, FoundSpan, IJudge, ISpanFinder, IEmbedder,
                            InferenceInput, InferencePurpose, ModelId + version; no runtime packages
 YFex.Inference             router (rules → local → remote per policy), abstain, deadlines (Resilience), session cache        → .Public,
                            (Persistence ICache), input guard seam, Diagnostics probes, rules-only providers                    Resilience, Persistence
 YFex.Inference.Onnx        local encoder models (decision heads, span finders, embedders, rerankers) through ONNX Runtime    third-party (⚑IF25)
 YFex.Inference.Http        remote `state + questions` decision APIs (Jev, Microsoft Foundry, self-hosted OpenJev/AnyJev),    → YFex.Web
                            OpenAI-compatible local servers (llama.cpp) for GGUF models
 YFex.Inference.ChatModels  adapters: IAiAgent / IChatClient as an uncalibrated last-resort judge; IEmbedder ↔                → MEAI.Abstractions
                            IEmbeddingGenerator<string, Embedding<float>> (⚑IF20, ⚑IF27)
 bridges (named for the bridge, owned per ⚑SK23 / atlas F25):
   YFex.Privacy.Inference   PrivacyTextScanner (scrubber ∪ model spans), the remote-input guard from AiDataPolicy (⚑IF3, ⚑IF4)
   YFex.Search.Inference    intent signals, catalog embeddings, rerank stage (⚑SK45–⚑SK53)
```

## 3. Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md); outcomes per [ecosystem-fit §1](../../agent/rules/ecosystem-fit.md).

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex core (`src/YFex`) | used | `IExtractable<T>` (deterministic span finding the rules provider wraps), `Hash256` for model files, `[Union]`; no new core types (contracts stay in `.Public` until a second boundary must name them) | IF24 |
| core `Diagnostics` | used | probes: latency per provider, abstain rate, deadline misses, acceptance of suggestions; no raw text | IF32 |
| core `Localization` | not a fit | questions are instructions to a model, not user-facing text; option *labels* shown to users come from the consumer's catalog | — |
| core `Consent` | not a fit | processing is justified by Privacy purposes, not consent levels; `InferencePurpose` maps to them | IF4 |
| YFex.Persistence | used | session cache of judgments, stored embedding vectors | IF15 |
| YFex.Data | consumer; fits later | DataMining lives in its repo; vector columns for record search later | IF6 |
| YFex.Data.Placement / Tenancy.Registry (design) | not a fit | placement of tenants | — |
| YFex.Backup | not a fit | caches are rebuildable; model files belong to the install | — |
| YFex.Tenancy | used | per-scope policy: which providers, remote allowed or not, thresholds | IF16 |
| YFex.CustomLogic | fits | a tenant-specific rules judge | IF16 |
| YFex.Validation | not a fit | checks stay deterministic; a plausibility score would be a Rules warning | IF11 |
| YFex.Rules | fits; **lacks a feature?** | a judgment probability as a fact a rule reads (e.g. `p(fraud) ≥ 0.9 → NeedsApproval`); the verdict stays in Rules | IF11, D39 |
| YFex.Expressions | not a fit | no formula concern | — |
| YFex.Composer (design) | fits later | end-user screens could add a yes/no or choice question to a step | IF22 |
| YFex.Xml | not a fit | — | — |
| YFex.Cqrs | not a fit now | a judgment is not a command; an "ask" query may wrap it in the app | — |
| YFex.Messaging | fits later | handhelds asking a site-broker-hosted model over RPC | IF18 |
| YFex.State / NavigatR / Mvvm | not a fit | consumers' view models hold suggestions | — |
| YFex.UI | fits; **lacks a feature** | one suggestion control (chip with confidence, ghost default, paste preview) bound to a passive shape in `YFex.UI.Public`; UI never references Inference | IF35, UI116 |
| YFex.Authentication | not a fit | — | — |
| YFex.Authorization | not a fit | who may change inference policy is ordinary settings permission | — |
| YFex.Licensing | fits later | model-backed features as a licensed module, through the Authorization availability hook | IF22 |
| YFex.Capabilities | used through Search | help intent ("como faço sangria?") and settings search reach capabilities through Search's signals; no Capabilities change | IF43 |
| YFex.Search | consumer | intent boost, rerank, catalog embeddings through `YFex.Search.Inference`; help intent and settings search for Capabilities | IF5, IF43, SK45 |
| YFex.Files (design) | consumer | catalogue inbound partner CSVs before import (same column profiler as DataMining) | IF9 |
| YFex.Web | used | remote providers: per-scope endpoints and credentials, typed HTTP clients | IF13 |
| YFex.Cryptography | used | SHA-256 / `Hash256` verification of model files before load | IF17 |
| YFex.Privacy | consumer **and** used; **lacks a feature** | consumer: names, health terms, addresses in free text; data-subject search in free text; used: remote-input guard from `AiDataPolicy` / `PseudonymizationSession` (⚑P19, not built); `TextScrubber` must accept extra spans | IF3, IF4, IF6, IF41, P26–P29 |
| YFex.Logging | consumer, not on the hot path | redaction stays deterministic; a later Jobs pass samples logs for leaks; Alerts (design) groups exceptions and incidents | IF19, IF42, L22, L23 |
| YFex.Actors | used indirectly | an action taken on a judgment carries an agent/system origin through the executing pipeline | IF12 |
| YFex.Audit | used indirectly; **lacks a feature** | the executing pipeline records model id, version and probabilities (LGPD art. 20 review of automated decisions) | IF12, A32 |
| YFex.Time | used | `TimeProvider` for cache TTL and deadlines | — |
| YFex.Networking | not a fit | — | — |
| YFex.Installations | fits | model files inside signed bundles to sites | IF17 |
| YFex.Offline | fits later | rules only offline on handhelds; local models keep working on desktops | IF18 |
| YFex.Resilience | used | per-provider per-attempt timeout; no retries on interactive paths, retries for batch | IF14 |
| YFex.Orchestration | not a fit | a judgment is one step | — |
| YFex.EventSourcing | not a fit | — | — |
| YFex.Jobs | used by consumers | batch cataloguing, log leak sampling, re-embedding after a model change | IF21 |
| YFex.Cluster / Provisioning (design) | not a fit | per-process providers | — |
| YFex.Hosting (design) | fits later | one host per machine may host a shared local model for its modules and site terminals | IF18 |
| YFex.Installer (design) | fits | installs and updates model files (hundreds of MB); the library never downloads at run time | IF17 |
| YFex.Notifications | consumer (inbound) | triage of inbound replies and messages (queue, urgency) in an app inbound handler; Notifications stays the transport | IF36, N30 |
| YFex.Automation | consumer **and** provider | `AiDecisionTrigger` on `IJudge`; `IAiAgent` becomes the chat fallback provider | IF7, IF27 |
| YFex.System | not a fit | — | — |
| YFex.Profiling | **not a fit (deliberately)** | no profiling of people by models (Microsoft's terms exclude it; LGPD risk) | IF31 |
| YFex.Security | fits later | screening text sent to AI agents (prompt injection), as a yes/no judgment | IF10 |
| YFex.Devices | not a fit | — | — |
| YFex Forms (design) | consumer | paste / AI fill: `IExtractable<T>` per field first, then spans for names and addresses; the user confirms (⚑FM23); suggested defaults and duplicate warnings | IF8, IF35, IF40, FM51, FM52 |
| Domain: YFex.Fiscal, YFex.Finance, YFex.Farma | consumers | NCM/CFOP suggestions, bank reconciliation and chart of accounts, supplier item matching, prescriptions; options are their tables, purposes are theirs | IF39 |
| YFex.DataMining (in YFex.Data) | consumer | column semantics as a choice over the project's declared types, from local profiles (shapes, hit ratios) | IF6 |
| YFex.Brain | fits later | master-data enrichment through the Privacy session | IF22 |

Outside: `Microsoft.ML.OnnxRuntime` (MIT) and `Microsoft.ML.Tokenizers` (MIT) for `.Onnx` (⚑IF25);
`Microsoft.Extensions.AI.Abstractions` (MIT, already used by DataMining) in `.ChatModels` only (⚑IF20); model weights
(⚑IF30). LLamaSharp (in-process llama.cpp) was considered and rejected: native, large, AOT unknown. Run GGUF models in an
out-of-process server through `.Http` instead.

## 4. Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex.Privacy | `TextScrubber` accepts spans found elsewhere (merge, deterministic wins on overlap); `PrivacyLabels` for person name, health condition, address | no | IF3, P26 | IF-P3 |
| YFex.Privacy | build `AiDataPolicy` + `PseudonymizationSession` (⚑P19) so remote providers have a guard | no (designed) | IF4, P27 | IF-P1 (remote) |
| YFex.Privacy | rights tooling (07) uses the span finder to find a subject in free-text columns, as candidates | no (designed) | IF41, P28 | later |
| YFex.Privacy | `SemanticClassificationMap` (privacy 03 §5.3) fed by DataMining evidence | no (designed) | IF6, P29 | IF-P3 |
| YFex.Data (DataMining) | `ColumnProfiler` (shapes, `IExtractable<T>` hit ratios) and a typed worm replacing the free-text `AiSemanticWorm` | no (skeleton) | IF6 | IF-P3 |
| YFex.Automation | `AiDecisionTrigger` over `IJudge` with a threshold; `IAiAgent` adapter as a provider | yes for the trigger constructor (no callers outside tests) | IF7 | IF-P3 |
| YFex.Rules | a fact source that reads a judgment probability (optional) | no | IF11, D39 | later |
| YFex.Search | `YFex.Search.Inference` bridge (⚑SK45–⚑SK53) | no | IF5 | SK-P8 |
| YFex.Installer (design) | model files as install payload with hash | design only | IF17, IN9 | IF-P2 |
| YFex.Installations | model files inside signed site bundles (a bundle kind) | no | IF17, I45 | IF-P2 |
| YFex.Cryptography | none: built `Hashing.Sha256Async(Stream)` verifies model files | no | IF17, K20 | IF-P2 |
| YFex.UI | suggestion control + passive `FieldSuggestion` shape in `YFex.UI.Public` | no | IF35, UI116 | IF-P3 |
| YFex.Notifications | none in the engine; an inbound-handler pattern for triage, documented | no | IF36, N30 | later |
| YFex.Logging | Jobs pass for leak sampling; Alerts groups exceptions with a choice over known issues | no (design) | IF19, IF42, L22, L23 | later |
| YFex.Audit | an optional model-involvement part on audit entries (model id, version, probabilities) | no | IF12, A32 | IF-P3 |
| YFex.Offline / YFex.Hosting (design) | a host module serving a local model to site terminals | no (design) | IF18, O33, H23 | later |
| YFex Forms (design) | paste/AI fill through Inference; suggested defaults; duplicate warning | design only | IF8, IF35, IF40, FM51, FM52 | IF-P3 |
| YFex.Files (design) | inbound file cataloguing step in Exchange | design only | IF9, F23 | later |
| YFex.Composer (design) | a composed step that asks a user-defined question | design only | IF22, C6 | later |

## 5. Decisions (all pending)

Recommended option first. Integration and placement first (⚑IF1–⚑IF22), then internals.

| ⚑ | Topic | Proposal | Rejected alternatives | See |
|---|---|---|---|---|
| IF1 | Library and name | new repo **`YFex.Inference`** (`inference.slnx`), infrastructure, domain-neutral | `YFex.Decisions` (clashes with Rules verdicts); `YFex.Classification` (clashes with Privacy's `DataClassification`); `YFex.AI` (too broad, invites chat/generation); `YFex.Understanding`; placing it in Search, Privacy, Automation or core (05 §4) | §1 |
| IF2 | Packages and bridges | `.Public` + engine + `.Onnx` + `.Http` + `.ChatModels`; bridges `YFex.Privacy.Inference`, `YFex.Search.Inference` placed per ⚑SK23 / atlas F25 | one package with every runtime; bridges inside Inference | §2 |
| IF3 | Privacy direction | neither engine references the other; `YFex.Privacy.Inference` adds model spans to the scrubber; request: `TextScrubber` merges extra spans | Privacy references Inference (adds a third-party runtime to a BCL-only library); Inference references the Privacy engine (cycle with the scanner) | §3, 05 §5.2 |
| IF4 | Guard before remote providers | every remote call passes an input guard filled by `YFex.Privacy.Inference` from `AiDataPolicy` (⚑P19): cloud default nothing above `Internal` in clear, value shapes instead of samples; no guard registered → remote providers refuse | trust callers to scrub; scrub inside each HTTP adapter | 05 §3.7 |
| IF5 | Search | consumer through `YFex.Search.Inference` (⚑SK45–⚑SK54) | Search builds its own model layer | search 05 |
| IF6 | DataMining cataloguing | local `ColumnProfiler` + typed choice over the project's semantic types; deterministic hits become `Fact`, model answers `Suggestion`; feeds Privacy's `SemanticClassificationMap` | keep the free-text chat worm; send samples to cloud | 05 §5.1 |
| IF7 | Automation | `AiDecisionTrigger` uses `IJudge` with a threshold (abstain = do not fire); `IAiAgent` is the chat fallback provider (⚑IF27) | keep "yes" parsing; Automation owns the judgment contract | 05 §5.4 |
| IF8 | Forms paste / AI fill | per field `IExtractable<T>` first, then span finder for names/addresses, `find_record` for ids (⚑SK29); always shown for confirmation (⚑FM23) | model fills the whole form; no model | 05 §4 |
| IF9 | Files exchange | inbound partner CSVs profiled and catalogued before import with the DataMining profiler | per-partner hand mapping only | 05 §5.1 |
| IF10 | Security | prompt-injection screening of text sent to agents as a yes/no judgment, later | none; a separate classifier in Security | §3 |
| IF11 | Rules | judgments are facts a rule may read; the verdict (allowed / needs approval / denied) stays in Rules; optional fact source | a model returns verdicts; models inside Validation | §3 |
| IF12 | Audit and LGPD art. 20 | the pipeline that acts on a judgment records model id, version, probabilities and inputs' shapes with its audit entry; review by a person is possible | Inference writes audit; no record of model involvement | §3 |
| IF13 | Web | remote providers are YFex.Web typed clients with per-scope endpoints and credentials | raw `HttpClient` per adapter | §2 |
| IF14 | Resilience | per-provider per-attempt timeout; no retries on interactive purposes, retry budget for batch | own timers; retries everywhere | §3 |
| IF15 | Persistence | session cache (`ICache`, short TTL, keyed by model id + version + question set + input hash) and stored embedding vectors | no cache (flicker, cost); persistent cache of personal inputs | IF32 |
| IF16 | Tenancy / CustomLogic | per-scope settings choose providers, remote allowed, thresholds; CustomLogic for tenant judges | one global policy | §3 |
| IF17 | Model files | installed by the app or YFex.Installer, inside signed site bundles (Installations), verified by SHA-256 through Cryptography before load; never downloaded by the library at run time | download from Hugging Face at first use; unverified files | §3 |
| IF18 | Offline / Hosting / Messaging | handhelds rules only; later a model hosted once per machine (Hosting) or per site broker (Offline Phase 4), reached over Messaging RPC | model files on handhelds | search 05 §3.6 |
| IF19 | Logging | the log path stays deterministic; a later Jobs pass samples logs for leaks the scrubber missed | model scan on every log line | 05 §5.2 |
| IF20 | `Microsoft.Extensions.AI` | own small `IEmbedder` / `IJudge` in `.Public`; MEAI only in `.ChatModels` adapters (both directions for embeddings) | MEAI types in `.Public`; ignore MEAI | §2 |
| IF21 | Jobs | batch work (cataloguing, re-embedding after a model change, log sampling) runs as YFex.Jobs jobs owned by consumers | timers inside Inference | §3 |
| IF22 | Later consumers | Composer, Brain, Licensing: no work now, each fit recorded | — | §3 |
| IF23 | Operation kinds | three only: **judgments** (yes/no, choice, score), **spans** (labelled, with probability), **embeddings**; no text generation (Automation owns agents) | a general "AI client"; add summarize/generate | §1 |
| IF24 | Contract shape | mirrors the shared `state + typed questions` form: closed `JudgmentQuestion` union (YesNo, Choice with named options and descriptions, Score with ordered levels), `Distribution` per question, option names app-declared; `JudgmentSource` (Rules, Local, Remote + model id/version) | free-text answers; one vendor's JSON as the contract | §1 |
| IF25 | Local runtime | `YFex.Inference.Onnx` on ONNX Runtime + Microsoft.ML.Tokenizers (MIT); **spike IF-P0**: Native AOT publish, Linux glibc/musl, osx-arm64, cold start, RAM | LLamaSharp in-process; Python sidecar; only remote | §3 |
| IF26 | Remote providers | one HTTP adapter for the `state + questions` shape (Jev, Microsoft Foundry `…/v1/systemone`, self-hosted OpenJev/AnyJev); one OpenAI-compatible adapter for local llama.cpp servers | an SDK per vendor; vendor-specific contracts | search 05 §1 |
| IF27 | Chat fallback | `IAiAgent` / `IChatClient` asked to pick a numbered option; result marked **uncalibrated**, lowest priority, off unless enabled | no fallback; chat as the default judge | §2 |
| IF28 | Router and abstain | per purpose: rules → local → remote as policy allows; stop at the first answer above the purpose's threshold; `Abstained` (no provider, policy, deadline) is a normal outcome every consumer handles | throw when no model; silently guess | §1 |
| IF29 | Portuguese acceptance | before a model is enabled for a purpose: a PT-BR labelled set per purpose, accuracy and calibration (reliability bins) measured, thresholds set from it, kept as a regression test | trust vendor benchmarks; English sets | search 05 §2 |
| IF30 | Weight licences | only Apache-2.0 / MIT weights in products; non-commercial (CC BY-NC: OpenJev, StartLux ONNX) rejected; weights distilled from proprietary model outputs (JevK5) need the user's review | any open weights | search 05 §2 |
| IF31 | Evidence, not verdict | a judgment never decides alone about a person (credit, health, employment…), never profiles people; consumers show suggestions or feed Rules | auto-act on high probability | §1 |
| IF32 | Cache, determinism, telemetry | session cache (IF15); same input + model version → same answer; Diagnostics probes without raw text; model version in every judgment | live calls each time; log inputs | §3 |
| IF33 | Vocabulary | model output is a **judgment**; "decision" stays the Rules verdict in all YFex docs and APIs | call them decisions (vendor term) | search 05 §4 |
| IF35 | UI suggestions | one YFex.UI suggestion control (confidence chip, ghost default, paste preview) bound to a passive `FieldSuggestion` shape in `YFex.UI.Public` (value text, probability, source label, accept/reject); Forms and Search view models fill it (⚑UI116) | each screen draws its own; UI references Inference | 01 §9 |
| IF36 | Notifications inbound triage | an app inbound handler (inbound router, ⚑N13) asks a choice (queue) and a score (urgency); the Notifications engine is unchanged (⚑N30) | triage inside the Notifications engine; no triage | 01 §7 |
| IF37 | Feedback loop | accept/reject recorded as `JudgmentFeedback` (purpose, model id + version, top option, accepted; no input text) through a sink in `.Public`; per-tenant calibration report; Rules may raise an auto-accept threshold only for purposes declared **low risk**; purposes about people are never auto-accepted | silent self-tuning; no feedback | 01 §9 |
| IF38 | Image input | not in IF-P1; an image case of `InferenceInput` later, remote only by default, refused for health images unless the provider is local | images now; never | 01 §8 |
| IF39 | Domain consumers | Fiscal, Finance and Farma use their own purposes and option tables (NCM, accounts, receivables, SKUs, registry drugs); nothing domain in Inference; their plans record the use when written | built-in domain judges | 01 §2–§6 |
| IF40 | Duplicate records | deterministic blocking (same phone, CPF, CEP) → local yes/no "same record?" → a banner; merging is always the user's command (⚑FM52) | auto-merge; a model over all pairs | 01 §5 |
| IF41 | Privacy rights search | data-subject requests (privacy 07) use the span finder over free-text columns to list candidates a person reviews (⚑P28) | typed columns only (misses free text); automatic erasure from model hits | 01 §8 |
| IF42 | Logging alerts triage | YFex.Logging.Alerts (design) groups exceptions and incidents with a choice over known issues (+ "new"), server side, after ingest (⚑L23) | a model on the ingest path; no grouping | 01 §8 |
| IF43 | Capabilities help and settings | reached through Search signals (⚑SK46 help intent, ⚑SK49 catalog embeddings); settings exposed as capabilities; no Capabilities change | a separate help search | 01 §2 |
| IF34 | Platforms | main packages BCL only; `.Onnx` native libs per RID (win-x64/arm64, linux-x64/arm64, osx-arm64; osx-x64 and musl to verify); Android later; handhelds rules only | ship ONNX in the main package | §6 |

## 6. Phases (proposed)

| Phase | Content | Done when |
|---|---|---|
| IF-P0 Spike | ONNX Runtime + tokenizer in a Native AOT app (Windows, Linux via `linuxKit`); one decision model and one span model (GLiNER-class multi) on a PT-BR set of 300 items; latency and RAM on the reference desktop | numbers recorded; ⚑IF25, ⚑IF29, ⚑IF30 confirmed or changed |
| IF-P1 Contracts + engine | `.Public`, router, abstain, deadlines, cache, probes, rules-only judges, `.Http` (`state + questions`), guard seam | a consumer works with rules only and with a fake remote; zero AOT warnings |
| IF-P2 Local | `.Onnx` (judgments, spans, embeddings), verified model files | a local judgment within the purpose deadline on the reference desktop |
| IF-P3 Bridges | `YFex.Privacy.Inference`, DataMining worm, Automation trigger, `.ChatModels`; Search in SK-P8 | 05 §5.1, §5.2, §5.4 run end to end with and without a model |

## 7. Per-OS differences

- ONNX Runtime ships native libraries per RID; macOS x64 and Alpine (musl) support must be verified in IF-P0. Android needs
  its own ONNX Runtime package (later).
- Model files are large (0.3–2 GB); install location and read-only permissions per OS follow YFex.Installer.
- Main and `.Http` packages have no OS code.

## 8. Recorded in the other plans

Mirror rows added 2026-11-18, all pending, each pointing back here.

| Plan | Rows | Inference rows |
|---|---|---|
| [search-capabilities](../search-capabilities/README.md) | ⚑SK45–⚑SK54 | IF5, IF43 |
| [privacy](../privacy/README.md) | ⚑P26–⚑P29 | IF3, IF4, IF41, IF6 |
| [forms](../forms/README.md) | ⚑FM51, ⚑FM52 | IF8, IF35, IF40 |
| [files](../files/README.md) | ⚑F23 | IF9 |
| [logging](../logging/README.md) | ⚑L22, ⚑L23 | IF19, IF42 |
| [notifications](../notifications/README.md) | ⚑N30 | IF36 |
| [audit](../audit/README.md) | ⚑A32 | IF12 |
| [validation (Rules)](../validation/README.md) | ⚑D39 | IF11 |
| [offline](../offline/README.md), [hosting](../hosting/README.md) | ⚑O33, ⚑H23 | IF18 |
| [installations](../installations/README.md), [installer](../installer/README.md), [cryptography](../cryptography/README.md) | ⚑I45, IN9, ⚑K20 | IF17 |
| [composer](../studio/README.md) | C6 | IF22 |
| YFex.UI [avalonia-components-plan](../../../../YFex.UI/docs/plans/avalonia-components-plan.md) | ⚑UI116 | IF35 |
| YFex.Automation [06-ai-agents](../../../../YFex.Automation/docs/06-ai-agents.md), [07-triggers](../../../../YFex.Automation/docs/07-triggers-hosting-record.md) | pending notes | IF7, IF27 |
| YFex.DataMining [ARCHITECTURE](../../../../YFex.Data/docs/ARCHITECTURE.md) | pending note | IF6 |

Not recorded elsewhere: **YFex.Tenancy** (IF16 only *uses* its built scoped settings; `tenancy/` is the Registry design),
**YFex.Security** (no design document; IF10 waits for one), **Resilience, Persistence, Web, Jobs** (used as built, no change
requested), **YFex.Profiling** (not a fit on purpose, IF31), and Fiscal / Finance / Farma (IF39; no domain plan exists yet).
