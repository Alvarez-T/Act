# YFex.Expressions — spreadsheet filter dialect (proposal)

> Status: **proposal** (2026-11-23), no code. Decisions **⚑XS1–XS12**, all pending (prefix checked unused in `docs/`).
> Requested by the YFex.UI data grid plan ([02-data-grid §5.4](../../../../YFex.UI/docs/plans/data-and-logic/02-data-grid.md))
> and the expression editor ([03-logic-builder §5](../../../../YFex.UI/docs/plans/data-and-logic/03-logic-builder.md)).
> The existing rule expression language (`Expr`, `ExprJson`, `ExprText`, Phase X) stays as it is; this adds a **second
> kind** of expression with its own text form.

## 1. Why a second kind

End users who filter tables know spreadsheet formulas (Excel, Google Sheets, LibreOffice). The rule dialect reads like code
(`Order.Total > @CreditLimit`); a filter over a grid should read like a cell formula over **column names**:

```
=AND([Rate] > 20; [Days] < 4)
=OR([Region] = "South"; [Region] = "North")
=CONTAINS([Customer]; "silva")          =STARTSWITH([Code]; "NF")       =ISBLANK([Note])
=[Weight] >= 5                          =BETWEEN([Date]; TODAY()-30; TODAY())
=[Status] = "Ativo*"                    (wildcards * and ? in text equality, like spreadsheet criteria)
```

## 2. Shape

| Piece | Proposal |
|---|---|
| Kind | `ExprDialect.Rule` (existing) and `ExprDialect.Filter` (new) (⚑XS1) |
| Surface syntax | leading `=` optional; column refs in `[...]` by **display header** (resolved to field paths through a column map the grid passes); functions in upper case; argument separator follows the culture (`;` in pt-BR, `,` in en) like spreadsheets (⚑XS3); string literals in double quotes; numbers and dates in the user's culture |
| Lowering | parsed into the **existing `Expr` AST** and bound against an `ExprSchema` built from the visible columns, so typing, `FunctionCatalog`, compiler and interpreter are reused (⚑XS2) |
| Functions | spreadsheet names mapped to catalog functions: `AND`, `OR`, `NOT`, `IF`, `ISBLANK`, `CONTAINS`, `STARTSWITH`, `ENDSWITH`, `LEN`, `UPPER`, `LOWER`, `TODAY`, `NOW`, `YEAR`, `MONTH`, `DAY`, `BETWEEN`, `IN`, `ROUND`, `ABS`, `MIN`, `MAX`; localized aliases (`E`, `OU`, `SE`, `HOJE`) when the culture asks (⚑XS4) |
| Output | a predicate `Func<TRow, bool>` (client grids, compiled once, AOT-safe through the existing emitter/interpreter) and the **bound tree** for server translation (⚑XS5) |
| Server | the tree travels in core `FilterSpec` (YFex.UI ⚑DG20); YFex.Data translates a safe subset to SQL parameters, never string concatenation (⚑XS6) |
| Printing | `FilterText.Write(expr, culture)` round-trips for chips and editing |
| Completion | the completion API at a caret (YFex.UI ⚑LB18) serves both dialects |
| Diagnostics | `ExprDiagnosticCodes` with spreadsheet-friendly messages ("Unknown column [Rat]. Did you mean [Rate]?") |
| Limits | max length, max depth, no side effects, no row cross-references except aggregates in a later phase (`[Rate] > AVERAGE([Rate])`, ⚑XS8) |

## 3. Phases

| Phase | Builds |
|---|---|
| XS-P1 | tokenizer + parser (culture separators) → `Expr`; column map binding; `ExprDialect`; printer |
| XS-P2 | function aliases, wildcards in equality, completion API |
| XS-P3 | SQL-safe subset translation contract (consumed by YFex.Data) |
| XS-P4 | aggregates over the filtered set (optional) |

## 4. Decisions

| ⚑ | Topic | Proposal | Rejected |
|---|---|---|---|
| XS1 | Kind | new `ExprDialect.Filter` beside the rule dialect | replace the rule text form |
| XS2 | Engine | lower to existing `Expr` AST + catalog | a separate evaluator |
| XS3 | Separators | culture-driven `;`/`,` | always `,` |
| XS4 | Localized function names | aliases per culture, canonical names always accepted | English only |
| XS5 | Outputs | predicate + bound tree | predicate only |
| XS6 | Server translation | safe subset to parameterized SQL in YFex.Data | client-only filtering |
| XS7 | Column refs | display header in `[...]`, resolved by a column map; field path also accepted (`[Customer.Name]`) | field paths only |
| XS8 | Aggregates | later phase | now |
| XS9 | Wildcards | `*`/`?` in `=` text comparisons, `~` escapes | regex |
| XS10 | Dates | culture parsing + `DATE(y;m;d)` | ISO only |
| XS11 | Package | inside YFex.Expressions (`Dialects/Filter`) | a new package |
| XS12 | Rule dialect reuse | the rule editor may accept filter syntax for conditions later | never |
