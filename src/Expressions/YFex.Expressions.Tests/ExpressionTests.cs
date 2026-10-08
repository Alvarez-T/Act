using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using YFex.Validation;
using static YFex.Expressions.Tests.Order;

namespace YFex.Expressions.Tests;

/// <summary>Phase X.2 — the expression engine over a runtime schema (no situations): semantics, types, errors, JSON, allocations.</summary>
public class ExpressionTests
{
    private const string PixAndBig = """{ "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { ">=": [ { "var": "Total" }, 500 ] } ] }""";

    [Fact]
    public void Enum_literals_and_amount_comparisons_evaluate_over_a_runtime_schema()
    {
        var p = Compile(PixAndBig);
        p.ResultType.IsBool.Should().BeTrue();
        p.ItemCollection.Should().Be(-1);

        var pix = Sale(PayMethod.Pix);
        var cash = Sale(PayMethod.Cash);
        var env = default(ExprEnv);
        p.RunBool(ref pix, in env).Should().BeTrue();
        p.RunBool(ref cash, in env).Should().BeFalse();
    }

    [Fact]
    public void Amount_arithmetic_keeps_the_amount_type()
    {
        Run(Compile("""{ "*": [ { "var": "Total" }, 2 ] }"""), Sale()).As<Money>().Should().Be(new Money(1920));
        Run(Compile("""{ "percentOf": [ { "var": "Total" }, 5 ] }"""), Sale()).As<Money>().Should().Be(new Money(48));
        Run(Compile("""{ "/": [ { "var": "Total" }, { "var": "Total" } ] }"""), Sale()).TryGet(out decimal ratio).Should().BeTrue();
        Run(Compile("""{ "-": [ { "var": "Total" }, 60 ] }"""), Sale()).As<Money>().Should().Be(new Money(900));
    }

    [Fact]
    public void Aggregates_run_over_the_default_collection()
    {
        Run(Compile("""{ "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }"""), Sale()).As<Money>().Should().Be(new Money(960));
        Run(Compile("""{ "count": { ">": [ { "line": "Quantity" }, 1 ] } }"""), Sale()).TryGet(out long n).Should().BeTrue();
        n.Should().Be(1);
        Run(Compile("""{ "any": { "==": [ { "line": "Category" }, "drinks" ] } }"""), Sale()).TryGet(out bool any).Should().BeTrue();
        any.Should().BeTrue();
        Run(Compile("""{ "all": { ">": [ { "line": "Quantity" }, 1 ] } }"""), Sale()).TryGet(out bool all).Should().BeTrue();
        all.Should().BeFalse();
        Run(Compile("""{ "maxOf": { "line": "UnitPrice" } }"""), Sale()).As<Money>().Should().Be(new Money(900));
        Run(Compile("""{ "minOf": { "line": "UnitPrice" } }"""), Sale()).As<Money>().Should().Be(new Money(10));
    }

    [Fact]
    public void A_line_read_outside_aggregates_makes_a_per_item_program()
    {
        var p = Compile("""{ "==": [ { "line": "Category" }, "drinks" ] }""");
        p.ItemCollection.Should().Be(LinesId);
        var r = Sale();
        var env = default(ExprEnv);
        p.RunBool(ref r, in env, 0).Should().BeTrue();
        p.RunBool(ref r, in env, 1).Should().BeFalse();
        p.RunBool(ref r, in env).Should().BeFalse();                        // no item → null → false
    }

    [Fact]
    public void Evaluation_is_total_and_null_propagating()
    {
        Run(Compile("""{ "/": [ { "var": "Total" }, 0 ] }"""), Sale()).IsNone.Should().BeTrue();
        Run(Compile("""{ "upper": [ { "var": "Note" } ] }"""), Sale(note: null)).IsNone.Should().BeTrue();
        Run(Compile("""{ "coalesce": [ { "var": "Note" }, "none" ] }"""), Sale(note: null)).TryGet(out string? s).Should().BeTrue();
        s.Should().Be("none");
        Run(Compile("""{ "if": [ { "var": "Member" }, 1, 2 ] }"""), Sale(member: true)).TryGet(out long one).Should().BeTrue();
        one.Should().Be(1);
        Run(Compile("""{ "between": [ { "var": "Installments" }, 1, 3 ] }"""), Sale()).TryGet(out bool inside).Should().BeTrue();
        inside.Should().BeTrue();
        Run(Compile("""{ "in": [ { "var": "Payment" }, ["Pix", "Card"] ] }"""), Sale()).TryGet(out bool found).Should().BeTrue();
        found.Should().BeTrue();
    }

