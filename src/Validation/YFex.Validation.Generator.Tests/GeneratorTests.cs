using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YFex.Validation.Generator.Tests;

public class EmissionTests
{
    private const string Usings = "using System; using System.Collections.Generic; using System.Collections.Immutable; using YFex; using YFex.Validation;\n";

    [Fact]
    public void AttributeType_EmitsNormativeShape_AndCompilesClean()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace App;
            public sealed partial record CustomerDto
            {
                [Required, Length(2, 80)] public string Name { get; init; } = "";
                [Range(0, 150)] public int Age { get; init; }
                public string Untouched { get; init; } = "";
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        string src = run.Source("App.CustomerDto");
        src.Should().Contain("partial record CustomerDto : global::YFex.Validation.IValidatable<CustomerDto>");
        src.Should().Contain("public const ushort Name = 0;");
        src.Should().Contain("public static readonly global::YFex.FieldPath Name = global::YFex.FieldPath.Of(\"Name\");");
        src.Should().Contain("public const int MaxLength = 80;");
        src.Should().Contain("!global::YFex.Validation.BuiltIn.Checks.LengthWithin(value.Length, 2, 80)");
        src.Should().Contain("if (value < 0 || value > 150)");
        src.Should().Contain("public static class Untouched", "every member gets a Rules class (State calls them by convention)");
        src.Should().Contain("global::YFex.Validation.RuntimeRules<CustomerDto>.Run(in v, ref w);");
    }

    [Fact]
    public void Emission_IsDeterministic()
    {
        const string source = Usings + "namespace App; public partial record A { [Required] public string? X { get; init; } [Each] public List<B> Bs { get; init; } = []; } public partial record B { [Range(1, 2)] public int Y { get; init; } }";
        var first = GeneratorHarness.Run(source).Sources;
        var second = GeneratorHarness.Run(source).Sources;
        second.Should().Equal(first);
    }

    [Fact]
    public void DataAnnotations_MapToBuiltIns()
    {
        var run = GeneratorHarness.Run(Usings + """
            using DA = System.ComponentModel.DataAnnotations;
            namespace App;
            public partial class Legacy
            {
                [DA.Required, DA.StringLength(10, MinimumLength = 2)] public string? Name { get; set; }
                [DA.MaxLength(3)] public string? Code { get; set; }
            }
            """);
        run.CompileProblems.Should().BeEmpty();
        string src = run.Source("App.Legacy");
        src.Should().Contain("LengthWithin(value.Length, 2, 10)");
        src.Should().Contain("\"len.max\"");
    }

    [Fact]
    public void Each_Nested_When_Profile_Struct_Invariants()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace App;
            public partial record struct Line([Required] string Sku, [Range(1, 99)] int Qty);
            public partial class Order
            {
                public bool Rush { get; set; }
                [Each] public ImmutableArray<Line> Lines { get; set; }
                [Required(When = nameof(Rush))] public string? Courier { get; set; }
                [Required(Profile = "final")] public string? Approver { get; set; }
                public Address? Ship { get; set; }

                [Invariant]
                static void CourierNeedsLines(in Order o, ref ViolationWriter w)
                {
                    if (o.Courier is not null && o.Lines.IsDefaultOrEmpty) w.Add(in Fields.Courier, "order.courier");
                }
            }
            public partial class Address { [Required] public string? City { get; set; } }
            """);

        run.CompileProblems.Should().BeEmpty();
        string src = run.Source("App.Order");
        src.Should().Contain("ref readonly var __e = ref __c.ItemRef(__i);");
        src.Should().Contain("Validator.ValidateAt<global::App.Line>(in __e, ref w, in __p);");
        src.Should().Contain("if (v.Rush && (string.IsNullOrWhiteSpace(__m)))");
        src.Should().Contain("profile.Includes(\"final\")");
        src.Should().Contain("Validator.ValidateAt<global::App.Address>(in __n, ref w, in Fields.Ship);");
        src.Should().Contain("if (!w.HasErrorFor(in Fields.Courier) && !w.HasErrorFor(in Fields.Lines))");
        run.Source("ModuleInitializer").Should().Contain("ValidationSlots.Register<global::App.Order>();");
    }

    [Fact]
    public void GenericAndPrivateNestedTypes_AreNotRegistered()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace App;
            public partial class Box<T> { [Required] public string? Name { get; set; } }
            public partial class Outer { private partial class Hidden { [Required] public string? X { get; set; } } }
            public partial class Plain { [Required] public string? X { get; set; } }
            """);
        run.CompileProblems.Should().BeEmpty();
        string init = run.Source("ModuleInitializer");
        init.Should().Contain("Register<global::App.Plain>");
        init.Should().NotContain("Box").And.NotContain("Hidden");
    }

    [Fact]
    public void MessagesJson_GeneratesCatalog()
    {
        var run = GeneratorHarness.Run("namespace App; public class X {}",
            additionalFiles:
            [
                ("C:/p/Messages/messages.json", """{ "sale.cap": "Max {0}", "fields": { "SaleVm.Discount": "Discount" } }"""),
                ("C:/p/Messages/messages.pt-BR.json", """{ "sale.cap": "Máximo {0}" }"""),
                ("C:/p/other.json", """{ "ignored": "x" }"""),
            ],
            buildProperties: new Dictionary<string, string> { ["RootNamespace"] = "My.Sales" });

        run.CompileProblems.Should().BeEmpty();
        string src = run.Source("Messages.g.cs");
        src.Should().Contain("namespace My.Sales");
        src.Should().Contain("public sealed class SalesMessages : global::YFex.Localization.IMessageSource");
        src.Should().Contain("case \"pt-BR\":");
        src.Should().Contain("case \"sale.cap\": template = \"Máximo {0}\"; return true;");
        src.Should().Contain("case \"SaleVm.Discount\": name = \"Discount\"; return true;");
        src.Should().NotContain("ignored");
    }
}

