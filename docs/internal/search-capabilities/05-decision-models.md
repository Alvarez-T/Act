# 05 — Decision models: what they are, where they fit Search, and a shared library

> Decisions **⚑SK45–⚑SK54**, all pending. Research done 2026-11-18 with web sources (listed in §9). All code is a
> *proposed* API unless it names a built type. The shared library this file recommends is proposed separately in
> [`../inference/README.md`](../inference/README.md) (prefix **⚑IF**).
>
> Starting point that stays: Search is deterministic (query analysis, format detectors, cost tiers, fuzzy scorer, composite
> ranking = match + intent boost + frecency). The old design's "do not start with ML/NLP"
> ([universal-search-design §9](../universal-search-design.md)) is kept for the keystroke path. Models add **signals**; they
> never decide and never become required (AI is a multiplier, not a dependency).

## 1. What the two names are

Both names were found and verified. They are **not** generative LLMs in the usual sense: they are *decision models*.

**What a "decision model" means here.** The caller sends a *state* (text, JSON or, for some, an image) and one or more
*typed questions*: yes/no (`noul`), choice among named options, or an ordered score (levels). The model returns one
**probability distribution per question** and no text. The answer is the most likely option, and the confidence is that
option's probability. Vendors train the models to be *calibrated*: when a model says 0.8, it should be right about 80% of
the time. Microsoft's catalog calls it a "decision-scoring model" that returns probabilities "instead of generated text". Jev
is described as discriminative: classification or regression, not generation.