    [Fact]
    public void Problems_are_violations_with_json_paths_and_neutral_codes()
    {
        Errors("""{ "==": [ { "var": "Nope" }, 1 ] }""").Should().Contain("e.==[0]:expr.unknown-field");
        Errors("""{ "==": [ { "var": "Payment" }, "Boleto" ] }""").Should().Contain("e.==[1]:expr.literal");
        Errors("""{ "+": [ { "var": "Total" }, { "var": "Installments" } ] }""").Should().Contain(c => c.EndsWith("expr.type"));
        Errors("""{ "gold": [ 1 ] }""").Should().Contain("e.gold:expr.unknown-function");
        Errors("""{ "abs": [ "text" ] }""").Should().Contain(c => c.Contains("abs") && c.EndsWith("expr.type"));
        Errors("""{ "sum": { "sum": { "line": "Quantity" } } }""").Should().Contain("e.sum.sum:expr.nested-aggregate");
        Errors("""{ "var": "$nope" }""").Should().Contain("e:expr.unknown-special");
    }

    [Fact]
    public void Hosts_map_codes_to_their_own_namespace()
    {
        var options = Options() with { Codes = ExprDiagnosticCodes.Default with { UnknownField = "rules.dynamic.unknown-field" } };
        Errors("""{ "var": "Nope" }""", options).Should().Equal("e:rules.dynamic.unknown-field");
    }

    [Fact]
    public void Specials_read_the_environment_by_slot()
    {
        var specials = ExprSpecials.None
            .With(new ExprSpecial("$base", new ExprType(Money.RawType), 0, "running amount"))
            .With(new ExprSpecial("$today", ExprType.Date, 1, "the business date"))
            .WithItemIndex("$lineIndex")
            .WithUnavailable("$user", "screen.no-user", "no user in this context");
        var options = Options() with { Specials = specials };

        var p = Compile("""{ "*": [ { "var": "$base" }, 2 ] }""", options);
        var r = Sale();
        var env = ExprEnv.Of(RawValueConvert.From(new Money(100)), RawValue.From(new DateOnly(2026, 10, 1)));
        p.Run(ref r, in env).As<Money>().Should().Be(new Money(200));

        var index = Compile("""{ "var": "$lineIndex" }""", options);
        index.ItemCollection.Should().Be(LinesId);
        index.Run(ref r, in env, 1).TryGet(out long i).Should().BeTrue();
        i.Should().Be(1);

        Errors("""{ "var": "$user" }""", options).Should().Equal("e:screen.no-user");
    }

    [Fact]
    public void Non_deterministic_functions_are_rejected_only_when_the_host_requires_determinism()
    {
        var today = ExprFunction.Create("today", ExprType.Date, [], static _ => RawValue.From(new DateOnly(2026, 10, 1)),
            "The current date.", deterministic: false);
        var catalog = FunctionCatalog.BuiltIn.With(today);

        Compile("""{ "today": [] }""", Options(catalog)).ResultType.IsDate.Should().BeTrue();
        Errors("""{ "today": [] }""", Options(catalog) with { RequireDeterministic = true }).Should().Equal("e.today:expr.non-deterministic");
        catalog.Version.Should().NotBe(FunctionCatalog.BuiltIn.With(today.WithImplementation(static _ => RawValue.None)).Version - 1);
    }

    [Fact]
    public void Contracts_bind_only_once_implemented()
    {
        var contract = ExprFunction.Contract("isHoliday", ExprType.Bool, [new("date", ExprType.Date)], "Public holiday?");
        var declared = FunctionCatalog.BuiltIn.With(contract);
        Errors("""{ "isHoliday": [ { "var": "Date" } ] }""", Options(declared)).Should().Equal("e.isHoliday:expr.unimplemented");

        var implemented = declared.Implement("isHoliday", static a => RawValue.From(a[0].TryGet(out DateOnly d) && d.Month == 12 && d.Day == 25));
        Run(Compile("""{ "isHoliday": [ { "var": "Date" } ] }""", Options(implemented)), Sale()).TryGet(out bool h).Should().BeTrue();
        h.Should().BeFalse();
        declared.Unimplemented.Should().ContainSingle();
    }

