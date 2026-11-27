# YFex.Expressions — designs and requests

> Index created 2026-11-27 so requests from other designs have one place. Designs: [phase-x-extraction](phase-x-extraction.md) (extraction from Rules.Dynamic, ⚑XD), [filter-dialect](filter-dialect.md) (⚑XS).

## Requests from other designs (all pending)

| Request | From | Breaking | Blocks |
|---|---|---|---|
| Completion API at a caret (what may follow) | YFex.UI ⚑LB18 | no | UI LB-P3b expression editor |
| Scope prefixes `trigger.`, `step.`, `item.` in the readable form and the binder | YFex.UI ⚑LB34 | no | flows with step outputs |
| **`ExprSchema.FromShape(TypeShape)`** and an `IFieldReader` over a Validation `IRecordReader<T>`, so a condition such as `trigger.Customer.Code = "ACME" and trigger.Total > 5000` binds to an event payload and is checked when saved | YFex.Studio ⚑C53 (2026-11-27) | no (additive; Expressions already references Validation) | Studio CR1 step R2 |
