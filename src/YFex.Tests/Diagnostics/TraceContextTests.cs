using YFex.Diagnostics;

namespace YFex.Tests.Diagnostics;

public sealed class TraceContextTests
{
    private const string W3CExample = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public void Parses_the_w3c_example_and_writes_it_back()
    {
        TraceContext.TryParseTraceParent(W3CExample, out TraceContext parsed).Should().BeTrue();
        parsed.TraceId.ToString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        parsed.ParentOperationId.ToString().Should().Be("00f067aa0ba902b7");
        parsed.OperationId.IsEmpty.Should().BeTrue("the receiver opens its own operation under the incoming one");
        parsed.Flags.Should().Be(TraceFlags.Sampled);

        // The sender's view: the incoming span id is its own operation id.
        var sender = new TraceContext(parsed.TraceId, parsed.ParentOperationId, default, parsed.Flags);
        Span<char> buffer = stackalloc char[TraceContext.TraceParentLength];
        sender.WriteTraceParent(buffer).Should().Be(55);
        buffer.ToString().Should().Be(W3CExample);
    }

    [Fact]
    public void Random_contexts_round_trip()
    {
        Span<char> buffer = stackalloc char[64];
        for (int i = 0; i < 1000; i++)
        {
            TraceContext root = TraceContext.NewRoot(i % 2 == 0 ? TraceFlags.Sampled : TraceFlags.None);
            int written = root.WriteTraceParent(buffer);
            written.Should().Be(55);
            TraceContext.TryParseTraceParent(buffer[..written], out TraceContext back).Should().BeTrue();
            back.TraceId.Should().Be(root.TraceId);
            back.ParentOperationId.Should().Be(root.OperationId);
            back.Flags.Should().Be(root.Flags);
        }
    }

    [Fact]
    public void Local_recording_flag_never_reaches_the_wire()
    {
        var context = new TraceContext(TraceId.NewRandom(), OperationId.NewRandom(), default, TraceFlags.Recording);
        Span<char> buffer = stackalloc char[55];
        context.WriteTraceParent(buffer);
        buffer[53..].ToString().Should().Be("00");
    }

    [Theory]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01")]   // upper case
    [InlineData("ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]   // forbidden version
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]   // zero trace id
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]   // zero span id
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-x")] // version 00 must be exact
    [InlineData("00_4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]   // separator
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e473-00f067aa0ba902b7-01")]    // short
    [InlineData("")]
    public void Rejects_invalid_traceparent(string text)
        => TraceContext.TryParseTraceParent(text, out _).Should().BeFalse();

    [Fact]
    public void Accepts_future_versions_with_extra_fields()
    {
        TraceContext.TryParseTraceParent("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-extra", out TraceContext ctx).Should().BeTrue();
        ctx.TraceId.ToString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Fact]
    public void Hex_parse_accepts_both_cases_for_ids_but_not_zero()
    {
        TraceId.TryParseHex("4BF92F3577B34DA6A3CE929D0E0E4736", out TraceId upper).Should().BeTrue();
        TraceId.TryParseHex("4bf92f3577b34da6a3ce929d0e0e4736", out TraceId lower).Should().BeTrue();
        upper.Should().Be(lower);
        TraceId.TryParseHex(new string('0', 32), out _).Should().BeFalse();
        OperationId.TryParseHex("00F067AA0BA902B7", out OperationId op).Should().BeTrue();
        op.ToString().Should().Be("00f067aa0ba902b7");
    }

    [Fact]
    public void Random_ids_are_non_empty_and_distinct()
    {
        var seen = new HashSet<TraceId>();
        for (int i = 0; i < 10_000; i++)
        {
            TraceId id = TraceId.NewRandom();
            id.IsEmpty.Should().BeFalse();
            seen.Add(id).Should().BeTrue();
        }
    }

    [Fact]
    public void Activity_bridge_round_trips()
    {
        TraceId trace = TraceId.NewRandom();
        OperationId operation = OperationId.NewRandom();

        global::System.Diagnostics.ActivityTraceId activityTrace = trace.ToActivity();
        activityTrace.ToHexString().Should().Be(trace.ToString());
        TraceId.From(activityTrace).Should().Be(trace);

        global::System.Diagnostics.ActivitySpanId activitySpan = operation.ToActivity();
        activitySpan.ToHexString().Should().Be(operation.ToString());
        OperationId.From(activitySpan).Should().Be(operation);
    }

    [Fact]
    public void Adopting_an_activity_id_does_not_allocate()
    {
        var activityTrace = global::System.Diagnostics.ActivityTraceId.CreateRandom();
        var activitySpan = global::System.Diagnostics.ActivitySpanId.CreateRandom();
        TraceId.From(activityTrace);
        OperationId.From(activitySpan);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            TraceId.From(activityTrace);
            OperationId.From(activitySpan);
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void Formatting_and_new_ids_do_not_allocate()
    {
        Span<char> buffer = stackalloc char[64];
        TraceContext.NewRoot().WriteTraceParent(buffer);   // warm the thread's generator

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            TraceContext root = TraceContext.NewRoot();
            root.WriteTraceParent(buffer);
            TraceContext.TryParseTraceParent(buffer[..55], out _);
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void Support_code_is_first_eight_hex_digits()
    {
        TraceId.TryParseHex("4bf92f3577b34da6a3ce929d0e0e4736", out TraceId id);
        Span<char> code = stackalloc char[9];
        id.FormatSupportCode(code).Should().Be(9);
        code.ToString().Should().Be("4BF9-2F35");
    }
}
