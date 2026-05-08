using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
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

    private static readonly DiagnosticDescriptor s_nav005 = new(
        "NAV005", "[Prefetch] must return Task or Task<T>",
        "[Prefetch] method '{0}' must return Task or Task<T>.",
        "YFex.NavigatR", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_nav007 = new(
        "NAV007", "Duplicate [Prefetch] return type",
        "Multiple [Prefetch] methods on '{0}' return Task<{1}>. Each return type must be unique.",
        "YFex.NavigatR", DiagnosticSeverity.Error, true);

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

    // -------------------------------------------------------------------------
    // ExtractModel
    // -------------------------------------------------------------------------

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

            foreach (var ctor in routeTypeSymbol.Constructors)
            {
                if (ctor.Parameters.Length == 1)
                {
                    paramsTypeName = ctor.Parameters[0].Type
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    parameterRequired = ctor.Parameters[0].Type.NullableAnnotation != NullableAnnotation.Annotated;
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

        // Scan [Prefetch]-marked methods
        var prefetchList = new List<PrefetchMethod>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;

            bool hasPrefetchAttr = false;
            foreach (var a in method.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() == "YFex.NavigatR.PrefetchAttribute")
                {
                    hasPrefetchAttr = true;
                    break;
                }
            }
            if (!hasPrefetchAttr) continue;

            var returnType = method.ReturnType;
            bool isTaskVoid = returnType.ToDisplayString() == "System.Threading.Tasks.Task";
            bool isTaskT = returnType is INamedTypeSymbol rn
                && rn.IsGenericType
                && rn.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>";

            if (!isTaskVoid && !isTaskT) continue;

            string? retTypeName = null;
            bool producesValue = false;
            if (isTaskT && returnType is INamedTypeSymbol taskT)
            {
                retTypeName = taskT.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                producesValue = true;
            }

            prefetchList.Add(new PrefetchMethod(method.Name, retTypeName, producesValue));
        }

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
            DisplayName: displayName,
            PrefetchMethods: new EquatableArray<PrefetchMethod>(prefetchList));
    }

    // -------------------------------------------------------------------------
    // EmitDiagnosticsAndRoute
    // -------------------------------------------------------------------------

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

        string interfaceClause = model.ResultTypeName is not null
            ? "global::YFex.NavigatR.IRouteProduces<" + model.ResultTypeName + ">"
            : "global::YFex.NavigatR.IRoute";

        string displayNameLiteral = model.DisplayName is not null
            ? "\"" + model.DisplayName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : "null";

        string? ctorParam = null;
        if (model.ParamsTypeName is not null)
        {
            ctorParam = model.ParameterRequired
                ? model.ParamsTypeName + " Params"
                : model.ParamsTypeName + "? Params = null";
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

        if (ctorParam is not null)
            sb.AppendLine("public sealed record " + model.GeneratedRouteClassName + "(" + ctorParam + ") : " + interfaceClause);
        else
            sb.AppendLine("public sealed record " + model.GeneratedRouteClassName + " : " + interfaceClause);

        sb.AppendLine("{");
        sb.AppendLine("    public string? DisplayName => " + displayNameLiteral + ";");
        sb.AppendLine("}");

        string hintName = string.IsNullOrEmpty(model.Namespace)
            ? model.GeneratedRouteClassName + ".g.cs"
            : model.Namespace + "." + model.GeneratedRouteClassName + ".g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

    // -------------------------------------------------------------------------
    // EmitViewModelPartial
    // -------------------------------------------------------------------------

    private static void EmitViewModelPartial(SourceProductionContext spc, RouteViewModel model)
    {
        if (!model.IsPartial || !model.ImplementsINavigable) return;

        bool hasParam = model.ParamsTypeName is not null;
        bool hasResult = model.ProducesResult && model.ResultTypeName is not null;

        // Collect prefetch methods into plain lists
        var allPrefetches = new List<PrefetchMethod>();
        foreach (var pm in model.PrefetchMethods)
            allPrefetches.Add(pm);

        var valuePrefetches = new List<PrefetchMethod>();
        var warmPrefetches = new List<PrefetchMethod>();
        foreach (var pm in allPrefetches)
        {
            if (pm.ProducesValue) valuePrefetches.Add(pm);
            else warmPrefetches.Add(pm);
        }

        bool hasPrefetch = allPrefetches.Count > 0;

        if (!hasParam && !hasResult && !hasPrefetch) return;

        var p = model.ParamsTypeName;
        var r = model.ResultTypeName;
        string? resultUnion = hasResult ? "global::YFex.NavigatR.NavigationResult<" + r + ">" : null;
        string? tcsType = hasResult
            ? "global::System.Threading.Tasks.TaskCompletionSource<" + resultUnion + ">"
            : null;

        string routeTypeFqn = model.Mode == RouteMode.AutoGenerate
            ? (string.IsNullOrEmpty(model.Namespace)
                ? "global::" + model.GeneratedRouteClassName
                : "global::" + model.Namespace + "." + model.GeneratedRouteClassName)
            : "global::" + model.ManualRouteTypeName;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.Append("namespace "); sb.Append(model.Namespace); sb.AppendLine(";");
            sb.AppendLine();
        }

        sb.AppendLine("partial class " + model.ViewModelName);
        sb.AppendLine("{");

        // ── Prefetch backing Task fields ─────────────────────────────────
        foreach (var pm in valuePrefetches)
        {
            sb.AppendLine("    private global::System.Threading.Tasks.Task<" + pm.ReturnTypeName + ">? _" + ToCamelCase(pm.MethodName) + "Task;");
        }
        if (valuePrefetches.Count > 0) sb.AppendLine();

        // ── INavigable.OnPrefetch bridge ─────────────────────────────────
        if (hasPrefetch)
        {
            sb.AppendLine("    global::System.Threading.Tasks.Task global::YFex.NavigatR.INavigable.OnPrefetch(");
            sb.AppendLine("        global::YFex.NavigatR.NavigationContext context,");
            sb.AppendLine("        global::System.Threading.CancellationToken ct)");
            sb.AppendLine("    {");

            if (hasParam)
                sb.AppendLine("        var __p = ((" + routeTypeFqn + ")context.Route).Params;");

            foreach (var pm in valuePrefetches)
            {
                string pArg = hasParam ? "__p, ct" : "ct";
                sb.AppendLine("        _" + ToCamelCase(pm.MethodName) + "Task = global::System.Threading.Tasks.Task.Run(() => " + pm.MethodName + "Core(" + pArg + "));");
            }

            foreach (var pm in warmPrefetches)
            {
                string pArg = hasParam ? "__p, ct" : "ct";
                sb.AppendLine("        var __" + pm.MethodName + "Warm = global::System.Threading.Tasks.Task.Run(() => " + pm.MethodName + "(" + pArg + "));");
            }

            // Return statement
            var allTaskRefs = new List<string>();
            foreach (var pm in valuePrefetches)
                allTaskRefs.Add("_" + ToCamelCase(pm.MethodName) + "Task!");
            foreach (var pm in warmPrefetches)
                allTaskRefs.Add("__" + pm.MethodName + "Warm");

            if (allTaskRefs.Count == 1)
                sb.AppendLine("        return " + allTaskRefs[0] + ";");
            else
                sb.AppendLine("        return global::System.Threading.Tasks.Task.WhenAll(" + string.Join(", ", allTaskRefs) + ");");

            sb.AppendLine("    }");
            sb.AppendLine();

            // Rename original [Prefetch] Task<T> methods to {Name}Core
            foreach (var pm in valuePrefetches)
            {
                string pSig = hasParam ? p + " parameter, " : "";
                sb.AppendLine("    private partial global::System.Threading.Tasks.Task<" + pm.ReturnTypeName + "> " + pm.MethodName + "Core(" + pSig + "global::System.Threading.CancellationToken ct);");
                sb.AppendLine();
            }
        }

        // ── INavigable.OnNavigation bridge ───────────────────────────────
        if (hasParam || hasPrefetch)
        {
            sb.AppendLine("    async global::System.Threading.Tasks.Task global::YFex.NavigatR.INavigable.OnNavigation(");
            sb.AppendLine("        global::YFex.NavigatR.NavigationContext context,");
            sb.AppendLine("        global::System.Threading.CancellationToken ct)");
            sb.AppendLine("    {");

            if (hasParam)
                sb.AppendLine("        var __p = ((" + routeTypeFqn + ")context.Route).Params;");

            // Resolve each prefetch task
            foreach (var pm in valuePrefetches)
            {
                string pArg = hasParam ? "__p, ct" : "ct";
                string fieldName = "_" + ToCamelCase(pm.MethodName) + "Task";
                string localName = "__" + ToCamelCase(pm.MethodName);
                sb.AppendLine("        var " + localName + " = " + fieldName + " is not null");
                sb.AppendLine("            ? await " + fieldName + ".ConfigureAwait(false)");
                sb.AppendLine("            : await " + pm.MethodName + "Core(" + pArg + ").ConfigureAwait(false);");
                sb.AppendLine("        " + fieldName + " = null;");
            }

            // Build call args
            var callArgs = new List<string>();
            if (hasParam) callArgs.Add("__p");
            foreach (var pm in valuePrefetches) callArgs.Add("__" + ToCamelCase(pm.MethodName));
            callArgs.Add("ct");

            sb.AppendLine("        await OnNavigation(" + string.Join(", ", callArgs) + ");");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Enforced partial signature
            var sigArgs = new List<string>();
            if (hasParam)
            {
                string paramType = model.ParameterRequired ? p! : p + "?";
                sigArgs.Add(paramType + " parameter");
            }
            foreach (var pm in valuePrefetches)
                sigArgs.Add(pm.ReturnTypeName + " " + ToCamelCase(pm.MethodName));
            sigArgs.Add("global::System.Threading.CancellationToken ct = default");

            sb.AppendLine("    /// <summary>Called when the screen is navigated to. All prefetch data is ready.</summary>");
            sb.AppendLine("    public partial global::System.Threading.Tasks.Task OnNavigation(");
            for (int i = 0; i < sigArgs.Count; i++)
            {
                bool last = i == sigArgs.Count - 1;
                sb.AppendLine("        " + sigArgs[i] + (last ? ");" : ","));
            }
            sb.AppendLine();
        }
        else if (hasParam)
        {
            string paramType = model.ParameterRequired ? p! : p + "?";
            sb.AppendLine("    global::System.Threading.Tasks.Task global::YFex.NavigatR.INavigable.OnNavigation(");
            sb.AppendLine("        global::YFex.NavigatR.NavigationContext context,");
            sb.AppendLine("        global::System.Threading.CancellationToken ct)");

            if (model.ParameterRequired)
                sb.AppendLine("        => OnNavigation(((" + routeTypeFqn + ")context.Route).Params, ct);");
            else
            {
                sb.AppendLine("    {");
                sb.AppendLine("        return OnNavigation(((" + routeTypeFqn + ")context.Route).Params, ct);");
                sb.AppendLine("    }");
            }
            sb.AppendLine();
            sb.AppendLine("    public partial global::System.Threading.Tasks.Task OnNavigation(");
            sb.AppendLine("        " + paramType + " parameter,");
            sb.AppendLine("        global::System.Threading.CancellationToken ct = default);");
            sb.AppendLine();
        }

        // ── Result plumbing ──────────────────────────────────────────────
        if (hasResult)
        {
            sb.AppendLine("    private readonly " + tcsType + " _navigationResultTcs");
            sb.AppendLine("        = new " + tcsType + "(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
            sb.AppendLine();
            sb.AppendLine("    public void Returns(" + r + " result)");
            sb.AppendLine("        => _navigationResultTcs.TrySetResult(new " + resultUnion + ".Success(result));");
            sb.AppendLine();
            sb.AppendLine("    public void Cancel()");
            sb.AppendLine("        => _navigationResultTcs.TrySetResult(new " + resultUnion + ".Cancelled());");
            sb.AppendLine();
            sb.AppendLine("    public void Deny(string? reason = null)");
            sb.AppendLine("        => _navigationResultTcs.TrySetResult(new " + resultUnion + ".Denied(reason));");
            sb.AppendLine();
            sb.AppendLine("    global::System.Threading.Tasks.Task<" + resultUnion + ">");
            sb.AppendLine("        global::YFex.NavigatR.INavigable<" + r + ">.WaitForResultAsync()");
            sb.AppendLine("        => _navigationResultTcs.Task;");
        }

        sb.AppendLine("}");

        // ── File-scoped GetParameter extension ───────────────────────────
        if (hasParam)
        {
            string paramType = model.ParameterRequired ? p! : p + "?";
            sb.AppendLine();
            sb.AppendLine("file static class NavigationContextExtensions_" + model.ViewModelName);
            sb.AppendLine("{");
            sb.AppendLine("    internal static " + paramType + " GetParameter(");
            sb.AppendLine("        this global::YFex.NavigatR.NavigationContext context)");
            sb.AppendLine("        => ((" + routeTypeFqn + ")context.Route).Params;");
            sb.AppendLine("}");
        }

        string hintName2 = string.IsNullOrEmpty(model.Namespace)
            ? model.ViewModelName + ".NavigatR.g.cs"
            : model.Namespace + "." + model.ViewModelName + ".NavigatR.g.cs";

        spc.AddSource(hintName2, sb.ToString());
    }

    // -------------------------------------------------------------------------
    // EmitRegistration
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
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