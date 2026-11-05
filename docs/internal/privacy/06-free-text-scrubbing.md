# 06 — Free-text scrubbing

Typed values are handled by their classification. **Free text** is where accidents happen: exception messages (`Key (cpf)=(12345678909)
already exists`), validation messages that echo input, HTTP errors with URLs and tokens, `Observacao` fields, search queries, AI prompts,
`RawPayload` JSON in YFex.Brain, and telemetry property bags. The scrubber is **defence in depth**. The main defence is that the
ecosystem captures typed, classified values instead of strings.

## 1. Where it runs

| Caller | Text | Destination |
|---|---|---|
| Logging consumer thread | captured strings, template holes, exception messages, URLs | `DiagnosticLog` / `AuditLog` |
| Profiling `TelemetryBatch` | property bag values (after key conventions) | `Telemetry` |
| AI gateway (08 §8) | prompts, tool results' free-text members | `CloudLanguageModel` / `LocalLanguageModel` |
| Rights export (07) | nothing (exports are the subject's own data, unredacted) | — |
| Error reporting to users (support code screens) | exception messages | `Display` |
| DataMining `AiSemanticWorm` | sample values → replaced by **shapes** (08 §8.2), scrubber as backstop | `CloudLanguageModel` |

## 2. API

```csharp
namespace YFex.Privacy;

/// Immutable, thread-safe; build once per policy and cache.
public sealed class TextScrubber
{
    public static TextScrubber Default { get; }                              // built-in generic detectors only (§3)
    public static TextScrubberBuilder CreateBuilder();

    /// Fast path: returns false and writes NOTHING when no detector matched — the caller keeps the original string.
    public bool TryScrub(ReadOnlySpan<char> text, IBufferWriter<char> output, in ScrubContext context, out ScrubSummary summary);

    public bool ContainsPersonalData(ReadOnlySpan<char> text);               // detection only (tests, analyzers of payloads)
    public int MaximumInputLength { get; }                                   // default 64 KiB; the tail beyond is replaced by "<truncated:n>"
}

public sealed class TextScrubberBuilder
{
    public TextScrubberBuilder AddBuiltIn(BuiltInDetectors detectors);       // flags, default All
    public TextScrubberBuilder Add<T>() where T : IClassifiedData<T>;        // value-object detector via T.TryFind (§4)
    public TextScrubberBuilder Add(ITextDetector detector);                  // custom
    public TextScrubberBuilder WithMaximumInputLength(int characters);
    public TextScrubber Build();
}

/// How to write a replacement: decided by destination policy (05 §1) and the optional AI session (08 §8).
public readonly struct ScrubContext(DataDestination destination, Redactor redactor, Pseudonymizer? pseudonymizer,
                                    PseudonymizationSession? session = null);

public readonly struct ScrubSummary
{
    public int MatchCount { get; }
    public DetectedKinds Kinds { get; }      // flags: Email, Card, Jwt, Bearer, ApiKey, SecretAssignment, UrlQuery, IpAddress, ValueObject, Custom
    public bool Truncated { get; }
    public bool TimedOut { get; }            // a regex hit its time limit → the whole text was replaced (fail closed)
}

[Flags] public enum BuiltInDetectors : ushort
{
    None = 0, Email = 1, CardNumber = 2, Jwt = 4, BearerToken = 8, KnownApiKeys = 16, SecretAssignment = 32,
    UrlQuery = 64, IpAddress = 128, All = 0xFF,
}

public interface ITextDetector
{
    DetectorPrefilter Prefilter { get; }                                     // §5.1
    DataClassification Classification { get; }
    string Label { get; }                                                    // "cpf", "email", "card": used in replacements
    bool TryFind(ReadOnlySpan<char> text, out int start, out int length);    // first match at or after index 0 of the slice
}
```

## 3. Built-in detectors (generic, not country-specific)

| Detector | Pattern (bounded quantifiers, no nested repetition) | Validation | Classification → default log replacement |
|---|---|---|---|
| E-mail | `[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,253}\.[A-Za-z]{2,24}` | domain has a dot, no `..` | `Personal` → `<email #h:…>` |
| Card number | `(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)` | **Luhn** + length 13–19 + IIN prefix sanity | `Financial` → `<redacted:card>` |
| JWT | `eyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{10,}` | — | `Secret` → `<redacted:jwt>` |
| Bearer token | `(?i)\bbearer\s+[A-Za-z0-9._~+/-]{16,}=*` | — | `Secret` → `Bearer <redacted>` |
| Known API keys | `sk-[A-Za-z0-9_-]{20,}`, `AKIA[0-9A-Z]{16}`, `ghp_[A-Za-z0-9]{36}`, `xox[abprs]-[A-Za-z0-9-]{10,}`, `AIza[0-9A-Za-z_-]{35}` | — | `Secret` → `<redacted:api-key>` |
| Secret assignment | `(?i)\b(password|senha|pwd|secret|token|api[_-]?key|client[_-]?secret)\s*[=:]\s*[^\s;&,]{1,256}` (connection strings, query strings, config dumps) | — | `Secret` → keeps the key name, erases the value: `Password=<redacted>` |
| URL query | `https?://[^\s?#]{1,2048}\?[^\s#]{1,4096}` | — | keeps scheme+host+path, query → `?<redacted>` |
| IP address | IPv4 `(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])`; IPv6 (compressed forms) | each octet ≤ 255; excludes version strings by context (`v1.2.3.4`) | `Personal`/location → `{ip}` mask `192.168.10.*` |

All are `[GeneratedRegex(..., RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 25)]` and are run
with `Regex.EnumerateMatches(ReadOnlySpan<char>)` (a `ValueMatchEnumerator`, no `Match` objects, no allocation).

## 4. Country-specific documents come from the value objects (⚑P18)

> **Decided 2026-11-05 (supersedes `TryFind` on `IClassifiedData<T>` and `TryFindIn` below):** finding a value in text is not a
> privacy concept, so it gets its own contract in YFex core (types only), used by Privacy, Profiling, DataMining (scraping) and
> Search alike:
>
> ```csharp
> public interface IExtractable<TSelf> where TSelf : IExtractable<TSelf>
> {
>     /// The first valid occurrence in text: the parsed value and where it is.
>     static abstract bool TryExtract(ReadOnlySpan<char> text, out TSelf value, out int start, out int length);
> }
> ```
>
> `Cpf`, `Telefone` (and later `Rg`, `Cnpj`, EAN, NCM, NF-e key) implement it in YFex.Domain. Privacy adds
> `TextScrubber.Add<T>() where T : IExtractable<T>, IClassifiedData<T>`; non-personal types (EAN, NCM, CNPJ) implement only
> `IExtractable<T>`. Returning the parsed value lets scraping and Search use it directly and lets the scrubber mask (later
> pseudonymize) with the type's own rules. The sketches below keep the old names for history.

The library is domain-agnostic, so it does not know CPF. `Cpf`, `Telefone`, `Rg` and `Cnpj` in YFex.Domain **already know how to
parse and validate themselves**. They expose detection through the contract (02 §6). The scrubber calls it through a generic detector:

```csharp
// YFex.Privacy (📦 Framework): the bridge from a value object to a detector — generic, no reflection
internal sealed class ValueObjectDetector<T> : ITextDetector where T : IClassifiedData<T>
{
    public bool TryFind(ReadOnlySpan<char> text, out int start, out int length) => T.TryFind(text, out start, out length);
    public DataClassification Classification => T.Classification;
    // replacement: T.TryParse(match) → Pseudonymizer.Pseudonymize(in value, …) so the pseudonym equals the typed one
}
```

**✍ Dev writes** (YFex.Domain, once, hand-written next to the existing parser)

```csharp
public readonly partial struct Cpf
{
    /// Candidates: 11 digits, optionally formatted ddd.ddd.ddd-dd, not adjacent to other digits/letters.
    /// Validated by the two check digits, which drops ~99% of random 11-digit runs (barcodes, keys, phone numbers).
    public static bool TryFindIn(ReadOnlySpan<char> text, out int start, out int length) { /* span scan, no regex needed */ }
}
```

The generator sees `TryFindIn` and wires `IClassifiedData<Cpf>.TryFind` to it (03 §2.1). The app registers what it wants:

```csharp
services.AddYFexPrivacy(p => p
    .Scrubber(s => s.AddBuiltIn(BuiltInDetectors.All).Add<Cpf>().Add<Telefone>().Add<Rg>()));
```

**Consequence worth the design:** a CPF found inside an exception message gets **the same pseudonym** as a typed `Cpf` captured
by the logging generator (`#h:9f2c41d07a3be1c2`), because both go through `Cpf.TryWriteCanonical` + `Pseudonymizer`. Searching logs
for one customer finds both.

`Cnpj` detection is available but **not registered by default**: CNPJs are `Public`. YFex.Search's `IFormatDetector`
implementations (01 §2) can call the same `TryFindIn` methods, so the parsing logic exists once.

## 5. Algorithm (zero allocation when nothing matches)

### 5.1 Prefilter

One vectorized pass computes cheap features with `SearchValues<char>` and `IndexOfAnyInRange`:

| Feature | Enables |
|---|---|
| longest run of digits (separators `.`, `-`, `/`, ` ` allowed inside) ≥ 8 | card, CPF, phone, RG, IP |
| contains `@` | e-mail |
| contains `eyJ` | JWT |
| contains `://` | URL query |
| contains `=` or `:` next to a secret word (case-insensitive `SearchValues<string>`) | secret assignment, bearer |
| starts-with tokens `sk-`, `AKIA`, `ghp_`, `xox`, `AIza` | known API keys |

Each detector declares the features it needs (`DetectorPrefilter` flags). Typical log text (`"Venda finalizada em 48 ms"`) enables
**no** detector and returns in ~100 ns per KiB.

### 5.2 Matching and replacement

1. Run the enabled detectors over the text and collect matches into a `stackalloc` array of 32 `(start, length, detector)` entries
   (rented from `ArrayPool` beyond that).
2. Sort by start and resolve overlaps: the longer match wins, and on equal length the higher sensitivity wins.
3. If no match remains, return `false` (the caller keeps its original string, no copy).
4. Otherwise write the output once: copy each gap, write each replacement through the context: a pseudonym (logs), a placeholder (AI
   session), a mask (display), or a label (telemetry).
5. A regex timeout makes the scrubber **fail closed**: the whole input becomes `<scrub-timeout:{length}>`, `TimedOut` is set and the
   `privacy.scrubber.timeouts` metric increments.

### 5.3 Replacement formats

| Destination | Personal / PersonalIdentifier | SensitivePersonal | Financial / Secret |
|---|---|---|---|
| `DiagnosticLog` | `<cpf #h:9f2c41d07a3be1c2>` | `<redacted:health>` | `<redacted:card>` |
| `Telemetry` | `<cpf>` | `<redacted>` | `<redacted>` |
| `CloudLanguageModel` | `[[CPF_1]]` (session placeholder) | `[[REDACTED]]` | `[[REDACTED]]` |
| `Display` | mask (`***.456.789-**`) | `******` | `****1234` / `******` |

## 6. Exceptions and property bags

```csharp
namespace YFex.Privacy;

public static class ExceptionScrubbing
{
    /// Scrubs Message, Data values and inner exception messages; stack frames are kept (method names carry no personal data).
    /// Writes a structured, scrubbed view; never mutates the exception.
    public static void WriteScrubbed(Exception exception, TextScrubber scrubber, in ScrubContext context, IBufferWriter<char> output);
}

/// Replacement for PrivacyScrubber.Scrub: keys classified by token conventions (02 §5.2), values scrubbed.
public sealed class PropertyRedactor
{
    public void Redact(IDictionary<string, object?> properties, in ScrubContext context);   // in place; no new dictionary
}
```

**✍ Before** (Profiling `TelemetryBatch`)

```csharp
var scrubbed = batch.Select(e => PrivacyScrubber.Scrub(e, _options.TransportSaltKey)).ToList();   // LINQ + new dictionary per event
```

**After**

```csharp
foreach (var telemetryEvent in batch)                    // List<T>; no LINQ
    _propertyRedactor.Redact(telemetryEvent.Properties, in _telemetryContext);   // keys: token conventions; values: TextScrubber
```

**👁 Result**

| Key | Value before | Today | After |
|---|---|---|---|
| `url` | `https://loja.com/pedido?cpf=12345678909` | host hash (salted SHA-256) | `#h:…` of the host (domain `telemetry`); query never shipped |
| `multipleMonitors` | `"true"` | `"masked"` (contains `ip`) | `"true"` (token match: no `ip` token) |
| `machineName` | `CAIXA-03` | hashed (contains `mac`) | `Personal` by convention (`machine`+`name` listed in the telemetry key table) → pseudonym |
| `userName` | `joao.silva` | **clear** | `Personal` by convention (`user`+`name`) → pseudonym |
| `windowTitle` | `Cliente: João Silva - CPF 123.456.789-09` | hashed | `Personal` by convention (`title`) → pseudonym; with `telemetry.active-window-title` withdrawn → not collected at all |
| `error` | `timeout calling https://api/x?token=abc…` | clear | `timeout calling https://api/x?<redacted>` |

## 7. Tests that keep it honest

`YFex.Privacy.Tests/Scrubbing/Corpus/*.txt`, a corpus of real-world strings with expected outputs:

- **Must not match:** EAN-13 barcodes, the NF-e 44-digit access key (11-digit windows inside are blocked by the "not adjacent to
  digits" boundary), GUIDs, ISO dates, amounts `1.234.567,89`, version strings `1.2.3.4`, CFOP/NCM codes, Unix timestamps.
- **Must match:** Npgsql/SQLite unique-violation messages with CPF and e-mail, ASP.NET Core URL logs with `?token=`, connection
  strings, `Authorization: Bearer …` dumps, JWTs, card numbers with spaces, phone numbers in `(11) 98765-4321` and `+55 11 987654321`.
- Property: for any input, output never contains a substring that a detector would match (scrub is idempotent).
- Allocation test: 10 000 no-match calls allocate 0 bytes.

## 8. Migration of `PrivacyScrubber`

| Member | Becomes |
|---|---|
| `PrivacyScrubber.Scrub(TelemetryEvent, salt)` | `PropertyRedactor.Redact` (shim kept one release, `[Obsolete]`) |
| `PrivacyScrubber.HashUrl(url, salt)` | `UrlRedaction.PseudonymizeHost(url, pseudonymizer, PseudonymDomain.Telemetry)` |
| `PrivacyScrubber.HashString(value, salt)` | `Pseudonymizer.Pseudonymize(value, PseudonymDomain.Telemetry)` |
| `PrivacyScrubber.MaskIpAddress(ip)` | `Masks.TryWriteIpAddress` |
| `TelemetryOptions.TransportSaltKey` | removed after the shim release; the telemetry pseudonym key comes from the vault (app-level `KeyScope` for single-tenant desktop apps) |
