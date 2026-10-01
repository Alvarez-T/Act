using Microsoft.CodeAnalysis;

namespace YFex.Validation.Generator.Tests;

/// <summary>YFV diagnostics must be in-source locations so <c>#pragma warning disable</c> / editorconfig suppress them.</summary>
public class PragmaSuppressionTests
{
    private const string Prelude = """
        using System; using YFex; using YFex.Validation;
        namespace App;
        public sealed class Cfg<T> { public Cfg<T> Validation(Action<ValidationBuilder<T>> rules) => this; }
        public sealed record Loose(string Name);
        public sealed class Clash { [Required] public string? Rules { get; set; } }
        """;

    [Fact]
    public void Generator_diagnostics_are_located_in_the_source_tree()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public static class C { public static void X() => new Cfg<Loose>().Validation(v => v.Field(c => c.Name).Required()); }
            """);

        var yfv = run.GeneratorDiagnostics.Where(d => d.Id.StartsWith("YFV", StringComparison.Ordinal)).ToList();
        yfv.Should().Contain(d => d.Id == "YFV0003").And.Contain(d => d.Id == "YFV0001");
        yfv.Should().OnlyContain(d => d.Location.IsInSource, "external-file locations ignore #pragma");
    }

    [Fact]
    public void Locations_point_into_the_compilation_trees()
    {
        var run = GeneratorHarness.Run(Prelude + """
            public static class C { public static void X() => new Cfg<Loose>().Validation(v => v.Field(c => c.Name).Required()); }
            """);

        var d = run.GeneratorDiagnostics.Single(x => x.Id == "YFV0003");
        run.Output.SyntaxTrees.Should().Contain(d.Location.SourceTree!, "the compiler applies #pragma / editorconfig per tree");
        d.Location.GetLineSpan().StartLinePosition.Line.Should().Be(4, "the Validation call is on the line after the prelude");
    }
}
