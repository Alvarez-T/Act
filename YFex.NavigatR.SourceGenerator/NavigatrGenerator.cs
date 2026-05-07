using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace YFex.NavigatR.SourceGenerator;

[Generator]
public sealed class NavigatRGenerator : IIncrementalGenerator
{
    // -----------------------------------------------------------------------
    // Diagnostics
    // -----------------------------------------------------------------------

    private static readonly DiagnosticDescriptor s_nav001 = new(
        "NAV001", "[Route] requires partial",
        "[Route] requires '{0}' to be declared as partial.",
        "YFex.NavigatR", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_nav002 = new(
        "NAV002", "Must implement INavigable",
        "'{0}' must implement INavigable to use [Route].",
        "YFex.NavigatR", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_nav003 = new(
        "NAV003", "RouteType must implement IRoute",
        "The type '{0}' passed to [Route(typeof(...))] must implement IRoute.",
        "YFex.NavigatR", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_nav004 = new(
        "NAV004", "Route name conflict",
        "Route name '{0}' would generate '{1}Route' which conflicts with an existing type.",
        "YFex.NavigatR", DiagnosticSeverity.Warning, true);

    // -----------------------------------------------------------------------
    // Initialize
    // -----------------------------------------------------------------------

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var pipeline = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "YFex.NavigatR.RouteAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value);

        // 1. Diagnostics + generated Route class
        context.RegisterSourceOutput(pipeline,
            static (spc, model) => EmitDiagnosticsAndRoute(spc, model));

        // 2. ViewModel partial:
        //    - When Parameter declared: explicit INavigable.OnNavigation bridge
        //      + file-scoped GetParameter() extension + enforced partial OnNavigation(TParam, ct)
        //    - When INavigable<TResult>: Returns(), Cancel(), Deny(), WaitForResultAsync()
        context.RegisterSourceOutput(pipeline,
            static (spc, model) => EmitViewModelPartial(spc, model));

        // 3. NavigatRRegistration.g.cs
        var registrationPipeline = pipeline
            .Collect()
            .Select(static (models, _) =>
            {
                var entries = new List<RegistrationEntry>();
                foreach (var m in models)
                {
                    if (!m.IsPartial || !m.ImplementsINavigable) continue;
                    if (m.Mode == RouteMode.Manual && !m.ManualRouteTypeIsValid) continue;

                    string routeFull = m.Mode == RouteMode.AutoGenerate
                        ? (string.IsNullOrEmpty(m.Namespace)
                            ? m.GeneratedRouteClassName!
                            : m.Namespace + "." + m.GeneratedRouteClassName!)
                        : m.ManualRouteTypeName!;

                    string vmFull = string.IsNullOrEmpty(m.Namespace)
                        ? m.ViewModelName
                        : m.Namespace + "." + m.ViewModelName;

                    entries.Add(new RegistrationEntry(routeFull, vmFull));
                }
                return new RegistrationModel(new EquatableArray<RegistrationEntry>(entries));
            });

        context.RegisterSourceOutput(registrationPipeline,
            static (spc, model) => EmitRegistration(spc, model));
    }

    // -----------------------------------------------------------------------
    // Transform
    // -----------------------------------------------------------------------

    private static RouteViewModel? ExtractModel(
        GeneratorAttributeSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetNode is not ClassDeclarationSyntax classDecl) return null;
        if (ctx.TargetSymbol is not INamedTypeSymbol symbol) return null;

        string ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } nsSymbol
            ? nsSymbol.ToDisplayString()
            : string.Empty;

        bool isPartial = false;
        foreach (var mod in classDecl.Modifiers)
            if (mod.IsKind(SyntaxKind.PartialKeyword)) { isPartial = true; break; }

        bool implementsINavigable = HasInterface(symbol, "YFex.NavigatR", "INavigable");

        // TResult from INavigable<TResult>
        string? resultTypeName = null;
        bool producesResult = false;
        foreach (var iface in symbol.AllInterfaces)
        {
            if (!iface.IsGenericType) continue;
            if (GetBaseInterfaceName(iface) != "YFex.NavigatR.INavigable") continue;
            if (iface.TypeParameters.Length != 1) continue;
            resultTypeName = iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            producesResult = true;
            break;
        }

        // Parse [Route] attribute
        var attr = ctx.Attributes[0];
        if (attr.ConstructorArguments.Length == 0) return null;

        string? routeName = null;
        string? manualRouteTypeName = null;
        bool manualRouteTypeIsValid = true;
        string? displayName = null;
        string? paramsTypeName = null;
        RouteMode mode;

        var arg = attr.ConstructorArguments[0];
        if (arg.Kind == TypedConstantKind.Primitive && arg.Value is string nameStr)
        {
            routeName = nameStr;
            mode = RouteMode.AutoGenerate;
        }
        else if (arg.Kind == TypedConstantKind.Type && arg.Value is INamedTypeSymbol routeTypeSymbol)
        {
            manualRouteTypeName = routeTypeSymbol.ToDisplayString(
                new SymbolDisplayFormat(
                    globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces));
            manualRouteTypeIsValid = HasInterface(routeTypeSymbol, "YFex.NavigatR", "IRoute")
                || IsInterface(routeTypeSymbol, "YFex.NavigatR", "IRoute");
            mode = RouteMode.Manual;
        }
        else return null;

        // Named args: DisplayName, Parameter, ParameterRequired
        bool parameterRequired = true; // default required
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "DisplayName" && named.Value.Value is string dn)
                displayName = dn;

