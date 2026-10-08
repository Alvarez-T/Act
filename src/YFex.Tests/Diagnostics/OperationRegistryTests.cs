using YFex.Diagnostics;

namespace YFex.Tests.Diagnostics;

public sealed class OperationRegistryTests
{
    [Fact]
    public void Well_known_kinds_have_fixed_ids_and_names()
    {
        OperationKind.Command.Id.Should().Be(1);
        OperationKind.Command.Name.Should().Be("cqrs.command");
        OperationKind.Query.Name.Should().Be("cqrs.query");
        OperationKind.Sql.Name.Should().Be("sql");
        OperationKind.Custom.Id.Should().Be(63);
        OperationKind.Custom.Name.Should().Be("custom");
        OperationKind.Command.IsWellKnown.Should().BeTrue();
        OperationKind.TryFind("state.command", out OperationKind state).Should().BeTrue();
        state.Should().Be(OperationKind.StateCommand);
        new OperationKind(50).Name.Should().Be("kind.50");
    }

    [Fact]
    public void Registering_a_kind_is_idempotent()
    {
        OperationKind sefaz = OperationKind.Register("tests.fiscal.sefaz");
        sefaz.Id.Should().BeGreaterThanOrEqualTo(OperationKind.FirstRegisteredId);
        OperationKind.Register("tests.fiscal.sefaz").Should().Be(sefaz);
        OperationKind.NameOf(sefaz).Should().Be("tests.fiscal.sefaz");
        OperationKind.Register("cqrs.command").Should().Be(OperationKind.Command);
        OperationKind.Count.Should().BeGreaterThan(sefaz.Id);
    }

    [Fact]
    public void Concurrent_kind_registration_yields_one_id_per_name()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<ushort>();
        Parallel.For(0, 64, i => ids.Add(OperationKind.Register("tests.concurrent." + (i % 4)).Id));
        ids.Distinct().Should().HaveCount(4);
    }

    [Fact]
    public void Descriptors_get_dense_indexes_and_stable_event_ids()
    {
        var a = OperationRegistry.Register(new OperationDescriptor(OperationKind.Job, "tests.registry.a", "YFex.Tests"));
        var b = OperationRegistry.Register(new OperationDescriptor(OperationKind.Job, "tests.registry.b", "YFex.Tests"));
        b.Index.Should().Be(a.Index + 1);
        OperationRegistry.Get(a.Index).Should().BeSameAs(a);
        OperationRegistry.Register(a).Index.Should().Be(a.Index, "registering twice is a no-op");
        OperationRegistry.TryGet("tests.registry.b", out OperationDescriptor? found).Should().BeTrue();
        found.Should().BeSameAs(b);
        OperationRegistry.TryGetByEventId(a.EventId, out OperationDescriptor? byId).Should().BeTrue();
        byId.Should().BeSameAs(a);

        a.EventId.Should().Be(OperationDescriptor.ComputeEventId("tests.registry.a"));
        a.EventId.Should().Be(OperationDescriptor.ComputeEventId("tests.registry.a"), "stable across processes: StableHash64");
        new OperationDescriptor(OperationKind.Job, "x", "m", eventId: 0x5A1E0013).EventId.Should().Be(0x5A1E0013u);
        OperationRegistry.All.ToArray().Should().Contain(a).And.Contain(b);
    }

    [Fact]
    public void Event_id_collisions_are_reported()
    {
        var first = OperationRegistry.Register(new OperationDescriptor(OperationKind.Job, "tests.collision.first", "m", eventId: 0xC0111DE));
        var second = OperationRegistry.Register(new OperationDescriptor(OperationKind.Job, "tests.collision.second", "m", eventId: 0xC0111DE));
        OperationRegistry.Collisions.Should().Contain(c => c.First == first && c.Second == second);
    }

    [Fact]
    public void Registered_event_fires_for_engines()
    {
        OperationDescriptor? seen = null;
        void Handler(OperationDescriptor d)
        {
            if (d.Name == "tests.registry.event") seen = d;
        }
        OperationRegistry.Registered += Handler;
        try
        {
            var d = OperationRegistry.RegisterMessage(default, "tests.registry.event", "m", "Hello {Name}", LogLevel.Warning, [new("Name")]);
            seen.Should().BeSameAs(d);
            d.Kind.Should().Be(OperationKind.Message);
            d.Template.Should().Be("Hello {Name}");
            d.Has(OperationDescriptorFlags.Leaf).Should().BeTrue();
            d.Fields.Length.Should().Be(1);
        }
        finally
        {
            OperationRegistry.Registered -= Handler;
        }
    }

    [Fact]
    public void Outcome_codes_are_interned()
    {
        int id = OutcomeCodes.Intern("tests.VAL.REQUIRED");
        id.Should().BeGreaterThan(0);
        OutcomeCodes.Intern("tests.VAL.REQUIRED").Should().Be(id);
        OutcomeCodes.NameOf(id).Should().Be("tests.VAL.REQUIRED");
        OutcomeCodes.Intern(null).Should().Be(0);
        OutcomeCodes.NameOf(0).Should().BeNull();
    }
}
