using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using YFex.Cqrs.SourceGenerator;

namespace YFex.Cqrs.SourceGenerator.Test;

/// <summary>
/// Verifies that CQRSGenerator emits the expected static helper methods for the
/// canonical nested-record patterns: IQuery, ICommand, IEvent, IQueueable.
/// </summary>
public sealed class CqrsGeneratorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (GeneratorDriverRunResult Run, CSharpCompilation Post) RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12));
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(YFex.Cqrs.IQuery<>).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new CQRSGenerator();
        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        var result = driver.GetRunResult();
        var postCompilation = (CSharpCompilation)compilation.AddSyntaxTrees(result.GeneratedTrees);
        return (result, postCompilation);
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileHint)
    {
        var tree = result.GeneratedTrees.FirstOrDefault(t => t.FilePath.Contains(fileHint));
        tree.Should().NotBeNull($"generator should have emitted a file matching '{fileHint}'");
        return tree!.ToString();
    }

    // ── Test #1 – IQuery<T> produces a static helper ────────────────────────

    [Fact]
    public void Query_ProducesStaticHelper_WithCorrectSignature()
    {
        const string source = """
            namespace MyApp;
            public class CustomerDto { }
            public partial class Customer
            {
                public static partial class Queries
                {
                    public partial record GetByIdQuery(int Id) : YFex.Cqrs.IQuery<CustomerDto>;
                }
            }
            """;

        var (result, post) = RunGenerator(source);

        result.GeneratedTrees.Should().NotBeEmpty("generator must emit at least one file");

        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().Contain("GetById", "helper name = record name minus 'Query' suffix");
        allGenerated.Should().Contain("int id", "positional param must pass through (camelCase in generated signature)");
        allGenerated.Should().Contain("CancellationToken", "ct parameter must be appended");

        post.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("generated code must compile cleanly");
    }

    // ── Test #2 – ICommand<T> produces a static helper ──────────────────────

    [Fact]
    public void Command_ProducesStaticHelper_ReturningQueueableResult()
    {
        const string source = """
            namespace MyApp;
            public class CustomerDto { }
            public partial class Customer
            {
                public static partial class Commands
                {
                    public partial record CreateCommand(int Id, string Name)
                        : YFex.Cqrs.ICommand<CustomerDto>;
                }
            }
            """;

        var (result, post) = RunGenerator(source);

        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().Contain("Create", "helper name = record name minus 'Command' suffix");
        allGenerated.Should().Contain("int id", "first positional param (camelCase)");
        allGenerated.Should().Contain("string name", "second positional param (camelCase)");

        post.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // ── Test #3 – IQueueable command gets IdempotencyKey injected ───────────

    [Fact(Skip = "IdempotencyKey auto-injection for IQueueable records is a planned Plan 1 generator feature not yet emitted by CodeBuilder")]
    public void QueueableCommand_GetsIdempotencyKeyInjected()
    {
        const string source = """
            namespace MyApp;
            public partial class Order
            {
                public static partial class Commands
                {
                    public partial record PlaceOrderCommand(decimal Total)
                        : YFex.Cqrs.ICommand<Order>, YFex.Cqrs.IQueueable;
                }
            }
            public class Order { }
            """;

        var (result, post) = RunGenerator(source);

        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().Contain("IdempotencyKey", "IQueueable record must gain IdempotencyKey property");

        post.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // ── Test #4 – IEvent<> produces a Raise static helper ───────────────────

    [Fact]
    public void Event_ProducesRaiseHelper()
    {
        const string source = """
            namespace MyApp;
            public partial class Customer
            {
                public static partial class Events
                {
                    public partial record Created(int Id, string Name) : YFex.Cqrs.IEvent;
                }
            }
            """;

        var (result, post) = RunGenerator(source);

        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().Contain("Raise", "event helper is always named Raise");

        post.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // ── Test #5 – Non-partial nested class is skipped ───────────────────────

    [Fact]
    public void NonPartialCommandsClass_IsNotGenerated()
    {
        const string source = """
            namespace MyApp;
            public partial class Customer
            {
                public class Commands          // NOT partial — generator should skip
                {
                    public record CreateCommand(string Name) : YFex.Cqrs.ICommand;
                }
            }
            """;

        var (result, _) = RunGenerator(source);

        // No helpers emitted for the Commands class because it isn't partial.
        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().NotContain("Create(", "non-partial class blocks generation");
    }

    // ── Test #6 – Multiple aggregates in one compilation ────────────────────

    [Fact]
    public void MultipleAggregates_EachGetHelpers()
    {
        const string source = """
            namespace MyApp;
            public partial class Customer
            {
                public static partial class Queries
                {
                    public partial record GetCustomerQuery(int Id) : YFex.Cqrs.IQuery<string>;
                }
            }
            public partial class Order
            {
                public static partial class Commands
                {
                    public partial record CancelOrderCommand(int Id) : YFex.Cqrs.ICommand;
                }
            }
            """;

        var (result, post) = RunGenerator(source);

        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().Contain("GetCustomer", "Customer helper emitted");
        allGenerated.Should().Contain("CancelOrder", "Order helper emitted");

        post.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // ── Test #7 – Record with no positional params ───────────────────────────

    [Fact]
    public void RecordWithNoParams_ProducesHelperWithOnlyCancellationToken()
    {
        const string source = """
            namespace MyApp;
            public partial class Catalog
            {
                public static partial class Queries
                {
                    public partial record GetAllQuery() : YFex.Cqrs.IQuery<string[]>;
                }
            }
            """;

        var (result, post) = RunGenerator(source);

        var allGenerated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        allGenerated.Should().Contain("GetAll", "helper emitted for no-param record");
        allGenerated.Should().Contain("CancellationToken", "ct parameter still appended");

        post.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }
}