            if (named.Key == "Parameter" && named.Value.Value is INamedTypeSymbol paramTypeSymbol)
                paramsTypeName = paramTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (named.Key == "ParameterRequired" && named.Value.Value is bool req)
                parameterRequired = req;
        }

        string? generatedRouteClassName = mode == RouteMode.AutoGenerate && routeName is not null
            ? DeriveRouteClassName(routeName)
            : null;

        return new RouteViewModel(
            Namespace: ns,
            ViewModelName: symbol.Name,
            IsPartial: isPartial,
            ImplementsINavigable: implementsINavigable,
            Mode: mode,
            RouteName: routeName,
            GeneratedRouteClassName: generatedRouteClassName,
            ManualRouteTypeName: manualRouteTypeName,
            ManualRouteTypeIsValid: manualRouteTypeIsValid,
            ParamsTypeName: paramsTypeName,
            ParameterRequired: parameterRequired,
            ResultTypeName: resultTypeName,
            ProducesResult: producesResult,
            DisplayName: displayName);
    }

    // -----------------------------------------------------------------------
    // Emit — Route class + diagnostics
    // -----------------------------------------------------------------------

    private static void EmitDiagnosticsAndRoute(SourceProductionContext spc, RouteViewModel model)
    {
        if (!model.IsPartial)
        {
            spc.ReportDiagnostic(Diagnostic.Create(s_nav001, Location.None, model.ViewModelName));
            return;
        }
        if (!model.ImplementsINavigable)
        {
            spc.ReportDiagnostic(Diagnostic.Create(s_nav002, Location.None, model.ViewModelName));
            return;
        }
        if (model.Mode == RouteMode.Manual && !model.ManualRouteTypeIsValid)
        {
            spc.ReportDiagnostic(Diagnostic.Create(s_nav003, Location.None, model.ManualRouteTypeName ?? "?"));
            return;
        }

        if (model.Mode != RouteMode.AutoGenerate || model.GeneratedRouteClassName is null) return;

        // Route interface mirrors ViewModel's contracts
        // Required parameter → IRouteAccepts<T> enforces parameter at NavigateTo call site
        // Optional parameter → IRoute (no constraint, NavigateTo without parameter is valid)
        string interfaceClause;
        if (model.ParamsTypeName is not null && model.ResultTypeName is not null)
            interfaceClause = model.ParameterRequired
                ? $"global::YFex.NavigatR.IRoute<{model.ParamsTypeName}, {model.ResultTypeName}>"
                : $"global::YFex.NavigatR.IRouteProduces<{model.ResultTypeName}>";
        else if (model.ParamsTypeName is not null)
            interfaceClause = model.ParameterRequired
                ? $"global::YFex.NavigatR.IRouteAccepts<{model.ParamsTypeName}>"
                : "global::YFex.NavigatR.IRoute";
        else if (model.ResultTypeName is not null)
            interfaceClause = $"global::YFex.NavigatR.IRouteProduces<{model.ResultTypeName}>";
        else
            interfaceClause = "global::YFex.NavigatR.IRoute";

        string displayNameLiteral = model.DisplayName is not null
            ? "\"" + model.DisplayName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : "null";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.Append("namespace "); sb.Append(model.Namespace); sb.AppendLine(";");
            sb.AppendLine();
        }

        sb.Append("public sealed record "); sb.Append(model.GeneratedRouteClassName);
        sb.Append(" : "); sb.AppendLine(interfaceClause);
        sb.AppendLine("{");
        sb.Append("    public string? DisplayName => "); sb.Append(displayNameLiteral); sb.AppendLine(";");
        sb.AppendLine("}");

        string hintName = string.IsNullOrEmpty(model.Namespace)
            ? $"{model.GeneratedRouteClassName}.g.cs"
            : $"{model.Namespace}.{model.GeneratedRouteClassName}.g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

    // -----------------------------------------------------------------------
    // Emit — ViewModel partial
    //
    // Generates up to three concerns in a single file:
    //
    // A) Parameter declared ([Route(Parameter = typeof(T))]):
    //    - Explicit INavigable.OnNavigation bridge (invisible in IntelliSense)
    //    - file-scoped GetParameter() extension (visible only in this file)
    //    - public partial Task OnNavigation(TParam, ct) declaration (enforces implementation)
    //
    // B) INavigable<TResult> implemented:
    //    - Private TCS
    //    - Returns(TResult) — auto-wraps to Success
    //    - Cancel() — produces Cancelled
    //    - Deny(string?) — produces Denied
    //    - Explicit INavigable<TResult>.WaitForResultAsync() (invisible in IntelliSense)
    // -----------------------------------------------------------------------

    private static void EmitViewModelPartial(SourceProductionContext spc, RouteViewModel model)
    {
        if (!model.IsPartial || !model.ImplementsINavigable) return;

        bool hasParam = model.ParamsTypeName is not null;
        bool hasResult = model.ProducesResult && model.ResultTypeName is not null;

        if (!hasParam && !hasResult) return;

        var p = model.ParamsTypeName;
        var r = model.ResultTypeName;
        var resultUnion = hasResult ? $"global::YFex.NavigatR.NavigationResult<{r}>" : null;
        var tcsType = hasResult
            ? $"global::System.Threading.Tasks.TaskCompletionSource<{resultUnion}>"
            : null;
        var taskResult = hasResult
            ? $"global::System.Threading.Tasks.Task<{resultUnion}>"
            : null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.Append("namespace "); sb.Append(model.Namespace); sb.AppendLine(";");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {model.ViewModelName}");
        sb.AppendLine("{");

        // ── A) Parameter wiring ──────────────────────────────────────────────

        if (hasParam)
        {
            // Parameter type — nullable when optional
            string paramType = model.ParameterRequired ? p! : $"{p}?";

            // Explicit interface implementation — invisible in IntelliSense
            // When required: calls GetParameter<T>() which throws if missing
            // When optional: calls TryGetParameter<T>() and passes null if missing
            sb.AppendLine("    // Parameter bridge — not visible in IntelliSense");
            sb.AppendLine($"    global::System.Threading.Tasks.Task global::YFex.NavigatR.INavigable.OnNavigation(");
            sb.AppendLine($"        global::YFex.NavigatR.NavigationContext context,");
            sb.AppendLine($"        global::System.Threading.CancellationToken ct)");

            if (model.ParameterRequired)
            {
                sb.AppendLine($"        => OnNavigation(context.GetParameter<{p}>(), ct);");
            }
            else
            {
                sb.AppendLine($"    {{");
                sb.AppendLine($"        context.TryGetParameter<{p}>(out var __param);");
                sb.AppendLine($"        return OnNavigation(__param, ct);");
                sb.AppendLine($"    }}");
            }

            sb.AppendLine();

            // Enforced partial — compiler error if developer does not implement this
            sb.AppendLine("    /// <summary>Called when the screen is navigated to with the typed parameter.</summary>");
            sb.AppendLine($"    public partial global::System.Threading.Tasks.Task OnNavigation(");
            sb.AppendLine($"        {paramType} parameter,");
            sb.AppendLine($"        global::System.Threading.CancellationToken ct = default);");
            sb.AppendLine();
        }

        // ── B) Result wiring ─────────────────────────────────────────────────

        if (hasResult)
        {
            // Private TCS — invisible to developer
            sb.AppendLine($"    private readonly {tcsType} _navigationResultTcs");
            sb.AppendLine($"        = new {tcsType}(");
            sb.AppendLine($"            global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            sb.AppendLine();

            // Returns(TResult) — auto-wraps into Success
            sb.AppendLine($"    /// <summary>Signal completion with a result. Closes this screen and resumes the caller.</summary>");
            sb.AppendLine($"    public void Returns({r} result)");
            sb.AppendLine($"        => _navigationResultTcs.TrySetResult(new {resultUnion}.Success(result));");
            sb.AppendLine();

            // Cancel()
            sb.AppendLine($"    /// <summary>Signal that the user cancelled. Closes this screen.</summary>");
            sb.AppendLine($"    public void Cancel()");
            sb.AppendLine($"        => _navigationResultTcs.TrySetResult(new {resultUnion}.Cancelled());");
            sb.AppendLine();

            // Deny(string?)
            sb.AppendLine($"    /// <summary>Signal denial with an optional reason. Closes this screen.</summary>");
            sb.AppendLine($"    public void Deny(string? reason = null)");
            sb.AppendLine($"        => _navigationResultTcs.TrySetResult(new {resultUnion}.Denied(reason));");
            sb.AppendLine();

            // WaitForResultAsync() — explicit interface impl, invisible in IntelliSense
            sb.AppendLine($"    // Navigator-internal — not visible in IntelliSense");
            sb.AppendLine($"    {taskResult}");
            sb.AppendLine($"        global::YFex.NavigatR.INavigable<{r}>.WaitForResultAsync()");
            sb.AppendLine($"        => _navigationResultTcs.Task;");
            sb.AppendLine();
        }

        sb.AppendLine("}");

        // ── file-scoped GetParameter() extension ─────────────────────────────
        // 'file' keyword makes this visible ONLY within this generated file.
        // The developer's partial implementation is in a separate file and cannot see this,
        // but since the bridge calls context.GetParameter<T>() directly, the extension
        // is available where it is used (inside this generated file only).

        if (hasParam)
        {
            string paramType = model.ParameterRequired ? p! : $"{p}?";
            sb.AppendLine();
            sb.AppendLine("// File-scoped — only visible within this generated file");
            sb.AppendLine($"file static class NavigationContextExtensions_{model.ViewModelName}");
            sb.AppendLine("{");
            if (model.ParameterRequired)
            {
                sb.AppendLine($"    internal static {p} GetParameter(");
                sb.AppendLine($"        this global::YFex.NavigatR.NavigationContext context)");
                sb.AppendLine($"        => context.GetParameter<{p}>();");
            }
            else
            {
                sb.AppendLine($"    internal static {paramType} GetParameter(");
                sb.AppendLine($"        this global::YFex.NavigatR.NavigationContext context)");
                sb.AppendLine($"    {{");
                sb.AppendLine($"        context.TryGetParameter<{p}>(out var value);");
                sb.AppendLine($"        return value;");
                sb.AppendLine($"    }}");
            }
            sb.AppendLine("}");
        }

        string hintName = string.IsNullOrEmpty(model.Namespace)
            ? $"{model.ViewModelName}.NavigatR.g.cs"
            : $"{model.Namespace}.{model.ViewModelName}.NavigatR.g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

    // -----------------------------------------------------------------------
    // Emit — NavigatRRegistration.g.cs
    // -----------------------------------------------------------------------

    private static void EmitRegistration(SourceProductionContext spc, RegistrationModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace YFex.NavigatR;");
        sb.AppendLine();
        sb.AppendLine("public static partial class NavigatRRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    static partial void RegisterGenerated(global::YFex.NavigatR.RouteRegistry registry)");
        sb.AppendLine("    {");

        foreach (var entry in model.Entries)
        {
            sb.Append("        registry.Register<global::");
            sb.Append(entry.RouteFullName);
            sb.Append(", global::");
            sb.Append(entry.ViewModelFullName);
            sb.AppendLine(">();");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("NavigatRRegistration.g.cs", sb.ToString());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool HasInterface(INamedTypeSymbol symbol, string ns, string name)
    {
        foreach (var iface in symbol.AllInterfaces)
            if (iface.ContainingNamespace?.ToDisplayString() == ns && iface.Name == name)
                return true;
        return false;
    }

    private static bool IsInterface(INamedTypeSymbol symbol, string ns, string name)
        => symbol.ContainingNamespace?.ToDisplayString() == ns && symbol.Name == name;

    private static string GetBaseInterfaceName(INamedTypeSymbol iface)
    {
        var ns = iface.ContainingNamespace;
        string prefix = ns is null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString() + ".";
        return prefix + iface.Name;
    }

    internal static string DeriveRouteClassName(string routeName)
    {
        if (string.IsNullOrEmpty(routeName)) return "Route";
        var sb = new StringBuilder();
        bool cap = true;
        foreach (char ch in routeName)
        {
            if (ch == '-' || ch == '_') { cap = true; continue; }
            sb.Append(cap ? char.ToUpperInvariant(ch) : ch);
            cap = false;
        }
        sb.Append("Route");
        return sb.ToString();
    }
}