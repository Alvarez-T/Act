using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using YFex.Cqrs.SourceGenerator;

// ── 1. Defina o código de entrada ────────────────────────────────────────────
const string source = """
    namespace MyApp.Models;

    public partial class UserDto
    {
        public static partial class Queries
        {
            public record GetUserByIdQuery(int Id) : YFex.Cqrs.IQuery<UserDto>;
            public record GetUsersByAgeQuery(int MinAge, int MaxAge) : YFex.Cqrs.IQuery<System.Collections.Generic.List<UserDto>>;
        }

        public class Commands
        {
            public record CreateUserCommand(string Name, string Email) : YFex.Cqrs.ICommand;
            public record DeleteUserCommand(int Id) : YFex.Cqrs.ICommand;
        }

        public partial class Events
        {
            public record UserCreatedEvent(int Id, string Name) : YFex.Cqrs.IEvent;
        }
    }
    """;

// ── 2. Crie a compilação em memória ──────────────────────────────────────────
var syntaxTree = CSharpSyntaxTree.ParseText(source);

var references = new[]
{
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
    MetadataReference.CreateFromFile(
        System.Reflection.Assembly.Load("System.Runtime").Location),
};

var compilation = CSharpCompilation.Create(
    assemblyName: "ManualTest",
    syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
    references: new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
        MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(typeof(YFex.Cqrs.IQuery<>).Assembly.Location), // ← this
    },
    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

// ── 3. Execute o gerador ──────────────────────────────────────────────────────
var generator = new CQRSGenerator();
var driver = CSharpGeneratorDriver.Create(generator);
var result = driver.RunGenerators(compilation).GetRunResult();

// ── 4. Inspecione o resultado ─────────────────────────────────────────────────
Console.WriteLine($"Arquivos gerados: {result.GeneratedTrees.Length}");
Console.WriteLine(new string('─', 60));

foreach (var tree in result.GeneratedTrees)
{
    Console.WriteLine($"📄 {tree.FilePath}");
    Console.WriteLine(tree.ToString());
    Console.WriteLine(new string('─', 60));
}

// Erros de diagnóstico
foreach (var diag in result.Diagnostics)
    Console.WriteLine($"[{diag.Severity}] {diag.GetMessage()}");

// Erros na compilação original
foreach (var diag in compilation.GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error))
    Console.WriteLine($"[COMPILATION ERROR] {diag.GetMessage()}");