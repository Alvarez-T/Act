; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
YFV0001 | YFex.Validation | Error | Validatable type must be partial
YFV0002 | YFex.Validation | Error | Rule not applicable to member type
YFV0003 | YFex.Validation | Warning | Message with fluent validation is not partial
YFV0004 | YFex.Validation | Info | Fluent rule has non-constant arguments
YFV0005 | YFex.Validation | Error | Unsupported rule type
YFV0006 | YFex.Validation | Error | Invalid range bounds
YFV0007 | YFex.Validation | Error | Invalid regular expression
YFV0008 | YFex.Validation | Error | Unsupported invariant signature
YFV0009 | YFex.Validation | Info | Invariant reads could not be inferred / Reads disagrees with body (Warning)
YFV0010 | YFex.Validation | Warning | DataAnnotations and YFex.Validation rule stacked
YFV0011 | YFex.Validation | Error | Rule declared twice with different arguments
YFV0012 | YFex.Validation | Error | Cycle in nested validatable graph
YFV0013 | YFex.Validation | Warning | Member clashes with a generated nested class
YFV0014 | YFex.Validation | Error | Too many nested collection levels
YFV0015 | YFex.Validation | Error | When member missing or not bool
YFV0020 | YFex.Validation | Info | Contract members not mapped by the view model
YFV0021 | YFex.Validation | Error | [MapsTo] target does not exist on the contract
YFV0022 | YFex.Validation | Error | View-model property type incompatible with the contract member
YFV0023 | YFex.Validation | Warning | View-model rule looser than the contract
YFV0024 | YFex.Validation | Error | Property matched by several contracts
YFV0025 | YFex.Validation | Warning | Validation cannot run when the property changes
YFV0030 | YFex.Validation | Info | Required inferred from nullability
YFV0040 | YFex.Validation | Warning | Rule code has no message
YFV0041 | YFex.Validation | Error | Invalid messages file
