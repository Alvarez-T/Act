using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YFex.Expressions.Generator.Tests;

/// <summary>Driver tests for <c>[ExprFunctions]</c> (ported from YFex.Rules.Generator.Tests in Phase X; YFR0040–0044 → YFE0001–0005).</summary>
public class GeneratorTests
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> s_refs = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(YFex.Money).Assembly.Location);
        paths.Add(typeof(YFex.Expressions.ExprFunction).Assembly.Location);
        paths.Add(typeof(YFex.Validation.Violation).Assembly.Location);
        paths.Add(typeof(System.Collections.Immutable.ImmutableArray).Assembly.Location);
        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    private const string Usings = """
        using System;
        using YFex;
        using YFex.Expressions;
        namespace App;
        public enum Tier { Basic, Gold }

        """;

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var compilation = CSharpCompilation.Create("GenTests", [CSharpSyntaxTree.ParseText(source, Parse, path: "Input.cs")], s_refs.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new ExpressionsGenerator().AsSourceGenerator()], parseOptions: Parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diags);
        return (output, diags);
    }

    private static IEnumerable<Diagnostic> Problems(Compilation c) => c.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning);

    [Fact]
    public void Function_classes_and_contract_interfaces_compile_clean()
    {
        var (output, diags) = Run(Usings + """
            [ExprFunctions(Category = "app")]
            public static partial class AppFunctions
            {
                [ExprFunction("discountFor", Description = "Percent for a tier.")]
                public static decimal DiscountFor(Tier tier) => tier == Tier.Gold ? 5m : 0m;
                [ExprFunction(Description = "Money rounded to tens.")]
                public static Money Tens(Money value) => new(Math.Round(value.Amount / 10m) * 10m);
                [ExprFunction("today", Description = "The current date.", Deterministic = false)]
                public static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
            }
            [ExprFunctions]
            public partial interface ICalendar
            {
                [ExprFunction("isHoliday", Description = "Public holiday.")]
                static abstract bool IsHoliday(DateOnly date);
            }
            public sealed class Calendar : ICalendar { public static bool IsHoliday(DateOnly date) => false; }
            public static class Use { public static int N => AppFunctions.All.Length + ICalendar.Contracts.Length + ICalendar.Implement<Calendar>().Length; }
            """);
        diags.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        Problems(output).Should().BeEmpty();
        var text = string.Join("\n", output.SyntaxTrees.Select(t => t.ToString()));
        text.Should().Contain("static partial class AppFunctions").And.Contain("ExprFunctionArgs.GetEnum<global::App.Tier>(a[0])")
            .And.Contain("TImpl.IsHoliday(").And.Contain("ExprFunction.Contract(\"isHoliday\"")
            .And.Contain("deterministic: false");
        text.Split('\n').Count(l => l.Contains("deterministic: false")).Should().Be(1);   // only "today"
    }

    [Theory]
    [InlineData("public static class F { [ExprFunction(Description = \"x\")] public static int A() => 1; }", "YFE0001")]
    [InlineData("public static partial class F { [ExprFunction(Description = \"x\")] public static int A(object o) => 1; }", "YFE0002")]
    [InlineData("public static partial class F { [ExprFunction(Description = \"x\")] public static void A() { } }", "YFE0003")]
    [InlineData("public static partial class F { [ExprFunction(\"a\", Description = \"x\")] public static int A() => 1; [ExprFunction(\"a\", Description = \"y\")] public static int B() => 2; }", "YFE0004")]
    [InlineData("public static partial class F { [ExprFunction] public static int A() => 1; }", "YFE0005")]
    public void Function_diagnostics(string declaration, string id)
    {
        var (_, diags) = Run(Usings + "[ExprFunctions]\n" + declaration);
        diags.Select(d => d.Id).Should().Contain(id);
    }
}
