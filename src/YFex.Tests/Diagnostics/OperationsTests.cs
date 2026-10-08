using YFex.Diagnostics;

namespace YFex.Tests.Diagnostics;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OperationsCollection
{
    public const string Name = "Operations engine (process-global)";
}

public readonly record struct FinalizeCommand(Guid Id, Money Total, int Items);

public static class Outer
{
    public sealed record NestedQuery(int Id);

    public sealed record Generic<T>(T Value);
}

[Collection(OperationsCollection.Name)]
public sealed class OperationsTests : IDisposable
{
    private static readonly OperationDescriptor<FinalizeCommand> s_finalize = OperationRegistry.Register(
        new OperationDescriptor<FinalizeCommand>(
            OperationKind.Command, "Venda.Finalizar", "YFex.Tests",
            flags: OperationDescriptorFlags.Boundary,
            fields: [new("id"), new("total") { TypeName = "Money" }, new("items")],
            capture: static (in FinalizeCommand c, ref LogWriter w) =>
            {
                w.Write(0, c.Id);
                w.Write<Money>(1, c.Total);
                w.Write(2, c.Items);
            }));

    static OperationsTests() => OperationDescriptorFor<FinalizeCommand>.Descriptor = s_finalize;

    private readonly TestEngine _engine = new();

    public void Dispose() => Operations.Uninstall(_engine);

    [Fact]
    public void Without_an_engine_everything_is_inactive()
    {
        Operations.IsInstalled.Should().BeFalse();
        var command = new FinalizeCommand(Guid.NewGuid(), new Money(10m), 2);
        ActiveOperation operation = Operations.Start(in command);
        operation.IsActive.Should().BeFalse();
        operation.Context.IsEmpty.Should().BeTrue();
        operation.Complete();
        operation.Fail(new InvalidOperationException());
        operation.Annotate("rows", 1);
        Operations.Current.IsEmpty.Should().BeTrue();
        Operations.IsEnabled<FinalizeCommand>(LogLevel.Critical).Should().BeFalse();
        OperationLink.FromCurrent(LinkKind.FollowsFrom).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Without_an_engine_the_probes_do_not_allocate()
    {
        var command = new FinalizeCommand(Guid.NewGuid(), new Money(10m), 2);
        Probe(in command);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000_000; i++) Probe(in command);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);