public class DiagnosticTests
{
    private const string Usings = "using System; using System.Collections.Generic; using YFex; using YFex.Validation;\n";

    private static IEnumerable<string> Ids(string body, IDictionary<string, string>? props = null)
        => GeneratorHarness.Run(Usings + "namespace App;\n" + body, buildProperties: props).Ids();

    [Fact] public void YFV0001_NotPartial() => Ids("public class A { [Required] public string? X { get; set; } }").Should().Contain("YFV0001");

    [Fact] public void YFV0001_ContainerNotPartial() => Ids("public class O { public partial class A { [Required] public string? X { get; set; } } }").Should().Contain("YFV0001");

    [Fact] public void YFV0002_NotApplicable() => Ids("public partial class A { [Length(1, 2)] public int X { get; set; } }").Should().Contain("YFV0002");

    [Fact] public void YFV0002_EachOnNonCollection() => Ids("public partial class A { [Each] public int X { get; set; } }").Should().Contain("YFV0002");

    [Fact]
    public void YFV0005_UnsupportedRuleType()
        => Ids("public struct NotARule {} public partial class A { [ValidateWith<NotARule>] public int X { get; set; } }").Should().Contain("YFV0005");

    [Fact] public void YFV0006_MinGreaterThanMax() => Ids("public partial class A { [Range(10, 1)] public int X { get; set; } }").Should().Contain("YFV0006");

    [Fact] public void YFV0006_Unparsable() => Ids("public partial class A { [Range(\"abc\", \"9\")] public decimal X { get; set; } }").Should().Contain("YFV0006");

    [Fact] public void YFV0006_BadDate() => Ids("public partial class A { [Range(\"2020-13-45\", \"2021-01-01\")] public DateOnly X { get; set; } }").Should().Contain("YFV0006");

    [Fact] public void YFV0007_BadPattern() => Ids("public partial class A { [Pattern(\"([a-\")] public string? X { get; set; } }").Should().Contain("YFV0007");

    [Fact] public void YFV0008_BadInvariant() => Ids("public partial class A { [Required] public string? X { get; set; } [Invariant] static void Bad(int x) {} }").Should().Contain("YFV0008");

