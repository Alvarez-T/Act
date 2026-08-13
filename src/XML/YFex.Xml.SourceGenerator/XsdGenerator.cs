using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace YFex.Xml.SourceGenerator;

/// <summary>
/// Incremental generator: turns <c>.xsd</c> <see cref="AdditionalText"/> files into
/// internal POCOs and validating faceted structs. ALL fed files are merged into one
/// symbol table, so xs:include/xs:import resolve when the referenced files are also
/// supplied as AdditionalFiles.
///
/// MSBuild knobs (CompilerVisibleProperty):
/// <list type="bullet">
///   <item><c>YFexXmlGeneratedNamespace</c> — default <c>YFex.Xml.Generated</c>.</item>
///   <item><c>YFexXmlEmitFacetStructs</c> — <c>true</c>/<c>false</c>, default <c>true</c>.</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class XsdGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var xsdFiles = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".xsd", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => f.GetText(ct)?.ToString() ?? "")
            .Where(static text => !string.IsNullOrWhiteSpace(text));

        var options = context.AnalyzerConfigOptionsProvider.Select(static (p, _) =>
        {
            p.GlobalOptions.TryGetValue("build_property.YFexXmlGeneratedNamespace", out var ns);
            p.GlobalOptions.TryGetValue("build_property.YFexXmlEmitFacetStructs", out var emit);
            return (
                Namespace: string.IsNullOrWhiteSpace(ns) ? "YFex.Xml.Generated" : ns!,
                EmitStructs: !string.Equals(emit, "false", System.StringComparison.OrdinalIgnoreCase));
        });

        var combined = xsdFiles.Collect().Combine(options);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var (files, opts) = pair;
            if (files.IsDefaultOrEmpty) return;

            SchemaSet set;
            try
            {
                set = new SchemaSet();
                foreach (var text in files) XsdParser.ParseInto(text, set);
                XsdParser.Resolve(set);
            }
            catch { return; } // never break the build on a malformed schema

            var emitter = new Emitter(set, opts.Namespace, opts.EmitStructs);
            foreach (var (hint, source) in emitter.Emit())
                spc.AddSource($"{Emitter.Ident(hint)}.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }
}
