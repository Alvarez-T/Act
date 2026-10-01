using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YFex.Validation.Generator.Tests;

/// <summary>Fluent <c>.Validation(v =&gt; …)</c> as a source (03 §4, D23/D27, G8).</summary>
public class FluentGeneratorTests
{
    // Any method named Validation taking Action<ValidationBuilder<T>> is a source — YFex.Cqrs builders are one shape.
    private const string Prelude = """
        using System; using System.Collections.Generic; using YFex; using YFex.Validation;
        namespace App;
        public sealed class Cfg<T> { public Cfg<T> Validation(Action<ValidationBuilder<T>> rules) => this; }
        """;

    private static string Init(GeneratorRun run) => run.Sources["YFex.Validation.ModuleInitializer.g.cs"];

    [Fact]
    public void FluentOnly_message_gets_compiled_rules_and_key_list()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public sealed partial record CreateCustomer(string Name, int Age, string Email);
            public static class Config
            {
                public static void Configure(Cfg<CreateCustomer> b) => b.Validation(v =>
                {
                    v.Field(c => c.Name).Required().Length(2, 80);
                    v.Field(c => c.Age).Range(0, 150);
                    v.Field(c => c.Email).Email().Severity(Severity.Warning);
                });
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Ids(DiagnosticSeverity.Warning).Should().BeEmpty();
        string src = run.Source("App.CreateCustomer");
        src.Should().Contain("partial record CreateCustomer : global::YFex.Validation.IValidatable<CreateCustomer>");
        src.Should().Contain("public const int MaxLength = 80;");
        src.Should().Contain("if (value < 0 || value > 150)");
        Init(run).Should().Contain("global::YFex.Validation.ValidationSlots.Register<global::App.CreateCustomer>();");
        Init(run).Should().Contain(
            "global::YFex.Validation.CompiledFluentRules<global::App.CreateCustomer>.Set(\"Age|range:0:150:0\", \"Email|email\", \"Name|len:2:80\", \"Name|required\");");
    }

    [Fact]
    public void NonConstant_arguments_stay_runtime_only()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public sealed partial record Cmd(string Name, string Nick);
            public static class Config
            {
                public static int MaxNick = 20;
                public static void Configure(Cfg<Cmd> b) => b.Validation(v =>
                {
                    v.Field(c => c.Name).Required();
                    v.Field(c => c.Nick).Length(1, MaxNick);
                    v.Invariant(static (in Cmd c, ref ViolationWriter w) => { });
                });
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Ids().Should().Contain("YFV0004");
        Init(run).Should().Contain("CompiledFluentRules<global::App.Cmd>.Set(\"Name|required\");");
        run.Source("App.Cmd").Should().NotContain("LengthWithin");
    }

    [Fact]
    public void Not_partial_message_is_runtime_only_with_warning()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public sealed record Cmd(string Name);
            public static class Config
            {
                public static void Configure(Cfg<Cmd> b) => b.Validation(v => v.Field(c => c.Name).Required());
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Ids().Should().Contain("YFV0003");
        run.Sources.Keys.Should().NotContain(k => k.Contains("App.Cmd"));
    }

    [Fact]
    public void When_on_bool_member_is_compiled_and_keyed_by_lambda_text()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public sealed partial record Cmd(string Document, bool IsCompany, int Age);
            public static class Config
            {
                public static void Configure(Cfg<Cmd> b) => b.Validation(v =>
                {
                    v.Field(c => c.Document).Required().When(c => c.IsCompany);
                    v.Field(c => c.Age).Range(18, 99).When(c => c.Age > 0 && c.IsCompany);
                });
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Source("App.Cmd").Should().Contain("v.IsCompany");
        Init(run).Should().Contain("\"Document|required|when:c => c.IsCompany\"");
        Init(run).Should().NotContain("Age|range", "an arbitrary When lambda cannot be compiled");
        run.Ids().Should().Contain("YFV0004");
    }