    [Fact]
    public void Json_is_canonical_and_text_is_readable()
    {
        var e = Parse(PixAndBig);
        string once = Json(e);
        Json(ExprJson.Read(Encoding.UTF8.GetBytes(once))).Should().Be(once);           // write → read → write is stable
        // Default encoder escapes '>' — exactly how Rules.Dynamic has stored canonical specs; must not change (stored bytes).
        once.Replace((char)0x5C + "u003E", ">").Should().Be("""{"and":[{"==":[{"var":"Payment"},"Pix"]},{">=":[{"var":"Total"},500]}]}""");
        once.Should().Contain((char)0x5C + "u003E=");                                    // escaped exactly as today
        ExprText.Render(e).Should().Be("Payment = \"Pix\" and Total ≥ 500");
        ExprText.Render(Parse("""{ "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }""")).Should().Be("sum(line.UnitPrice × line.Quantity)");
    }

    private static string Json(Expr e)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) ExprJson.Write(w, e);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void Number_literals_scale_amounts_without_becoming_amounts()
    {
        Run(Compile("""{ "/": [ { "var": "Total" }, 4 ] }"""), Sale()).As<Money>().Should().Be(new Money(240));
        Run(Compile("""{ "*": [ 0.5, { "var": "Total" } ] }"""), Sale()).As<Money>().Should().Be(new Money(480));
        Run(Compile("""{ "+": [ { "var": "Total" }, 40 ] }"""), Sale()).As<Money>().Should().Be(new Money(1000));   // + still coerces
    }

    [Fact]
    public void Malformed_json_reports_the_path()
    {
        var act = () => Parse("""{ "and": [ { "==": 1 } ] }""");
        act.Should().Throw<JsonException>().Which.Path.Should().Be("$.and[0].==");
    }

    [Fact]
    public void Size_limits_apply()
    {
        var options = Options() with { Limits = new ExprLimits(MaxNodes: 3) };
        Errors(PixAndBig, options).Should().Equal("e:expr.too-large");
    }

    [Fact]
    public void Ref_struct_readers_work()
    {
        var p = Compile("""{ ">=": [ { "var": "Total" }, 500 ] }""");
        ReadOnlySpan<RawValue> fields = [RawValueConvert.From(new Money(960))];
        var r = new SpanReader(fields);
        var env = default(ExprEnv);
        p.RunBool(ref r, in env).Should().BeTrue();
    }

    [Fact]
    public void Warm_evaluation_does_not_allocate()
    {
        var condition = Compile(PixAndBig);
        var sum = Compile("""{ "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }""");
        var call = Compile("""{ "min": [ { "percentOf": [ { "var": "Total" }, 5 ] }, 30 ] }""");
        var r = Sale();
        var env = default(ExprEnv);
        for (int i = 0; i < 3; i++) { condition.RunBool(ref r, in env); sum.Run(ref r, in env); call.Run(ref r, in env); }   // warm + pool

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { condition.RunBool(ref r, in env); sum.Run(ref r, in env); call.Run(ref r, in env); }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void Schemas_require_dense_ids_and_hash_their_shape()
    {
        var act = () => new ExprSchema("bad", [new ExprField(1, "A", RawType.Int32)]);
        act.Should().Throw<ArgumentException>();
        new ExprSchema("x", [new ExprField(0, "A", RawType.Int32)]).Hash
            .Should().NotBe(new ExprSchema("x", [new ExprField(0, "A", RawType.Int64)]).Hash);
    }

    [Fact]
    public void A_binder_accumulates_per_item_use_across_expressions()
    {
        var errors = new List<Violation>();
        var binder = new ExprBinder(Options(), errors);
        binder.Bind(Parse("""{ "var": "Member" }"""), "a");
        binder.ItemCollection.Should().Be(-1);
        var b = binder.Bind(Parse("""{ ">": [ { "line": "Quantity" }, 1 ] }"""), "b");
        binder.ItemCollection.Should().Be(LinesId);
        b.Args[0].Kind.Should().Be(ExprKind.Line);
        b.Args[1].IsLiteral.Should().BeTrue();
        binder.Bind(Parse("1"), "c", new ExprType(Money.RawType)).Value.As<Money>().Should().Be(new Money(1));
        errors.Should().BeEmpty();
    }
}
