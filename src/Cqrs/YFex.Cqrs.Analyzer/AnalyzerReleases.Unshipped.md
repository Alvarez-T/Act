### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
YFCACHE001 | YFex.Cqrs | Error | LiveCache.ClientPersistent requires ICacheable on the backing query record.
YFCACHE002 | YFex.Cqrs | Error | ICacheable is only valid on IQuery<T> records.
YFQUE001   | YFex.Cqrs | Error | IQueueable is only valid on ICommand or ICommand<T> records.
YFINV001   | YFex.Cqrs | Warning | Both Invalidates and InvalidatedBy declared for the same (command, query) pair.
YFINV002   | YFex.Cqrs | Error | Match predicate not allowed on group/union invalidation targets.
YFINV003   | YFex.Cqrs | Error | Optimistic apply expression references non-existent or incompatible property.
