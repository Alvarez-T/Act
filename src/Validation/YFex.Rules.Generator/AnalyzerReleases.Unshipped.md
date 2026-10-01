; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
YFR0001 | YFex.Rules | Error | Situation must be partial
YFR0002 | YFex.Rules | Error | Unsupported situation field type
YFR0003 | YFex.Rules | Error | Invalid [RuleEffectiveDate] field
YFR0004 | YFex.Rules | Error | Only one lines field
YFR0005 | YFex.Rules | Warning | Situation has more than 64 fields
YFR0006 | YFex.Rules | Error | No constructor for deserialization
YFR0010 | YFex.Rules | Error | Rule method missing or mistyped
YFR0011 | YFex.Rules | Error | Unknown rule group
YFR0012 | YFex.Rules | Error | Ambiguous rule group
YFR0013 | YFex.Rules | Error | Rule type not accessible
YFR0014 | YFex.Rules | Error | Duplicate rule code
YFR0015 | YFex.Rules | Error | Rule without group
YFR0016 | YFex.Rules | Info | Applicability not split
YFR0017 | YFex.Rules | Error | Invalid effective date
YFR0020 | YFex.Rules | Error | Rule set must be static partial
YFR0021 | YFex.Rules | Warning | Rule set without groups
YFR0101 | YFex.Rules | Error | Unknown table column field
YFR0102 | YFex.Rules | Error | Table column type mismatch
YFR0103 | YFex.Rules | Error | Decision table must be partial
YFR0104 | YFex.Rules | Error | Decision table without Row
YFR0105 | YFex.Rules | Error | Invalid [Then] column
YFR0106 | YFex.Rules | Info | Row member without [When]/[Then]
YFR0030 | YFex.Rules | Error | [SituationSource] view model must be partial
YFR0031 | YFex.Rules | Error | Situation source member does not fit
YFR0007 | YFex.Rules | Error | Too many rule tiers
YFR0022 | YFex.Rules | Error | Base/LineBase must return an amount
YFR0023 | YFex.Rules | Error | Base and LineBase amounts differ
YFR0107 | YFex.Rules | Error | Unknown rule tier
YFR0040 | YFex.Rules | Error | [RuleFunctions] container shape
YFR0041 | YFex.Rules | Error | Unsupported rule function type
YFR0042 | YFex.Rules | Error | Rule function method shape
YFR0043 | YFex.Rules | Error | Duplicate rule function name
YFR0044 | YFex.Rules | Warning | Rule function without description
