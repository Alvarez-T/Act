### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
YFEX0110 | YFex.Observable | Error | [NotifyOnTaskCompletion] requires Task or Task<T>
YFEX0101 | YFex.Observable | Error | [Observable] requires partial class
YFEX0102 | YFex.Observable | Error | [Observable] outside StateObject
YFEX0210 | YFex.Computed | Error | Computed dependency cycle detected
YFEX0211 | YFex.Computed | Warning | Deep property chain — only root tracked
YFEX0212 | YFex.Computed | Warning | Expression contains a method call
YFEX0301 | YFex.Validation | Error | Validator must implement static abstract interface
YFEX0601 | YFex.Commands | Error | [StateCommand] method must be in a partial class
YFEX0610 | YFex.Commands | Warning | IncludeCancelCommand requires CancellationToken
YFEX0611 | YFex.Commands | Warning | TargetProperty requires Task<T> or ValueTask<T>
