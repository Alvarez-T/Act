using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace YFex.Validation.Generator.Tests;

internal sealed record GeneratorRun(
    GeneratorDriver Driver,
    Compilation Output,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompileDiagnostics,
    IReadOnlyDictionary<string, string> Sources)
{
    public string Source(string hintContains) => Sources.First(kv => kv.Key.Contains(hintContains, StringComparison.Ordinal)).Value;

    public IEnumerable<string> Ids(DiagnosticSeverity atLeast = DiagnosticSeverity.Hidden)
        => GeneratorDiagnostics.Where(d => d.Severity >= atLeast).Select(d => d.Id);

    /// <summary>Compiler errors/warnings of the final compilation (generated code must be clean).</summary>
    public IEnumerable<Diagnostic> CompileProblems
        => CompileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning);
}

internal static class GeneratorHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> s_references = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(ViolationWriter).Assembly.Location);   // YFex.Validation
        paths.Add(typeof(YFex.Money).Assembly.Location);        // YFex core
        paths.Add(typeof(YFex.State.StateObject).Assembly.Location); // YFex.State (view-model tests)
        return paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    });

    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static GeneratorRun Run(string source, IEnumerable<(string Path, string Text)>? additionalFiles = null,
        IDictionary<string, string>? buildProperties = null, bool trackSteps = false)
    {
        var compilation = CreateCompilation(source);
        var driver = CreateDriver(additionalFiles, buildProperties, trackSteps);
        return Execute(driver, compilation);
    }

    /// <summary>
    /// View models: runs the YFex.State generator next to the validation generator — the State generator declares
    /// the setter hooks this generator implements (D22), so the output compiles only with both.
    /// </summary>
    public static GeneratorRun RunWithState(string source)
    {
        var compilation = CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ValidationGenerator().AsSourceGenerator(), new YFex.State.Generator.StateGenerator().AsSourceGenerator()],
            additionalTexts: [],
            parseOptions: ParseOptions,
            optionsProvider: new OptionsProvider(new Dictionary<string, string>()));
        return Execute(driver, compilation);
    }

    public static CSharpCompilation CreateCompilation(params string[] sources)
        => CSharpCompilation.Create(
            "GenTests",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions, path: "Input" + Array.IndexOf(sources, s) + ".cs")),
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver CreateDriver(IEnumerable<(string Path, string Text)>? additionalFiles = null,
        IDictionary<string, string>? buildProperties = null, bool trackSteps = false)
    {
        var texts = (additionalFiles ?? []).Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text)).ToImmutableArray();
        return CSharpGeneratorDriver.Create(
            generators: [new ValidationGenerator().AsSourceGenerator()],
            additionalTexts: texts,
            parseOptions: ParseOptions,
            optionsProvider: new OptionsProvider(buildProperties ?? new Dictionary<string, string>()),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: trackSteps));
    }

    public static GeneratorRun Execute(GeneratorDriver driver, Compilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var result = driver.GetRunResult();
        var sources = result.Results.SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());
        return new GeneratorRun(driver, output, result.Diagnostics, output.GetDiagnostics(), sources);
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class OptionsProvider(IDictionary<string, string> global) : AnalyzerConfigOptionsProvider
    {
        private readonly Options _global = new(global.ToDictionary(kv => "build_property." + kv.Key, kv => kv.Value));

        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new([]);
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }
}
