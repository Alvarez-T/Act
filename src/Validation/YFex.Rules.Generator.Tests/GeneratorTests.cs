using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YFex.Rules.Generator.Tests;

/// <summary>Driver tests: clean emission, every YFR diagnostic, incremental caching.</summary>
public class GeneratorTests
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> s_refs = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(YFex.Rules.ISituation<>).Assembly.Location);
        paths.Add(typeof(YFex.Validation.ViolationWriter).Assembly.Location);
        paths.Add(typeof(YFex.Money).Assembly.Location);
        paths.Add(typeof(YFex.Rules.Dynamic.RuleFunction).Assembly.Location);
        paths.Add(typeof(System.Collections.Immutable.ImmutableArray).Assembly.Location);
        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private const string Usings = """
        using System;
        using System.Collections.Immutable;
        using YFex;
        using YFex.Rules;
        using YFex.Validation;
        using static YFex.Rules.Cells;
        namespace App;
        public enum Pay { Cash, Card }

        """;

    private const string Situation = """
        [Situation("order")]
        public readonly partial record struct Order([property: RuleEffectiveDate] DateOnly Date, Pay Pay, [property: Requested] int Installments,
            ImmutableArray<OrderLine> Lines);
        [Situation("order.line")]
        public readonly partial record struct OrderLine(string Sku, decimal Qty, Money Price);

        """;

    private const string RuleSet = """
        [RuleSet<Order>("order")]
        public static partial class OrderRules
        {
            public static readonly Stage Main = Stage.First("main");
            public static readonly RuleGroup Limits = new(Main, Resolve.MostRestrictive);
            public static Money LineBase(in Order s, in OrderLine l) => l.Price * l.Qty;
        }

        """;

    private static CSharpCompilation Compile(string source)
        => CSharpCompilation.Create("GenTests", [CSharpSyntaxTree.ParseText(source, Parse, path: "Input.cs")], s_refs.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static GeneratorDriver Driver()
        => CSharpGeneratorDriver.Create([new RulesGenerator().AsSourceGenerator()], parseOptions: Parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out var diags);
        return (output, diags);
    }

    private static IEnumerable<Diagnostic> Problems(Compilation c) => c.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning);

    [Fact]
    public void Valid_rule_set_compiles_clean()
    {
        var (output, diags) = Run(Usings + Situation + RuleSet + """
            [Rule("card.max", Group = nameof(OrderRules.Limits))]
            public readonly struct CardMax : IRule<Order>
            {
                public static bool Applies(in Order s) => s.Pay == Pay.Card && s.Installments > 0;
                public static void Evaluate(in Order s, ref DirectiveWriter d) => d.Limit(Order.Fields.Installments, max: 6);
            }
            [DecisionTable<Order>("order.cash", Group = nameof(OrderRules.Limits))]
            public partial class CashTable
            {
                public readonly partial record struct Row([property: When("Pay")] Match<Pay> Pay, [property: Then(DirectiveKind.LimitMax, "Installments")] int Max);
                public static readonly Row[] DefaultRows = [new(Is(Pay.Cash), 1)];
            }
            """);
        diags.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        Problems(output).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""[Situation("x")] public readonly record struct NotPartial(int A);""", "YFR0001")]
    [InlineData("""[Situation("x")] public readonly partial record struct Bad(System.Uri Link);""", "YFR0002")]
    [InlineData("""[Situation("x")] public readonly partial record struct TwoDates([property: RuleEffectiveDate] DateOnly A, [property: RuleEffectiveDate] DateOnly B);""", "YFR0003")]
    [InlineData("""
        [Rule("r", Group = nameof(OrderRules.Limits))]
        public readonly struct NoEvaluate : IRule<Order> { public static bool Applies(in Order s) => true; }
        """, "YFR0010")]
    [InlineData("""
        [Rule("r", Group = "Nope")]
        public readonly struct UnknownGroup : IRule<Order>
        { public static bool Applies(in Order s) => true; public static void Evaluate(in Order s, ref DirectiveWriter d) { } }
        """, "YFR0011")]
    [InlineData("""
        [Rule("r")]
        public readonly struct NoGroup : IRule<Order>
        { public static bool Applies(in Order s) => true; public static void Evaluate(in Order s, ref DirectiveWriter d) { } }
        """, "YFR0015")]
    [InlineData("""
        [DecisionTable<Order>("t", Group = nameof(OrderRules.Limits))]
        public partial class Typo { public readonly partial record struct Row([property: When("Payy")] Match<Pay> Pay, [property: Then(DirectiveKind.LimitMax, "Installments")] int Max); }
        """, "YFR0101")]
    [InlineData("""
        [DecisionTable<Order>("t", Group = nameof(OrderRules.Limits))]
        public partial class WrongCell { public readonly partial record struct Row([property: When("Pay")] Match<int> Pay, [property: Then(DirectiveKind.LimitMax, "Installments")] int Max); }
        """, "YFR0102")]
    [InlineData("""
        [DecisionTable<Order>("t", Group = nameof(OrderRules.Limits))]
        public class NotPartialTable { public readonly partial record struct Row([property: When("Pay")] Match<Pay> Pay); }
        """, "YFR0103")]
    [InlineData("""
        [DecisionTable<Order>("t", Group = nameof(OrderRules.Limits))]
        public partial class BadThen { public readonly partial record struct Row([property: When("Pay")] Match<Pay> Pay, [property: Then(DirectiveKind.Grant, "Installments")] int Max); }
        """, "YFR0105")]
    [InlineData("""[RuleSet<Order>("o2")] public static class NotPartialSet { }""", "YFR0020")]
    [InlineData("""[RuleSet<Order>("o3")] public static partial class TextBase { public static readonly RuleGroup G = new(Stage.First("s"), Resolve.All); public static string Base(in Order s) => ""; }""", "YFR0022")]
    [InlineData("""
        [Situation("deep")] public readonly partial record struct Deep([property: RuleTier] int A, [property: RuleTier] int B,
            [property: RuleTier] int C, [property: RuleTier] int D, [property: RuleTier] int E);
        """, "YFR0007")]
    [InlineData("""
        [DecisionTable<Order>("t", Group = nameof(OrderRules.Limits), EditableAt = new[] { "Tenant" })]
        public partial class NoSuchTier { public readonly partial record struct Row([property: When("Pay")] Match<Pay> Pay, [property: Then(DirectiveKind.LimitMax, "Installments")] int Max); }
        """, "YFR0107")]
    public void Reports_diagnostics(string code, string id)
        => Run(Usings + Situation + RuleSet + code).Diagnostics.Select(d => d.Id).Should().Contain(id);

    [Fact]
    public void SituationSource_mismatched_member_is_an_error()
        => Run(Usings + Situation + RuleSet + """
            [SituationSource<Order>]
            public partial class OrderVm
            {
                public string Pay { get; set; } = "";                  // Pay is an enum in the situation
                public int Installments { get; set; }
                public System.Collections.Generic.List<OrderLineVm> Lines { get; } = new();
                private partial DateOnly __Situation_Date() => DateOnly.MinValue;
            }
            [SituationSource<OrderLine>]
            public partial class OrderLineVm { public string Sku = ""; public decimal Qty; public Money Price; }
            """).Diagnostics.Select(d => d.Id).Should().Contain("YFR0031");

    [Fact]
    public void SituationSource_unmatched_field_must_be_implemented()
    {
        var (output, diags) = Run(Usings + Situation + RuleSet + """
            [SituationSource<Order>]
            public partial class OrderVm
            {
                public Pay Pay { get; set; }
                public int Installments { get; set; }
                public ImmutableArray<OrderLine> Lines { get; set; }
            }
            """);
        diags.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        // Date is not on the VM → the generated partial declaration has no implementation (the compiler names it).
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().ContainSingle().Which.GetMessage().Should().Contain("__Situation_Date");
    }

    [Fact]
    public void Non_splittable_applies_is_reported_as_info()
    {
        var (output, diags) = Run(Usings + Situation + RuleSet + """
            [Rule("ctx", Group = nameof(OrderRules.Limits))]
            public readonly struct UsesContext : IRule<Order>
            {
                public static bool Applies(in Order s, in RuleContext c) => c.Base > RawValue.From(0);
                public static void Evaluate(in Order s, ref DirectiveWriter d) { }
            }
            """);
        diags.Should().ContainSingle(d => d.Id == "YFR0016" && d.Severity == DiagnosticSeverity.Info);
        Problems(output).Should().BeEmpty();
    }

    [Fact]
    public void Diagnostics_have_in_source_locations_so_pragma_applies()
    {
        var (_, diags) = Run(Usings + Situation + RuleSet + """
            [Rule("ctx", Group = nameof(OrderRules.Limits))]
            public readonly struct UsesContext : IRule<Order>
            {
                public static bool Applies(in Order s, in RuleContext c) => c.Base > RawValue.From(0);
                public static void Evaluate(in Order s, ref DirectiveWriter d) { }
            }
            """);
        diags.Single(d => d.Id == "YFR0016").Location.SourceTree.Should().NotBeNull();
    }

    [Fact]
    public void Editing_an_unrelated_file_keeps_the_models_cached()
    {
        var compilation = Compile(Usings + Situation + RuleSet + """
            [Rule("card.max", Group = nameof(OrderRules.Limits))]
            public readonly struct CardMax : IRule<Order>
            {
                public static bool Applies(in Order s) => s.Pay == Pay.Card;
                public static void Evaluate(in Order s, ref DirectiveWriter d) => d.Limit(Order.Fields.Installments, max: 6);
            }
            """);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace App; public class Unrelated { }", Parse, path: "Other.cs")));
        var result = driver.GetRunResult().Results[0];

        foreach (var name in new[] { "YFexRules.Situations", "YFexRules.Rules", "YFexRules.RuleSets", "YFexRules.Assembled" })
            result.TrackedSteps[name].SelectMany(s => s.Outputs).Select(o => o.Reason)
                .Should().OnlyContain(r => r == IncrementalStepRunReason.Cached || r == IncrementalStepRunReason.Unchanged, name);
    }

    [Fact]
    public void Non_money_amounts_custom_types_and_app_tiers_compile_clean()
    {
        var (output, diags) = Run(Usings + """
            public readonly record struct Temperature(decimal Celsius) : IRawValue<Temperature>, System.IComparable<Temperature>
            {
                public static RawType RawType { get; } = RawType.Define<Temperature>("gen.temperature", ValueKind.Decimal);
                public RawValue ToRaw() => RawValue.Typed(RawType, Celsius);
                public static bool TryFromRaw(in RawValue raw, out Temperature value) { bool ok = raw.TryRead(RawType, out decimal c); value = new(c); return ok; }
                public int CompareTo(Temperature other) => Celsius.CompareTo(other.Celsius);
            }
            public readonly record struct CarrierId(string Value);
            [Situation("shipment")]
            public readonly partial record struct Shipment([property: RuleTier] CarrierId Carrier, [property: RuleTier] string Hub,
                [property: RuleEffectiveDate] DateOnly ShipDate, Temperature Storage, [property: Requested] Mass Declared,
                ImmutableArray<Parcel> Parcels);
            [Situation("shipment.parcel")]
            public readonly partial record struct Parcel(Mass Actual, bool Fragile);
            [RuleSet<Shipment>("freight")]
            public static partial class FreightRules
            {
                public static readonly Stage Packing = Stage.First("packing");
                public static readonly RuleGroup Tare = new(Packing, Resolve.Stack(capAmount: Mass.FromGrams(40_000)));
                public static Mass LineBase(in Shipment s, in Parcel p) => p.Actual;
                public static readonly DerivedField<Shipment, Mass> Billable = DerivedField.After(Packing, "Billable",
                    static (in Shipment s, in RunningTotals<Mass> t) => t.Final);
            }
            [Rule("pallet", Group = nameof(FreightRules.Tare))]
            public readonly struct Pallet : IRule<Shipment>
            {
                public static readonly RuleParameter<Mass> PerPallet = new("per-pallet", Mass.FromGrams(25_000), [nameof(Shipment.Carrier)]);
                public static bool Applies(in Shipment s) => s.Parcels.Length > 10;
                public static void Evaluate(in Shipment s, in RuleContext c, ref DirectiveWriter d) => d.Increase(AdjustTarget.Total, c.Get(PerPallet));
            }
            [DecisionTable<Shipment>("fragile", Group = nameof(FreightRules.Tare), EditableAt = new[] { nameof(Shipment.Carrier), nameof(Shipment.Hub) })]
            public partial class FragileTable
            {
                public readonly partial record struct Row([property: When("Parcels[].Fragile")] Match<bool> Fragile,
                    [property: Then(DirectiveKind.IncreasePercent)] Percentual Padding, [property: Then(DirectiveKind.DecreaseAmount)] Mass Credit);
                public static readonly Row[] DefaultRows = [new(Is(true), Percentual.FromPercentage(8), Mass.Zero)];
            }
            [DecisionTable<Shipment>("cold", Group = nameof(FreightRules.Tare))]
            public partial class ColdTable
            {
                public readonly partial record struct Row([property: When("Storage")] Band<Temperature> Storage, [property: Then(DirectiveKind.Warn)] string? Warning);
                public static readonly Row[] DefaultRows = [];
            }
            """);
        diags.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        Problems(output).Should().BeEmpty();
        var text = string.Join("\n", output.SyntaxTrees.Select(t => t.ToString()));
        text.Should().Contain("RuleTierPath.Of(").And.Contain(".Amount<global::YFex.Mass>()").And.Contain("global::App.Temperature.RawType")
            .And.Contain("tier: 2").And.Contain("d.Increase(global::YFex.Rules.AdjustTarget.Line, row.Padding)");
    }

    // ── Rule functions (YFex.Rules.Dynamic, Phase 6) ─────────────────────────

    private const string FunctionUsings = """
        using System;
        using YFex;
        using YFex.Rules.Dynamic;
        namespace App;
        public enum Tier { Basic, Gold }

        """;

    [Fact]
    public void Rule_function_classes_and_contract_interfaces_compile_clean()
    {
        var (output, diags) = Run(FunctionUsings + """
            [RuleFunctions(Category = "app")]
            public static partial class AppFunctions
            {
                [RuleFunction("discountFor", Description = "Percent for a tier.")]
                public static decimal DiscountFor(Tier tier) => tier == Tier.Gold ? 5m : 0m;
                [RuleFunction(Description = "Money rounded to tens.")]
                public static Money Tens(Money value) => new(Math.Round(value.Amount / 10m) * 10m);
            }
            [RuleFunctions]
            public partial interface ICalendar
            {
                [RuleFunction("isHoliday", Description = "Public holiday.")]
                static abstract bool IsHoliday(DateOnly date);
            }
            public sealed class Calendar : ICalendar { public static bool IsHoliday(DateOnly date) => false; }
            public static class Use { public static int N => AppFunctions.All.Length + ICalendar.Contracts.Length + ICalendar.Implement<Calendar>().Length; }
            """);
        diags.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        Problems(output).Should().BeEmpty();
        var text = string.Join("\n", output.SyntaxTrees.Select(t => t.ToString()));
        text.Should().Contain("static partial class AppFunctions").And.Contain("RuleFunctionArgs.GetEnum<global::App.Tier>(a[0])")
            .And.Contain("TImpl.IsHoliday(").And.Contain("RuleFunction.Contract(\"isHoliday\"");
    }

    [Theory]
    [InlineData("public static class F { [RuleFunction(Description = \"x\")] public static int A() => 1; }", "YFR0040")]
    [InlineData("public static partial class F { [RuleFunction(Description = \"x\")] public static int A(object o) => 1; }", "YFR0041")]
    [InlineData("public static partial class F { [RuleFunction(Description = \"x\")] public static void A() { } }", "YFR0042")]
    [InlineData("public static partial class F { [RuleFunction(\"a\", Description = \"x\")] public static int A() => 1; [RuleFunction(\"a\", Description = \"y\")] public static int B() => 2; }", "YFR0043")]
    [InlineData("public static partial class F { [RuleFunction] public static int A() => 1; }", "YFR0044")]
    public void Rule_function_diagnostics(string declaration, string id)
    {
        var (_, diags) = Run(FunctionUsings + "[RuleFunctions]\n" + declaration);
        diags.Select(d => d.Id).Should().Contain(id);
    }
}