        static void Probe(in FinalizeCommand command)
        {
            ActiveOperation operation = Operations.Start(in command);
            operation.Annotate(0, 1);
            operation.Complete(OperationOutcome.Ok);
            Operations.Start(s_finalize).Fail(null!);
            Operations.Mark(s_finalize, LogLevel.Debug);
            Operations.MarkWith(s_finalize, in command, LogLevel.Debug);
            _ = Operations.IsEnabled(s_finalize, LogLevel.Information);
            _ = Operations.Current;
        }
    }

    [Fact]
    public void Start_routes_the_registered_descriptor_and_captures_fields()
    {
        Operations.Install(_engine, allowReinstall: true);
        var command = new FinalizeCommand(Guid.NewGuid(), new Money(149.90m), 4);

        ActiveOperation operation = Operations.Start(in command);

        operation.IsActive.Should().BeTrue();
        _engine.LastDescriptor.Should().BeSameAs(s_finalize);
        _engine.LastFieldCount.Should().Be(3);
        operation.Context.TraceId.IsEmpty.Should().BeFalse();
        operation.Complete(OperationOutcome.Rejected, "VAL.RANGE");

        _engine.Handle.Ended.Should().Be(1);
        _engine.Handle.Outcome.Should().Be(OperationOutcome.Rejected);
        OutcomeCodes.NameOf(_engine.Handle.Code).Should().Be("VAL.RANGE");
        operation.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Ending_is_idempotent_and_copies_share_the_end()
    {
        Operations.Install(_engine, allowReinstall: true);
        ActiveOperation operation = Operations.Start(s_finalize);
        ActiveOperation copy = operation;

        operation.Complete();
        copy.Complete(OperationOutcome.Faulted);
        operation.Fail(new InvalidOperationException());

        _engine.Handle.Ended.Should().Be(1);
        _engine.Handle.Outcome.Should().Be(OperationOutcome.Ok);
    }

    [Fact]
    public void A_stale_token_cannot_end_the_reused_handle()
    {
        Operations.Install(_engine, allowReinstall: true);
        ActiveOperation first = Operations.Start(s_finalize);
        first.Complete();

        ActiveOperation second = Operations.Start(s_finalize);   // same pooled handle, next generation
        first.Complete(OperationOutcome.Faulted);
        first.Annotate("late", 7);
        _engine.Handle.Ended.Should().Be(1);
        _engine.Handle.Annotations.Should().Be(0);
        second.IsActive.Should().BeTrue();

        second.Annotate("rows", 3);
        second.Complete(OperationOutcome.Ok);
        _engine.Handle.Ended.Should().Be(2);
        _engine.Handle.Annotations.Should().Be(1);
    }

    [Fact]
    public void Concurrent_ends_write_exactly_once()
    {
        Operations.Install(_engine, allowReinstall: true);
        for (int round = 0; round < 200; round++)
        {
            ActiveOperation operation = Operations.Start(s_finalize);
            int before = _engine.Handle.Ended;
            Parallel.For(0, 8, i => operation.Complete(i % 2 == 0 ? OperationOutcome.Ok : OperationOutcome.Faulted));
            _engine.Handle.Ended.Should().Be(before + 1);
        }
    }

    [Fact]
    public void Engine_exceptions_never_reach_the_caller()
    {
        _engine.ThrowOnEnd = true;
        Operations.Install(_engine, allowReinstall: true);
        long errors = Operations.InternalErrorCount;
        ActiveOperation operation = Operations.Start(s_finalize);
        operation.Invoking(o => o.Fail(new InvalidOperationException())).Should().NotThrow();
        Operations.InternalErrorCount.Should().Be(errors + 1);
    }

    [Fact]
    public void Installing_a_second_engine_throws_unless_allowed()
    {
        Operations.Install(_engine, allowReinstall: true);
        Operations.Install(_engine);   // same instance: no-op
        var other = new TestEngine();
        FluentActions.Invoking(() => Operations.Install(other)).Should().Throw<InvalidOperationException>();
        Operations.Uninstall(other).Should().BeFalse();
        Operations.Engine.Should().BeSameAs(_engine);
    }

    [Fact]
    public void Marks_and_links_reach_the_engine()
    {
        Operations.Install(_engine, allowReinstall: true);
        var command = new FinalizeCommand(Guid.NewGuid(), new Money(1m), 1);
        Operations.Mark(s_finalize, LogLevel.Warning, OperationOutcome.Denied, OutcomeCodes.Intern("AUTH.DENIED"));
        Operations.MarkWith(s_finalize, in command, LogLevel.Debug);
        _engine.Marks.Should().Be(2);
        _engine.LastFieldCount.Should().Be(3);

        ActiveOperation root = Operations.Start(s_finalize);
        _engine.CurrentContext = root.Context;
        OperationLink link = OperationLink.FromCurrent(LinkKind.Replay);
        link.TraceId.Should().Be(root.Context.TraceId);
        link.Kind.Should().Be(LinkKind.Replay);
        root.Complete();

        Operations.StartLinked(s_finalize, in link).IsActive.Should().BeTrue();
        _engine.LastLink.Should().Be(link);
    }

    [Fact]
    public void Unregistered_types_get_a_fallback_descriptor_once()
    {
        Operations.Install(_engine, allowReinstall: true);
        var query = new Outer.NestedQuery(1);
        Operations.Start(in query, OperationKind.Query).Complete();
        OperationDescriptor fallback = _engine.LastDescriptor!;
        fallback.Name.Should().Be("Outer.NestedQuery");
        fallback.Kind.Should().Be(OperationKind.Query);
        fallback.Module.Should().Be("YFex.Tests");
        fallback.Index.Should().BeGreaterThanOrEqualTo(0);

        Operations.Start(in query).Complete();
        _engine.LastDescriptor.Should().BeSameAs(fallback);

        var generic = new Outer.Generic<int>(1);
        Operations.Start(in generic).Complete();
        _engine.LastDescriptor!.Name.Should().Be("Outer.Generic");
        _engine.LastDescriptor.Kind.Should().Be(OperationKind.Custom);
    }

    // ── Fake engine ──────────────────────────────────────────────────────────

    private sealed class TestEngine : IOperationEngine
    {
        public readonly TestHandle Handle = new();
        public OperationDescriptor? LastDescriptor;
        public int LastFieldCount;
        public int Marks;
        public OperationLink LastLink;
        public TraceContext CurrentContext;
        public bool ThrowOnEnd
        {
            set => Handle.Throw = value;
        }

        private readonly byte[] _buffer = new byte[512];

        public TraceContext Current => CurrentContext;

        public bool IsEnabled(OperationDescriptor descriptor, LogLevel level) => level >= descriptor.Level;

        public ActiveOperation Start(OperationDescriptor descriptor)
        {
            LastDescriptor = descriptor;
            LastFieldCount = 0;
            return Handle.Begin(TraceContext.NewRoot());
        }

        public ActiveOperation Start<T>(OperationDescriptor<T> descriptor, in T value)
        {
            LastDescriptor = descriptor;
            LastFieldCount = Capture(descriptor, in value);
            return Handle.Begin(TraceContext.NewRoot());
        }

        public ActiveOperation StartLinked(OperationDescriptor descriptor, in OperationLink link)
        {
            LastLink = link;
            return Start(descriptor);
        }

        public void Mark(OperationDescriptor descriptor, LogLevel level, OperationOutcome outcome, int code) => Marks++;

        public void Mark<T>(OperationDescriptor<T> descriptor, in T value, LogLevel level, OperationOutcome outcome, int code)
        {
            Marks++;
            LastFieldCount = Capture(descriptor, in value);
        }

        private int Capture<T>(OperationDescriptor<T> descriptor, in T value)
        {
            if (descriptor.Capture is null) return 0;
            var writer = new LogWriter(_buffer);
            descriptor.Capture(in value, ref writer);
            return writer.FieldCount;
        }
    }

    private sealed class TestHandle : OperationHandle
    {
        private TraceContext _context;
        public int Ended;
        public int Annotations;
        public int Code;
        public bool Throw;
        public OperationOutcome Outcome;

        public override TraceContext Context => _context;

        public ActiveOperation Begin(TraceContext context)
        {
            _context = context;
            return Open();
        }

        protected override void OnEnded(OperationOutcome outcome, int code, OperationFlags flags, Exception? error)
        {
            if (Throw) throw new InvalidOperationException("engine bug");
            Interlocked.Increment(ref Ended);
            Outcome = outcome;
            Code = code;
        }

        protected override void OnAnnotated(ushort field, string? name, long value) => Annotations++;
    }
}