    [Fact]
    public void YFV0009_Info_WhenReadsUnknown()
    {
        var run = GeneratorHarness.Run(Usings + "namespace App; public partial class A { [Required] public string? X { get; set; } [Invariant] static void I(in A a, ref ViolationWriter w) { Helper(a); } static void Helper(A a) {} }");
        run.GeneratorDiagnostics.Should().Contain(d => d.Id == "YFV0009" && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void YFV0009_Warning_WhenExplicitReadsDisagree()
    {
        var run = GeneratorHarness.Run(Usings + "namespace App; public partial class A { [Required] public string? X { get; set; } public int Y { get; set; } [Invariant(Reads = [nameof(Y)])] static void I(in A a, ref ViolationWriter w) { if (a.X is null) {} } }");
        run.GeneratorDiagnostics.Should().Contain(d => d.Id == "YFV0009" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void YFV0010_DataAnnotationsStacked()
        => Ids("public partial class A { [Required, System.ComponentModel.DataAnnotations.Required] public string? X { get; set; } }").Should().Contain("YFV0010");

    [Fact]
    public void YFV0012_Cycle()
        => Ids("public partial class A { [Required] public string? N { get; set; } public B Next { get; set; } = null!; } public partial class B { [Required] public string? N { get; set; } public A Back { get; set; } = null!; }")
            .Should().Contain("YFV0012");

    [Fact]
    public void YFV0012_NotReportedForNullableLinks()
        => Ids("public partial class A { [Required] public string? N { get; set; } public A? Parent { get; set; } }").Should().NotContain("YFV0012");

    [Fact]
    public void YFV0013_NameClash()
    {
        var run = GeneratorHarness.Run(Usings + "namespace App; public partial class A { [Required] public string? Rules { get; set; } }");
        run.Ids().Should().Contain("YFV0013");
        run.CompileProblems.Should().BeEmpty();
        run.Source("App.A").Should().Contain("public static class ValidationRules");
    }

    [Fact]
    public void YFV0014_TooDeep()
        => Ids("""
            public partial class L5 { [Required] public string? X { get; set; } }
            public partial class L4 { [Each] public List<L5> Items { get; set; } = []; }
            public partial class L3 { [Each] public List<L4> Items { get; set; } = []; }
            public partial class L2 { [Each] public List<L3> Items { get; set; } = []; }
            public partial class L1 { [Each] public List<L2> Items { get; set; } = []; }
            public partial class L0 { [Each] public List<L1> Items { get; set; } = []; }
            """).Should().Contain("YFV0014");

    [Fact] public void YFV0015_BadWhen() => Ids("public partial class A { public int Flag { get; set; } [Required(When = \"Flag\")] public string? X { get; set; } }").Should().Contain("YFV0015");

    [Fact]
    public void YFV0030_RequiredInferred_OnlyWhenEnabled()
    {
        const string body = "public partial class A { [Length(1, 5)] public string Name { get; set; } = \"\"; public string? Opt { get; set; } }";
        Ids(body).Should().NotContain("YFV0030");

        var run = GeneratorHarness.Run(Usings + "namespace App;\n" + body,
            buildProperties: new Dictionary<string, string> { ["YFexValidationInferRequired"] = "true" });
        run.Ids().Should().Contain("YFV0030");
        run.GeneratorDiagnostics.Single(d => d.Id == "YFV0030").GetMessage().Should().Contain("Name").And.NotContain("Opt");
        run.Source("App.A").Should().Contain("string.IsNullOrWhiteSpace(value)");
    }

    [Fact]
    public void YFV0041_InvalidMessagesJson()
    {
        var run = GeneratorHarness.Run("namespace App; public class X {}", additionalFiles: [("messages.json", "{ \"a\": ")]);
        run.Ids().Should().Contain("YFV0041");
    }

    [Fact]
    public void StateObjects_AreNotValidatable_TheyGetHooks()
    {
        var run = GeneratorHarness.RunWithState(Usings + "using YFex.State; namespace App { public partial class Vm : StateObject { [Observable, Required] public partial string? X { get; set; } } }");

        run.CompileProblems.Should().BeEmpty();
        string src = run.Source("App.Vm.Validation");
        src.Should().NotContain("IValidatable<Vm>", "view models validate through their bag, not statically");
        src.Should().Contain("partial void __OnValidate_X(in string? value)");
        run.Sources.Should().NotContainKey("YFex.Validation.ModuleInitializer.g.cs");
    }
}

public class CachingTests
{
    [Fact]
    public void UnrelatedEdit_KeepsModelsCached()
    {
        const string source = "using YFex.Validation; namespace App; public partial class A { [Required] public string? X { get; set; } }";
        var compilation = GeneratorHarness.CreateCompilation(source);
        var driver = GeneratorHarness.CreateDriver(trackSteps: true);
        driver = driver.RunGenerators(compilation);

        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace App; public class Unrelated { public int Y; }", GeneratorHarness.ParseOptions));
        driver = driver.RunGenerators(edited);

        var steps = driver.GetRunResult().Results[0].TrackedSteps;
        foreach (var name in new[] { TrackingNames.Models, TrackingNames.FinalModels })
        {
            steps.Should().ContainKey(name);
            steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason)
                .Should().OnlyContain(r => r == IncrementalStepRunReason.Cached || r == IncrementalStepRunReason.Unchanged);
        }
    }
}

public class MissingMessageTests
{
    private const string Source = """
        using YFex; using YFex.Validation;
        namespace App;
        public partial class A
        {
            [Required(Code = "a.name")] public string? Name { get; set; }
            [Required, FieldCode("a.code")] public string? Code { get; set; }
            [Range(1, 2)] public int N { get; set; }
            [Invariant] static void I(in A a, ref ViolationWriter w) { if (a.N == 2) w.Add(FieldPath.Empty, "a.inv"); }
        }
        """;

    [Fact]
    public void YFV0040_ReportsCodesMissingFromDefaultCulture()
    {
        var run = GeneratorHarness.Run(Source, additionalFiles:
        [
            ("messages.json", """{ "a.name": "Name!" }"""),
            ("messages.pt-BR.json", """{ "a.code": "Código!" }"""),
            ("messages.en.json", """{ "a.inv": "only in a non-default culture" }"""),
        ]);
        var missing = run.GeneratorDiagnostics.Where(d => d.Id == "YFV0040").Select(d => d.GetMessage()).ToList();
        missing.Should().ContainSingle().Which.Should().Contain("a.inv");
    }

    [Fact]
    public void YFV0040_InactiveWithoutMessagesFiles()
        => GeneratorHarness.Run(Source).Ids().Should().NotContain("YFV0040");
}
