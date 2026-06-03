using YFex.Cqrs;

namespace YFex.Messaging.Tests.Fixtures;

// ── Domain model ─────────────────────────────────────────────────────────────

public sealed record TestItem(int Id, string Name, bool IsDeleted = false);

// ── Aggregate with nested CQRS records ──────────────────────────────────────

public partial class TestAggregate
{
    public static partial class Queries
    {
        // ICacheable + ITestItemViews → used in cache and group-expansion tests
        public partial record GetByIdQuery(int Id) : IQuery<TestItem>, ICacheable, ITestItemViews;

        // NOT cacheable, but in the invalidation group
        public partial record GetAllQuery() : IQuery<List<TestItem>>, ITestItemViews;
    }

    public static partial class Commands
    {
        // IQueueable → enqueues when offline
        public partial record CreateCommand(int Id, string Name) : ICommand<TestItem>, IQueueable;

        // Void command, not queueable
        public partial record DeleteCommand(int Id) : ICommand;

        // Result-bearing, not queueable
        public partial record RenameCommand(int Id, string NewName) : ICommand<TestItem>;
    }

    public static partial class Events
    {
        public partial record Created(int Id, string Name) : IEvent;
        public partial record Deleted(int Id) : IEvent;
    }
}

// ── Invalidation group (marker interface) ────────────────────────────────────

/// <summary>Groups all TestItem query types for batch invalidation tests.</summary>
public interface ITestItemViews : IInvalidationGroup { }