    [Fact]
    public void Attribute_and_fluent_rules_are_unioned_and_conflicts_reported()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public sealed partial record Cmd
            {
                [Required, Length(2, 80)] public string Name { get; init; } = "";
                public string Code { get; init; } = "";
            }
            public static class Config
            {
                public static void Configure(Cfg<Cmd> b) => b.Validation(v =>
                {
                    v.Field(c => c.Name).Required();          // identical → deduplicated
                    v.Field(c => c.Name).Length(1, 60);       // same rule, other arguments → YFV0011
                    v.Field(c => c.Code).Pattern("^[A-Z]+$");
                });
            }
            """);

        run.Ids().Should().Contain("YFV0011");
        string src = run.Source("App.Cmd");
        src.Should().Contain("LengthWithin(value.Length, 2, 80)");
        src.Should().Contain("__Pattern_0", "the fluent Pattern lands next to the attribute rules");
    }

    [Fact]
    public void Two_configurations_for_the_same_message_are_merged()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public sealed partial record Cmd(string Name, int Age);
            public static class Baseline { public static void Configure(Cfg<Cmd> b) => b.Validation(v => v.Field(c => c.Name).Required()); }
            public static class Server { public static void Configure(Cfg<Cmd> b) => b.Validation(v => v.Field(c => c.Age).Range(1, 9)); }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Sources.Keys.Count(k => k.Contains("App.Cmd")).Should().Be(1);
        Init(run).Should().Contain("CompiledFluentRules<global::App.Cmd>.Set(\"Age|range:1:9:0\", \"Name|required\");");
    }

    [Fact]
    public void Each_over_elements_made_validatable_by_this_generator_is_compiled()
    {
        // Before generation Line is not IValidatable<Line>, so Each<TItem>'s constraint fails and the lambda does not
        // bind in the generator's semantic model — the parser must still resolve the single candidate.
        var run = GeneratorHarness.Run(Prelude + """
            public sealed partial record Line([property: Range(1, 99)] int Quantity);
            public sealed partial record Sale(IReadOnlyList<Line> Lines);
            public sealed record LooseSale(IReadOnlyList<Line> Lines);
            public static class Config
            {
                public static void Configure(Cfg<Sale> b) => b.Validation(v => v.Each(c => c.Lines));
                public static void Loose(Cfg<LooseSale> b) => b.Validation(v => v.Each(c => c.Lines));
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Source("App.Sale").Should().Contain("global::YFex.Validation.Validator.ValidateAt<global::App.Line>");
        Init(run).Should().Contain("CompiledFluentRules<global::App.Sale>.Set(\"Lines|each\");");
        run.Ids().Should().Contain("YFV0003", "LooseSale is not partial");
    }

    [Fact]
    public void ViewModel_borrows_rules_of_a_fluent_only_contract()
    {
        var run = GeneratorHarness.RunWithState(Prelude + """
            public sealed partial record CreateCustomer(string Name);
            public static class Config
            {
                public static void Configure(Cfg<CreateCustomer> b) => b.Validation(v => v.Field(c => c.Name).Required().Length(2, 80));
            }
            [ValidatesAs<CreateCustomer>]
            public partial class CustomerVm : YFex.State.StateObject
            {
                [YFex.State.Observable] public partial string Name { get; set; }
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Source("App.CustomerVm").Should().Contain("global::App.CreateCustomer.Rules.Name.Check(");
    }

    [Fact]
    public void UnrelatedEdit_keeps_fluent_step_cached()
    {
        const string source = Prelude + """
            public sealed partial record Cmd(string Name);
            public static class Config { public static void Configure(Cfg<Cmd> b) => b.Validation(v => v.Field(c => c.Name).Required()); }
            """;
        var compilation = GeneratorHarness.CreateCompilation(source);
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace App; public class Unrelated { public int Y; }", GeneratorHarness.ParseOptions));
        driver = driver.RunGenerators(edited);

        var steps = driver.GetRunResult().Results[0].TrackedSteps;
        foreach (var name in new[] { TrackingNames.Fluent, TrackingNames.FinalModels })
            steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason)
                .Should().OnlyContain(r => r == IncrementalStepRunReason.Cached || r == IncrementalStepRunReason.Unchanged);
    }
}
