using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;
using YFex.NavigatR.SourceGenerator;

namespace YFex.NavigatR.Generator;

[Generator]
public sealed class NavigatRGenerator : IIncrementalGenerator
{
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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var pipeline = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "YFex.NavigatR.RouteAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value);

        context.RegisterSourceOutput(pipeline,
            static (spc, model) => EmitDiagnosticsAndRoute(spc, model));

        context.RegisterSourceOutput(pipeline,
            static (spc, model) => EmitViewModelPartial(spc, model));

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

    private static RouteViewModel? ExtractModel(
        GeneratorAttributeSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.TargetNode is not ClassDeclarationSyntax classDecl) return null;
        if (ctx.TargetSymbol is not INamedTypeSymbol symbol) return null;

        string ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } nsSymbol
            ? nsSymbol.ToDisplayString() : string.Empty;

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

        var attr = ctx.Attributes[0];
        if (attr.ConstructorArguments.Length == 0) return null;

        string? routeName = null;
        string? manualRouteTypeName = null;
        bool manualRouteTypeIsValid = true;
        string? displayName = null;
        string? paramsTypeName = null;
        bool parameterRequired = true;
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

            // For manual routes — try to read parameter type from constructor
            foreach (var ctor in routeTypeSymbol.Constructors)
            {
                if (ctor.Parameters.Length == 1)
                {
                    paramsTypeName = ctor.Parameters[0].Type
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    // Check nullability for ParameterRequired
                    parameterRequired = ctor.Parameters[0].Type.NullableAnnotation
                        != NullableAnnotation.Annotated;
                    break;
                }
            }

            mode = RouteMode.Manual;
        }
        else return null;

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
            ? DeriveRouteClassName(routeName) : null;

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

        // Route interface
        string interfaceClause;
        if (model.ResultTypeName is not null)
            interfaceClause = $"global::YFex.NavigatR.IRouteProduces<{model.ResultTypeName}>";
        else
            interfaceClause = "global::YFex.NavigatR.IRoute";

        string displayNameLiteral = model.DisplayName is not null
            ? "\"" + model.DisplayName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : "null";

        // Constructor parameter — required = T, optional = T? = null
        string? ctorParam = null;
        if (model.ParamsTypeName is not null)
        {
            ctorParam = model.ParameterRequired
                ? $"{model.ParamsTypeName} Params"
                : $"{model.ParamsTypeName}? Params = null";
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.Append("namespace "); sb.Append(model.Namespace); sb.AppendLine(";");
            sb.AppendLine();
        }

        // Record with optional constructor param
        if (ctorParam is not null)
        {
            sb.Append("public sealed record "); sb.Append(model.GeneratedRouteClassName);
            sb.Append("("); sb.Append(ctorParam); sb.Append(") : ");
            sb.AppendLine(interfaceClause);
        }
        else
        {
            sb.Append("public sealed record "); sb.Append(model.GeneratedRouteClassName);
            sb.Append(" : "); sb.AppendLine(interfaceClause);
        }

        sb.AppendLine("{");
        sb.Append("    public string? DisplayName => "); sb.Append(displayNameLiteral); sb.AppendLine(";");
        sb.AppendLine("}");

        string hintName = string.IsNullOrEmpty(model.Namespace)
            ? $"{model.GeneratedRouteClassName}.g.cs"
            : $"{model.Namespace}.{model.GeneratedRouteClassName}.g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

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

        if (hasParam)
        {
            string paramType = model.ParameterRequired ? p! : $"{p}?";

            // Explicit interface bridge — reads Params property from route
            sb.AppendLine("    // Parameter bridge — not visible in IntelliSense");
            sb.AppendLine($"    global::System.Threading.Tasks.Task global::YFex.NavigatR.INavigable.OnNavigation(");
            sb.AppendLine($"        global::YFex.NavigatR.NavigationContext context,");
            sb.AppendLine($"        global::System.Threading.CancellationToken ct)");

            // Determine the fully-qualified route type for the direct cast
            string routeTypeFqn = model.Mode == RouteMode.AutoGenerate
                ? (string.IsNullOrEmpty(model.Namespace)
                    ? $"global::{model.GeneratedRouteClassName}"
                    : $"global::{model.Namespace}.{model.GeneratedRouteClassName}")
                : $"global::{model.ManualRouteTypeName}";

            if (model.ParameterRequired)
            {
                // Direct cast — no reflection, no GetParameter<T>()
                sb.AppendLine($"        => OnNavigation((({routeTypeFqn})context.Route).Params, ct);");
            }
            else
            {
                sb.AppendLine($"    {{");
                sb.AppendLine($"        return OnNavigation((({routeTypeFqn})context.Route).Params, ct);");
                sb.AppendLine($"    }}");
            }

            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Called when the screen is navigated to.</summary>");
            sb.AppendLine($"    public partial global::System.Threading.Tasks.Task OnNavigation(");
            sb.AppendLine($"        {paramType} parameter,");
            sb.AppendLine($"        global::System.Threading.CancellationToken ct = default);");
            sb.AppendLine();
        }

        if (hasResult)
        {
            sb.AppendLine($"    private readonly {tcsType} _navigationResultTcs");
            sb.AppendLine($"        = new {tcsType}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            sb.AppendLine();

            sb.AppendLine($"    public void Returns({r} result)");
            sb.AppendLine($"        => _navigationResultTcs.TrySetResult(new {resultUnion}.Success(result));");
            sb.AppendLine();

            sb.AppendLine($"    public void Cancel()");
            sb.AppendLine($"        => _navigationResultTcs.TrySetResult(new {resultUnion}.Cancelled());");
            sb.AppendLine();

            sb.AppendLine($"    public void Deny(string? reason = null)");
            sb.AppendLine($"        => _navigationResultTcs.TrySetResult(new {resultUnion}.Denied(reason));");
            sb.AppendLine();

            sb.AppendLine($"    {$"global::System.Threading.Tasks.Task<{resultUnion}>"}");
            sb.AppendLine($"        global::YFex.NavigatR.INavigable<{r}>.WaitForResultAsync()");
            sb.AppendLine($"        => _navigationResultTcs.Task;");
        }

        sb.AppendLine("}");

        // File-scoped GetParameter() extension — direct cast, no reflection
        if (hasParam)
        {
            string paramType = model.ParameterRequired ? p! : $"{p}?";
            string routeTypeFqn2 = model.Mode == RouteMode.AutoGenerate
                ? (string.IsNullOrEmpty(model.Namespace)
                    ? $"global::{model.GeneratedRouteClassName}"
                    : $"global::{model.Namespace}.{model.GeneratedRouteClassName}")
                : $"global::{model.ManualRouteTypeName}";

            sb.AppendLine();
            sb.AppendLine($"file static class NavigationContextExtensions_{model.ViewModelName}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal static {paramType} GetParameter(");
            sb.AppendLine($"        this global::YFex.NavigatR.NavigationContext context)");
            sb.AppendLine($"        => (({routeTypeFqn2})context.Route).Params;");
            sb.AppendLine("}");
        }

        string hintName = string.IsNullOrEmpty(model.Namespace)
            ? $"{model.ViewModelName}.NavigatR.g.cs"
            : $"{model.Namespace}.{model.ViewModelName}.NavigatR.g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

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