| | **Jev** | **Microsoft-Decision-1.1** |
|---|---|---|
| Publisher | TypeSafe AI (San Francisco, founded 2024) | Microsoft (Foundry / Azure AI catalog, "Direct from Azure") |
| Released | limited early access 2026-09-15; stable `jev-1.13.0`, 1.14 adds an image `box` question | v1.0 preview 2026-10-09; **v1.1 GA 2026-11-17** |
| Licence | proprietary | proprietary (the catalog licence tab did not render; no licence text verified) |
| Size / base | not published (transformer; outsiders guess an open LLM or BERT-like) | v1.1 post-trained on Google **Gemma 4 26B-A4B** (MoE); v1.0 was **Qwen3.5-9B**. Some sources still say Qwen for v1.1: they describe v1.0. Microsoft says it will rebase on MAI and OpenAI models |
| Runs locally? | **no**, API only | **no**, API only (Foundry, plus OpenRouter and Vercel gateways); no weights, no ONNX |
| Input | `state` (string, JSON object or array of strings) + questions `noul` / `choice` (≤ 255 options) / `score` (2–10 levels); text only (1.14 adds images) | same shape: `state` + `questions` map with `type` = `noul`/`choice`/`score`, `instructions`, `criteria`; text, JSON, up to 8 images (png/jpg); 32K tokens |
| Output | one distribution per question; `choice`, `score`, `confidence` computed from it by fixed formulas | calibrated probability per option; probability of yes; probability-weighted score; no rationale |
| Endpoint shape | `/v1/systemone` | `<endpoint>/providers/microsoft/v1/systemone` (the same shape as Jev's) |
| Latency | vendor: 70–500 ms end to end | Microsoft: "more than 2×" v1.0; **measured by a third party ~1.0–1.1 s median** from Japan to East US (network included) |
| Price | about $0.042 per 1M input tokens, output free | $0.042 (Global) to $0.05 (EU/APAC Data Zone) per 1M input tokens, output free |
| Portuguese | not stated | not stated (tested in Japanese and English only) |
| Data location | vendor cloud | Global, or Data Zone US / EU / APAC; **no Brazil zone listed** |
| Use limits | — | must not be "the sole automated decision-maker" about people (credit, employment, health…); no surveillance or profiling |

**What could not be verified:** both licences (no text read), Jev's size and architecture, Portuguese quality of either, the
parameter count of Microsoft-Decision-1.1 (only its base model's name, 26B total / ~4B active, is known), the "2×" latency
claim, and every benchmark figure (all vendor-reported or from leaderboards whose versions disagree).

## 2. Closest real candidates that run locally

Neither named model can be YFex's only path: both are proprietary, cloud-only and about one second away. These open models
are the realistic local options. Figures are from model cards and READMEs (self-reported) unless noted.

| Model | Kind | Licence (weights) | Size | Runtime | Portuguese | Notes |
|---|---|---|---|---|---|---|
| **GLiNER2.5-Decide** (Fastino) | decision (classify, yes/no, ordinal) | Apache-2.0 | 340M (DeBERTa-v3-large); 1B variant | Python `gliner2`, CPU or GPU; **no ONNX listed**; p50 ~167 ms on a 48-vCPU Xeon (press) | **no** (English); a 287M **multi** variant exists, language list unverified | output in card examples is the label, not probabilities |
| **GLiNER2-PII** (Fastino) | span extraction, 42 PII types | not stated (paper CC BY 4.0) | ~0.3B | Python `gliner2`; no ONNX | **yes** in training data (synthetic) | precision 0.35–0.37 on SPY: over-predicts |
| **JevK5** (independent) | decision (Jev shape) | Apache-2.0 code + weights | 2B / 4B / 9B (Qwen3.5 + LoRA) | transformers, **llama.cpp GGUF** (4B Q4 = 2.7 GB); CPU ~0.25 s (2B), ~0.6 s (4B) per short decision | English only | distilled partly from GPT-6 Luna outputs "subject to OpenAI's terms": licence review needed |
| **AnyJev / Tacit** (Nokia Applied Research) | decision; also a training-free `Decider` over any open LLM | Apache-2.0 code; Tacit carries base licence | 1.7B–9B; Tacitus 31B | transformers, vLLM; GGUF on roadmap | not stated | "LLMs are already decision models": read option logits, no training |
| **Strands-Decider 2B** (Strands Labs, AWS-linked) | decision | Apache-2.0 | 2B (Qwen3.5-2B LoRA) | GPU, Mac or CPU | not stated | one secondary source only |
| **OpenJev** | decision (Jev-compatible JSON) | **CC BY-NC 4.0** (non-commercial) | 27B | vLLM, MLX, GGUF (≥ 16.5 GB) | not stated (6 languages tagged) | unusable in products without a commercial licence |
| **multilingual-e5-small** (ONNX export) | embeddings | Apache-2.0 | small; QInt8 ONNX file published | ONNX Runtime | multilingual base | for synonym matching of short texts |
| **bge-reranker-v2-m3** (int8 ONNX by ONEBRAIN) | cross-encoder reranker | Apache-2.0 | 569 MB int8 (2.27 GB fp32) | ONNX Runtime, 512 tokens | multilingual base, not tested in pt | "≤ 500 ms p50" is a stated *target*, not a measurement |
| **Colibri embed PT-BR** | embeddings, Brazilian Portuguese | not verified | from embeddinggemma-300m | ONNX: 33 ms vs 76 ms on 4 vCPU (self-reported) | **yes** | single-author, indicative numbers |

Runtime facts for .NET: ONNX Runtime (`Microsoft.ML.OnnxRuntime`) runs these encoder models in process with native libraries
per RID (one source says no osx-x64 build from 1.24 on). `Microsoft.Extensions.AI.Abstractions` defines `IChatClient` and
`IEmbeddingGenerator<TInput, TEmbedding>`, and **no decision-model abstraction**. GGUF models run in llama.cpp, best kept
out of process (a local HTTP server) so YFex code stays AOT-clean. Native AOT with ONNX Runtime and a tokenizer is
**unverified**: a spike (⚑IF25).

**Conclusions from the research**

1. A decision model is the right *shape* for what YFex wants from AI in Search, Privacy and DataMining: closed options in,
   probabilities out, no free text to parse. The shape is shared by Jev, Microsoft, OpenJev and AnyJev, so one contract and
   one HTTP adapter cover all of them.
2. The two named models are cloud-only and about 1 s per call. They can never sit on the keystroke path, and they are
   optional providers for background work (cataloguing, triggers, batch classification).
3. The local candidates are 300M–2B parameters: 100–600 ms on CPU, hundreds of MB to GB of files. They fit desktops and
   servers, not handhelds, and not Tier 0 or Tier 1.
4. Portuguese quality is unknown for every decision model found. Calibration must be measured on our own PT-BR set before
   any model is enabled (⚑IF29).
5. Microsoft's own terms match YFex's stance: model output is a signal, never the sole decision about a person.

## 3. Fit for YFex.Search

### 3.1 The latency budget decides what is possible (⚑SK47)

| Tier (built) | Budget | Model use |
|---|---|---|
| Tier 0 Instant | < 16 ms, every keystroke | **none, ever**: detectors, catalog fold + prefix, frecency |
| Tier 1 Fast | ~100 ms | only an **in-process** model measured at p95 ≤ 20 ms on the reference POS desktop (a short-text embedding may pass; decision models do not) |
| Tier 2 Remote / fallback | ~300 ms, deadline per provider (⚑SK31) | local decision model or reranker on desktop/server, as a late **replacement list** (⚑SK42); dropped when late |
| "Ask" mode (Enter on a question, not per keystroke) | 1–2 s acceptable | remote decision APIs (Jev, Microsoft) or a chat agent, through Privacy (⚑SK51) |

### 3.2 Query intent as a boost, never a gate (⚑SK46)

The built ranking already treats detector output as a boost (`Ranking.IntentBoost`, additive, positive only). A model adds
the same kind of term, scaled by its probability and capped below a certain detector hit:

```csharp
// built: a detector flag lifts hits that satisfy it
double IntentBoost(SearchHit hit) => hit.Satisfies is { } kind ? weightFor(_signature.Formats, kind) : 0;

// proposed: signals from the analyzer (deterministic) and, when present, from a model (probability ∈ [0, 1])
public readonly record struct QueryIntent(string Name);                              // app-declared, like QueryFormat (⚑SK18)
public readonly record struct IntentSignal(QueryIntent Intent, float Probability, SignalSource Source);
// QuerySignature gains: ImmutableArray<IntentSignal> Intents
double ModelBoost(SearchHit hit)
    => hit.Answers is { } intent && _signature.TryGetIntent(intent, out IntentSignal s)
        ? Math.Min(s.Probability * _weights.ModelIntent, _weights.ModelIntentCap)   // cap < IntentCertain
        : 0;
```

No provider is skipped and no hit is removed because of a model. Without a model, `Intents` holds only deterministic
signals and the ranking equals today's.

### 3.3 Verb + subject: the parse is deterministic, a model only adds candidates (⚑SK50)

⚑SK13 already parses "histórico maria" with a verb dictionary from capability keywords. This extends it with app-declared
record-kind words and a small date lexicon. The model never produces a parse alone.

### 3.4 Semantic rerank of the top N (⚑SK48)

Optional Tier 2 stage over the top 20 merged hits: a cross-encoder or a decision model ("which of these answers *notas da
maria de ontem*?"). Desktop and server only, off by default, inside the provider deadline, emitted as a replacement list.
The rerank score enters the composite as one more bounded term. It does not replace the order.

### 3.5 Embeddings: the capability catalog, not record lookups (⚑SK49)

Record lookups stay exact, prefix and trigram in the database (⚑SK12, ⚑SK30). Embeddings help where words differ but
meaning matches ("faturar" ~ "Emitir NF-e", "estornar" ~ "Cancelar venda") in the **capability catalog**: a few thousand
short texts, vectors computed when the registry is built and stored through Persistence, compared in memory (Tier 1 when
the measured query-embedding time allows it, else Tier 2). Vector columns for records are a later YFex.Data request, not now.

### 3.6 Offline and handhelds (⚑SK52)

Handhelds run rules only: no model files on the device. A site broker could later host a model for its terminals (YFex.Offline
Phase 4 + YFex.Hosting, over Messaging RPC). Offline desktops use local models only. Remote providers report "offline" and the
search box shows deterministic results.

### 3.7 AOT, privacy and the deterministic fallback

- **AOT.** Search references only `YFex.Inference.Public` through an optional bridge `YFex.Search.Inference`. Model runtimes
  stay in Inference provider packages (⚑SK45, ⚑IF25).
- **Privacy of the query text (⚑SK51).** A query may hold a CPF or a name. Keystroke text goes only to in-process or on-machine
  providers. "Ask" mode may use a remote provider only through Privacy's `PseudonymizationSession` / `AiDataPolicy` (⚑P19:
  cloud default "nothing above `Internal` in clear"). Results are never cached beyond the process, and probes carry no raw
  text (⚑SK32).
- **Fallback and determinism (⚑SK53).** Every feature works with the rules-only provider. A model answer is cached for the
  session, keyed by model id + version + normalized query, so the same query ranks the same way twice. Ranking tests run with
  no model and with a fake provider that returns fixed probabilities.

## 4. Should model-backed classification get its own library? Yes: `YFex.Inference` (proposal, ⚑SK45 / ⚑IF1)

**Why a library.** At least six consumers need the same thing: closed options in, probabilities out, rules first, a model when
allowed, abstain otherwise. Today three AI seams exist and none of them is typed:

| Where | What it does today | Evidence |
|---|---|---|
| YFex.Automation | `IAiAgent` (chat, cloud/reversed/desktop); `AiDecisionTrigger` asks for "yes or no on the first line" and parses `StartsWith("yes")` | `YFex.Automation/src/YFex.Automation.Abstractions/Ai/IAiAgent.cs:21`, `YFex.Automation/src/YFex.Automation.Hosting/Triggers/AiDecisionTrigger.cs:31-40` |
| YFex.DataMining (in the YFex.Data repo) | `AiSemanticWorm` asks an `IChatClient` to "classify this column's semantic type in one short word", confidence hard-coded 0.6, sample values still TODO | `YFex.Data/src/YFex.DataMining/Analyzers/Ai/AiSemanticWorm.cs:41-68` |
| YFex.Privacy | deterministic only (`TextScrubber`, `ITextDetector`, `NameConventions`); person names and health terms in free text are not found | `YFex.Privacy/src/YFex.Privacy/Scrubbing/` |

Both chat seams turn free text into a decision by parsing. A decision contract removes that parsing and gives real
probabilities.

**Why not an existing owner.**

| Candidate owner | Why not |
|---|---|
| YFex.Search | Privacy, DataMining and Automation must not reference Search to classify text |
| YFex.Privacy | Search intent and trigger decisions are not privacy. Privacy is BCL-only by design (no third-party runtime). Privacy *consumes* it through a bridge |
| YFex.Automation | heavy (desktop, platform packages) and owns generative agents. Its `IAiAgent` becomes one provider (chat fallback, ⚑IF27) |
| YFex.DataMining | a migration tool inside the YFex.Data repo, and itself a consumer |
| YFex core | behaviour does not go to core |
| `Microsoft.Extensions.AI` only | has chat and embeddings, no decision contract. Kept as an adapter (⚑IF20) |

**Naming.** In YFex, a *decision* is a YFex.Rules verdict (allowed, warned, needs approval, denied; Cqrs `IDecisionBound`).
A model's answer is a **judgment**, which is evidence for a decision. The library is named for inference, not decisions (⚑IF1, ⚑IF33).

**Who reuses it** (each one is a ⚑IF row; the deterministic part stays with its owner):

| Consumer | Deterministic part (owner) | Model part (Inference) |
|---|---|---|
| Search | detectors, fold, fuzzy, frecency | intent signals, rerank, catalog embeddings (§3) |
| Privacy | `TextScrubber`, value-object detectors, `NameConventions` | person names, health terms and addresses in free text (span finder), through `YFex.Privacy.Inference` |
| DataMining / cataloguing | naming worm, `IExtractable<T>` hit ratios, value shapes | semantic type of a column as a choice over the project's declared types (§5.1) |
| Files exchange (design) | file format and schema checks | the same cataloguing over an inbound partner CSV before import |
| Forms paste / AI fill (⚑FM23) | `IExtractable<T>` per field, `find_record` (⚑SK29) | names and addresses from pasted text (spans), the user confirms |
| Automation | trigger cadence (Jobs) | `AiDecisionTrigger` as a yes/no judgment with a threshold |
| Security | — | screening of text sent to AI agents (prompt injection), later |
| Rules | verdicts | a judgment probability can be a *fact* a rule reads, never the verdict |
| Logging | redaction stays deterministic on the hot path | a later Jobs pass that samples logs for leaks |
| Audit | records what the executing pipeline did | stores model id, version and probabilities with the action (LGPD art. 20 review) |
| Validation | field checks stay deterministic | not a fit (a plausibility score would be a Rules warning) |

## 5. Before and after examples

### 5.1 Cataloguing a table or CSV: label columns and propose `[Sensitive]` classes

**Before (built skeleton):** one cloud chat call per column, free text back, fixed confidence, nothing local first.

```csharp
var response = await _chat.CompleteAsync($"Column: {col.Name} … Classify this column's semantic type in one short word", ct);
classification = response.Message.Text?.Trim() ?? "Unknown";                  // "CPF"? "Cpf"? "document number"?
yield return Ev(ctx, EvidenceKind.Suggestion, $"{table.Ref}.{col.Name}", $"classified as '{classification}'", 0.6, …);
```

**After (proposed):** a local profile first, then a typed question about the profile. Values never leave the machine.

```csharp
public sealed class ColumnSemanticsWorm(IJudge judge, ColumnProfiler profiler, ILogger<ColumnSemanticsWorm> logger)
    : AnalyzerBase(logger)
{
    public override async IAsyncEnumerable<Evidence> AnalyzeAsync(AnalyzerContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // options = the semantic types the migration project declares (TargetSchemaDefinition.SemanticTypes) + "FreeText", "Other"
        ChoiceQuestion semantic = ChoiceQuestion.FromOptions("semantic", "What does this column hold?", ctx.Project.SemanticOptions());
        YesNoQuestion personal = new("personal-free-text", "Is this free text that may name or describe a person?");

        foreach (TableSchema table in ctx.SourceSchema.Tables)
        foreach (ColumnSchema col in table.Columns)
        {
            // 1. Local and deterministic: 500 sampled values → shapes ("ddd.ddd.ddd-dd ×37"), null ratio, length range,
            //    hit ratio of each IExtractable<T> the app registered (Cpf, Telefone, core Email), scrubber hit kinds.
            ColumnProfile p = await profiler.ProfileAsync(table.Ref, col, sampleSize: 500, ct);
            if (p.BestWholeValueMatch is { HitRatio: >= 0.95 } exact)            // 487/500 values are a valid CPF
            {
                yield return Ev(ctx, EvidenceKind.Fact, Key(table, col), $"semantic {exact.Semantic}", exact.HitRatio, "checksum");
                continue;                                                          // no model needed
            }

            // 2. Model: asked about the profile (name, type, shapes, ratios), never about raw values,
            //    unless the project opts in to samples AND the provider is local (privacy 08 §8.2).
            JudgmentResult r = await judge.JudgeAsync(
                new JudgmentRequest(InferenceInput.Json(p.ToShapeJson()), [semantic, personal], InferencePurpose.DataCatalogue), ct);
            if (r.Outcome is InferenceOutcome.Abstained) continue;                 // no model, deadline, or policy: rules only

            if (r.Get(semantic).Top is { Probability: >= 0.5 } top)
                yield return Ev(ctx, EvidenceKind.Suggestion, Key(table, col), $"semantic {top.Name}", top.Probability, r.Source.ToString());
            if (r.Get(personal).Yes >= 0.7)
                yield return Ev(ctx, EvidenceKind.Suggestion, Key(table, col), "free text with personal data", r.Get(personal).Yes, r.Source.ToString());
        }
    }
}
```

**Result** (legacy ERP `CLIENTES`, local GLiNER-class model, proposal for the user to promote):

| Column | Profile (local) | Deterministic | Model | Proposed classification |
|---|---|---|---|---|
| `CL_NM1` | `aaaa aaaa ×412`, `aaaa aa aaaa ×88` | — | `PersonName` 0.91 | `FullName` value object → `[Sensitive(Personal)]` |
| `CL_DOC` | `ddddddddddd ×460`, `dd.ddd.ddd/dddd-dd ×40` | `Cpf` 0.92 + `Cnpj` 0.08 | skipped | `CpfCnpj` → `PersonalIdentifier` (conservative, privacy 03 §5.3) |
| `CL_MAIL` | `a@a.a ×301`, null ×199 | core `Email` 1.00 | skipped | `Email` → `Personal` / contact |
| `OBS` | free text, 3–400 chars | scrubber: phone 12%, e-mail 3% | personal free text 0.84 | `DataCategory.FreeText`, scrubbed on export and AI |
| `VL_LIM` | `ddd,dd ×500` | — | `Money` 0.88 | not personal |

The same profiler runs on an inbound partner CSV (YFex.Files exchange) before import, and the result feeds Privacy's
`SemanticClassificationMap` (privacy 03 §5.3) and its inventory ("where do we store CPF?").

**Cons.** A 500-value sample can miss rare personal data in `OBS`. Cryptic names (`CL_NM1`) are where the model helps most and
where it is least sure. Calibration in Portuguese is unmeasured. A local model adds 300 MB–2 GB of files and RAM to the
migration tool. Everything a model says stays a `Suggestion` that a person promotes, as DataMining already requires.

### 5.2 Sanitizing free text: deterministic detectors plus a model for what regex cannot see

**Before (built):** the scrubber finds what has a pattern. Names and health terms pass in clear.

```csharp
TextScrubber scrubber = TextScrubber.CreateBuilder().AddBuiltIn(BuiltInDetectors.All).Add<Cpf>().Add<Telefone>().Build();
scrubber.Scrub("Maria Aparecida (CPF 529.982.247-25) ligou, está com depressão; retornar no 11 98765-4321");
// → "Maria Aparecida (CPF <redacted:cpf>) ligou, está com depressão; retornar no <redacted:telefone>"
```

**After (proposed):** the log path is unchanged (synchronous, deterministic). Paths that leave the process (export, AI
prompt, partner file, support ticket) can add an asynchronous model pass. Its spans are merged into the same scrubber.

```csharp
// YFex.Privacy.Inference (bridge): deterministic matches ∪ model spans; deterministic wins on overlap
PrivacyTextScanner scanner = …;   // built from the same TextScrubber + ISpanFinder (local only by policy)
TextScan scan = await scanner.ScanAsync(note,
    new TextScanOptions(Labels: [PrivacyLabels.PersonName, PrivacyLabels.HealthCondition, PrivacyLabels.Address],
                        MinimumProbability: 0.6f, OnAbstain: ScanFallback.FailClosed), ct);
string clean = scan.Scrub(ScrubReplacement.Label);
// → "<redacted:person> (CPF <redacted:cpf>) ligou, está com <redacted:health>; retornar no <redacted:telefone>"
// scan.Found: PersonName 0.93, HealthCondition 0.81 → the record holding this note is flagged for review: health data is
// an art. 11 special category, so the note's record may need [Sensitive(SensitivePersonal, Category = "health")] (⚑P24)
```

Request to Privacy: `TextScrubber` accepts extra spans found elsewhere (additive; ⚑IF3). `FailClosed` replaces the whole text
when the model is absent or late, the same rule the scrubber applies to a regex timeout.

**Cons.** The only PII model found with Portuguese (GLiNER2-PII) reports precision 0.35–0.37: expect over-redaction. Recall is
never 100%, so this is defence in depth, not a guarantee. It costs tens to hundreds of ms per text, so it never runs on the
logging hot path. Health detection raises a classification question that a person must answer.

### 5.3 Search query "notas da maria de ontem" turned into signals

**Before (built):** no detector fires (`Formats = []`), and fuzzy matching on all tokens returns customers named Maria first.

```
1. Maria Silva            (cliente)      fuzzy "maria"
2. Maria Souza            (cliente)      fuzzy "maria"
3. Notas fiscais          (tela)         fuzzy "notas"
```

**After, deterministic (Tier 0, no model):** the analyzer adds signals from app-declared lexicons and `TimeProvider`.

```csharp
// app (domain) declares once
search.Lexicon(l => l
    .RecordKind(FiscalRecords.Invoice, "nota", "notas", "nf", "nfe", "cupom")       // "notas" means invoices in this app
    .StopWords("da", "de", "do", "das", "dos")
    .Dates(DateWords.PortugueseBrazil));                                               // hoje, ontem, semana passada…
// signature for "notas da maria de ontem"
//   RecordKindSignal(fiscal.invoice, 1.0, Lexicon)
//   DateRangeSignal(2026-11-17T00:00 → 2026-11-18T00:00 in America/Sao_Paulo, Lexicon)
//   SubjectSignal("maria") → IRecordLookup<CustomerId>.SearchAsync("maria")          (⚑SK13 verb + subject)
```

```
1. Notas fiscais — cliente Maria Silva, ontem (3)     record action, kind + date + subject
2. Notas fiscais — cliente Maria Souza, ontem (1)
3. Maria Silva            (cliente)
```

**After, with a model (Tier 2, optional desktop):** one choice question over the search box's closed intents adds a bounded
boost. It does not change which providers run.

```csharp
ChoiceQuestion intent = new("intent", "What does the user want?",
    [new("list-records", "see a list of records"), new("open-record", "open one record"),
     new("run-action", "do something"), new("navigate", "go to a screen"), new("help", "ask how to")]);
// → list-records 0.88, open-record 0.07 … → IntentSignal(list-records, 0.88, LocalModel("gliner2.5-multi-decide", "1.0"))
// the "Notas fiscais — Maria …" rows gain ModelBoost = min(0.88 × w, cap); nothing is removed
```

**Cons.** "notas" is ambiguous (invoices or notes), so the lexicon belongs to the app and stays explicit. The date words need a
tenant time zone. A model whose options are closed cannot invent an intent, but its Portuguese calibration is unknown, so the
boost stays capped. All of this adds per-app configuration that a pure fuzzy search did not need.

### 5.4 Automation trigger: parsing "yes" versus a typed judgment

```csharp
// before (built): free text, first word parsed
var decided = response.Text.TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase);

// after (proposed): typed, thresholded, the source and probability kept for the run log
JudgmentResult r = await judge.JudgeAsync(new(InferenceInput.Text(context), [new YesNoQuestion("fire", conditionPrompt)],
                                              InferencePurpose.Automation), ct);
bool decided = r.Outcome is InferenceOutcome.Answered && r.Get("fire").Yes >= threshold;   // abstain → do not fire
```

Con: the trigger loses the free-text rationale, which decision models do not produce. An agent can still be asked
separately when a person needs an explanation.

## 6. Ecosystem fit added by this file

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex.Inference (proposal) | fits; **new library** | judgments, spans, embeddings with rules-only, local and remote providers; Search consumes it through `YFex.Search.Inference` | SK45, IF1 |
| YFex.Privacy | used | `AiDataPolicy` / `PseudonymizationSession` (⚑P19, designed, not built) guard "ask" mode | SK51 |
| YFex.Persistence | used | catalog embedding vectors and the session cache of judgments | SK49, SK53 |
| YFex.Offline / YFex.Hosting (design) | fits later | a model hosted by the site broker for handhelds | SK52 |
| YFex.Data | fits later, **lacks a feature** | vector columns for record search (not requested now) | SK49 |

## 7. Requests added by this file

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex.Inference (new) | `IJudge` / `ISpanFinder` / `IEmbedder` contracts in `.Public`, rules-only provider, abstain outcome, deadlines | no (new) | SK45, IF1 | SK-P8 |
| YFex.Privacy | `PseudonymizationSession` + `AiDataPolicy` (⚑P19) before any remote "ask" mode | no (designed) | SK51 | SK-P8 (ask mode only) |

## 8. Decisions (pending)

Integration and placement first.

| ⚑ | Topic | Proposal | Rejected alternatives | See |
|---|---|---|---|---|
| SK45 | Where model-backed signals live | new library **YFex.Inference** ([proposal](../inference/README.md)); Search references only `YFex.Inference.Public` through an optional bridge `YFex.Search.Inference`; no model runtime in any Search package | models inside YFex.Search; inside YFex.Privacy; inside YFex.Automation; `Microsoft.Extensions.AI` referenced by Search directly | §4 |
| SK46 | Query intent from a model | **boost only**: `IntentSignal(intent, probability, source)` on `QuerySignature`, boost = probability × weight, capped below `IntentCertain`; intents app-declared (`QueryIntent`, like `QueryFormat`) | intent as a gate (route to one provider, hide others); a model replacing format detectors; a closed intent enum in infrastructure | §3.2 |
| SK47 | Latency placement | Tier 0 never; Tier 1 only in-process with measured p95 ≤ 20 ms on reference hardware; else Tier 2 with deadline; remote APIs only in an explicit "ask" mode on Enter | a remote decision API per keystroke; a model before the first paint | §3.1 |
| SK48 | Semantic rerank | optional Tier 2 stage over the top 20, desktop/server only, off by default, inside the deadline, emitted as a replacement list (⚑SK42), one bounded term in the composite | always on; rerank every hit; let the reranker replace the order | §3.4 |
| SK49 | Embeddings | capability catalog only: vectors built with the registry, stored through Persistence, compared in memory; record lookups stay in the database (⚑SK12, ⚑SK30); record vectors later as a YFex.Data request | embeddings for records now; an external vector database; embeddings at query time for the whole catalog | §3.5 |
| SK50 | Verb + subject parse | deterministic: capability verbs (⚑SK13) + app-declared record-kind words, stop words and date words (resolved with `TimeProvider` + tenant zone); a model's parse only adds boosted candidates | a model-only parse; dates parsed by a model | §3.3, §5.3 |
| SK51 | Privacy of query text | keystroke text goes only to in-process or on-machine providers; "ask" mode may use a remote provider only through Privacy ⚑P19 (`AiDataPolicy`, cloud default nothing above `Internal` in clear); no persisted cache; no raw text in probes | send queries to cloud decision APIs; persist judgments keyed by query text | §3.7 |
| SK52 | Offline and handhelds | handhelds rules only; desktops offline use local models; a site-broker-hosted model is a later option with YFex.Offline / YFex.Hosting | ship model files to handhelds; require a connection for search signals | §3.6 |
| SK53 | Determinism and tests | session cache keyed by model id + version + normalized query; tests with no model and with a fixed-probability fake; a model change is a configuration change logged through Diagnostics | non-cached model calls (ranking flickers); tests against a real model | §3.7 |
| SK54 | The two named models | Jev and Microsoft-Decision-1.1 are allowed only as **optional remote providers** behind the one HTTP adapter for the shared `state + questions` shape (⚑IF26), for background work and "ask" mode; never required, never on the keystroke path; enabled per tenant after the PT-BR calibration check (⚑IF29) | adopt one of them as Search's engine; a vendor SDK per model; ignore them | §1, §2 |

## 9. Sources

Named models: [Azure AI catalog: Microsoft-Decision-1.1](https://ai.azure.com/catalog/models/Microsoft-Decision-1.1) ·
[Classmethod: trying Microsoft-Decision-1.1](https://dev.classmethod.jp/en/articles/azure-decision1-1/) ·
[dev.to: Decision 1.1 GA](https://dev.to/alifar/microsoft-decision-11-reaches-general-availability-for-faster-ai-workflow-decisions-5gj7) ·
[The Register: Decision-1 on Qwen](https://www.theregister.com/ai-and-ml/2026/10/10/microsoft-leans-on-open-weight-model-from-chinese-ai-lab-to-challenge-jev/5302473) ·
[Crypto Briefing: Decision Index score](https://cryptobriefing.com/microsoft-decision-1-1-scores-68-index/) ·
[Vercel AI Gateway listing](https://vercel.com/ai-gateway/models/microsoft/decision-1.1) ·
[Wikipedia: Jev (AI model)](https://en.wikipedia.org/wiki/Jev_(AI_model)) ·
[Victor Dibia: how Jev works](https://newsletter.victordibia.com/p/how-jev-works-calibrated-decision) ·
[Artificial Analysis Decision Index](https://artificialanalysis.ai/articles/artificial-analysis-decision-index).

Open alternatives and research: [OpenJev](https://huggingface.co/openjev/openjev) · [JevK5](https://github.com/allebee/jevk5) ·
[AnyJev](https://github.com/nokia-applied-research/AnyJev) · [Releasing the Inner Jev (arXiv 2610.17205)](https://arxiv.org/abs/2610.17205) ·
[LLM-as-Jev (paper page)](https://huggingface.co/papers/2610.02076) ·
[GLiNER2.5-Decide](https://huggingface.co/fastino/gliner2.5-decide) · [GLiNER2.5-Decide-1B](https://huggingface.co/fastino/GLiNER2.5-Decide-1B) ·
[GLiNER2-PII (arXiv 2605.09973)](https://arxiv.org/html/2605.09973) ·
Strands-Decider and Cloudflare Clef: search summaries only, pages not opened ([model list](https://systemonemodels.org/models/), [eesel alternatives](https://www.eesel.ai/blog/microsoft-decision-1-alternatives)) ·
[multilingual-e5-small ONNX](https://huggingface.co/nixiesearch/multilingual-e5-small-onnx) ·
[bge-reranker-v2-m3 int8 ONNX](https://huggingface.co/onebrain-ai/onebrain-rerank-v1) ·
[Colibri embed PT-BR](https://huggingface.co/tardellirs/colibri-embed-ptbr) ·
[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) ·
[ChromaDotNet.Embeddings (ONNX Runtime osx-x64 note)](https://www.nuget.org/packages/ChromaDotNet.Embeddings).
