using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace YFex.Validation.Generator;

/// <summary>
/// Module message catalogs (10 §3). Every <c>messages.json</c> / <c>messages.&lt;culture&gt;.json</c> passed as
/// AdditionalFiles becomes one entry set of a generated <c>&lt;Module&gt;Messages : IMessageSource</c> with a
/// static switch on (culture, code) — no ResourceManager, no reflection.
/// <code>
/// { "sale.discount.cap": "Desconto máximo é {0}", "fields": { "SaleVm.ManualDiscount": "Desconto manual" } }
/// </code>
/// Class = last segment of RootNamespace (or assembly name) + "Messages", in that namespace; override the
/// full name with MSBuild <c>YFexMessagesClass</c> (needs CompilerVisibleProperty).
/// Self-contained on purpose (can move to a core generator later).
/// </summary>
internal static class MessagesGenerator
{
    public static readonly DiagnosticDescriptor InvalidJson = new(
        "YFV0041", "Invalid messages file",
        "Messages file '{0}' is not valid: {1}",
        "YFex.Validation", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal sealed record CatalogFile(string Path, string Culture, EquatableArray<Entry> Templates, EquatableArray<Entry> Fields, string? Error);

    internal sealed record Entry(string Key, string Value);

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static t => IsMessagesFile(t.Path))
            .Select(static (t, ct) => Parse(t.Path, t.GetText(ct)));

        var className = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) => ClassName(pair.Left.GlobalOptions, pair.Right.AssemblyName));

        context.RegisterSourceOutput(files.Collect().Combine(className), static (spc, pair) =>
        {
            var (list, name) = pair;
            foreach (var f in list)
                if (f.Error is not null)
                    spc.ReportDiagnostic(Diagnostic.Create(InvalidJson, Location.None, f.Path, f.Error));
            var valid = list.Where(f => f.Error is null).ToList();
            if (valid.Count > 0) spc.AddSource(name.Replace('.', '_') + ".Messages.g.cs", Render(name, valid));
        });
    }

    internal static bool IsMessagesFile(string path)
    {
        string file = Path.GetFileName(path);
        return file.StartsWith("messages", StringComparison.OrdinalIgnoreCase)
               && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               && (file.Length == "messages.json".Length || file[8] == '.');
    }

    internal static string CultureOf(string path)
    {
        // messages.json → ""  |  messages.pt-BR.json → "pt-BR"
        string file = Path.GetFileNameWithoutExtension(path);
        int dot = file.IndexOf('.');
        return dot < 0 ? "" : file.Substring(dot + 1);
    }

    private static string ClassName(Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions options, string? assemblyName)
    {
        if (options.TryGetValue("build_property.YFexMessagesClass", out var explicitName) && !string.IsNullOrWhiteSpace(explicitName))
            return explicitName.Trim();
        string ns = options.TryGetValue("build_property.RootNamespace", out var root) && !string.IsNullOrWhiteSpace(root)
            ? root.Trim()
            : assemblyName ?? "Module";
        string last = ns.Substring(ns.LastIndexOf('.') + 1);
        var id = new StringBuilder();
        foreach (char c in last) id.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (id.Length == 0 || char.IsDigit(id[0])) id.Insert(0, '_');
        return ns + "." + id + "Messages";
    }

    internal static CatalogFile Parse(string path, SourceText? text)
    {
        string culture = CultureOf(path);
        if (text is null) return new CatalogFile(path, culture, EquatableArray<Entry>.Empty, EquatableArray<Entry>.Empty, "file could not be read");
        try
        {
            var templates = new List<Entry>();
            var fields = new List<Entry>();
            var reader = new MiniJson(text.ToString());
            reader.ReadObject((key, r) =>
            {
                if (key == "fields")
                    r.ReadObject((fk, fr) => fields.Add(new Entry(fk, fr.ReadString())));
                else
                    templates.Add(new Entry(key, r.ReadString()));
            });
            reader.EndOfInput();
            return new CatalogFile(path, culture,
                new EquatableArray<Entry>(templates.ToArray()), new EquatableArray<Entry>(fields.ToArray()), null);
        }
        catch (FormatException ex)
        {
            return new CatalogFile(path, culture, EquatableArray<Entry>.Empty, EquatableArray<Entry>.Empty, ex.Message);
        }
    }

    internal static string Render(string fullName, IReadOnlyList<CatalogFile> files)
    {
        int lastDot = fullName.LastIndexOf('.');
        string ns = lastDot < 0 ? "" : fullName.Substring(0, lastDot);
        string cls = fullName.Substring(lastDot + 1);

        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n#nullable enable\n\n");
        if (ns.Length > 0) sb.Append("namespace ").Append(ns).Append("\n{\n");
        sb.Append("    /// <summary>Messages of this module (generated from messages*.json).</summary>\n");
        sb.Append("    [global::System.CodeDom.Compiler.GeneratedCode(\"YFex.Validation.Generator\", \"1.0.0\")]\n");
        sb.Append("    public sealed class ").Append(cls).Append(" : global::YFex.Localization.IMessageSource\n    {\n");
        sb.Append("        public static readonly ").Append(cls).Append(" Instance = new();\n\n");
        RenderLookup(sb, "TryGetTemplate", "code", "template", files, f => f.Templates);
        sb.Append('\n');
        RenderLookup(sb, "TryGetFieldName", "fieldKey", "name", files, f => f.Fields);
        sb.Append("    }\n");
        if (ns.Length > 0) sb.Append("}\n");
        return sb.ToString();
    }

    private static void RenderLookup(StringBuilder sb, string method, string keyName, string outName,
        IReadOnlyList<CatalogFile> files, Func<CatalogFile, EquatableArray<Entry>> select)
    {
        sb.Append("        public bool ").Append(method).Append("(string ").Append(keyName)
          .Append(", string cultureName, out string ").Append(outName).Append(")\n        {\n");

        // Merge files per culture (later files of the same culture add/override keys).
        var byCulture = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (!byCulture.TryGetValue(f.Culture, out var map)) byCulture[f.Culture] = map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in select(f)) map[e.Key] = e.Value;
        }

        sb.Append("            switch (cultureName)\n            {\n");
        foreach (var kv in byCulture)
        {
            if (kv.Value.Count == 0) continue;
            sb.Append("                case ").Append(Parser.Literal(kv.Key)).Append(":\n");
            sb.Append("                    switch (").Append(keyName).Append(")\n                    {\n");
            foreach (var e in kv.Value)
                sb.Append("                        case ").Append(Parser.Literal(e.Key)).Append(": ").Append(outName)
                  .Append(" = ").Append(Parser.Literal(e.Value)).Append("; return true;\n");
            sb.Append("                    }\n                    break;\n");
        }
        sb.Append("            }\n            ").Append(outName).Append(" = \"\";\n            return false;\n        }\n");
    }

    /// <summary>Minimal JSON reader: objects and strings (+ skips numbers/bools/null/arrays for forward compat).</summary>
    internal sealed class MiniJson
    {
        private readonly string _s;
        private int _i;

        public MiniJson(string s) => _s = s;

        public void ReadObject(Action<string, MiniJson> onProperty)
        {
            SkipWs();
            Expect('{');
            SkipWs();
            if (Peek() == '}') { _i++; return; }
            while (true)
            {
                SkipWs();
                string key = ReadString();
                SkipWs();
                Expect(':');
                SkipWs();
                if (Peek() is '"' or '{') onProperty(key, this);
                else SkipValue();
                SkipWs();
                char c = Next();
                if (c == '}') return;
                if (c != ',') throw Error("expected ',' or '}'");
            }
        }

        public string ReadString()
        {
            SkipWs();
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                char c = Next();
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = Next();
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                        sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                        _i += 4;
                        break;
                    default: throw Error("bad escape");
                }
            }
        }

        public void EndOfInput()
        {
            SkipWs();
            if (_i != _s.Length) throw Error("unexpected content after the root object");
        }

        private void SkipValue()
        {
            char c = Peek();
            if (c == '[')
            {
                _i++;
                int depth = 1;
                while (depth > 0)
                {
                    char n = Next();
                    if (n == '"') { _i--; ReadString(); }
                    else if (n == '[') depth++;
                    else if (n == ']') depth--;
                }
                return;
            }
            while (_i < _s.Length && _s[_i] is not (',' or '}' or ']') && !char.IsWhiteSpace(_s[_i])) _i++;
        }

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                if (char.IsWhiteSpace(_s[_i])) { _i++; continue; }
                if (_s[_i] == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')   // tolerate // comments
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    continue;
                }
                break;
            }
            if (_i == 0 && _s.Length > 0 && _s[0] == '﻿') { _i++; SkipWs(); }
        }

        private char Peek() => _i < _s.Length ? _s[_i] : throw Error("unexpected end");
        private char Next() => _i < _s.Length ? _s[_i++] : throw Error("unexpected end");

        private void Expect(char c)
        {
            if (Next() != c) throw Error($"expected '{c}'");
        }

        private FormatException Error(string message) => new($"{message} at position {_i}");
    }
}
