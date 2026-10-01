using System.Collections.Generic;
using System.Text;

namespace YFex.Cqrs.SourceGenerator;

/// <summary>
/// Emits the ActualLab.Fusion proxy types of the generated RPC contract (validation D30).
/// </summary>
/// <remarks>
/// Fusion 13 finds proxies ONLY by naming convention (<c>ActualLab.Interception.Proxies.TryGetProxyType</c> →
/// <c>{Namespace}.ActualLabProxies.{Name}Proxy</c> in the proxied type's assembly) and has no runtime fallback. Its own
/// generator (ActualLab.Generators, syntax-only) cannot see the CQRS-generated contract, so this builder reproduces its
/// output byte for byte for the three types Pipeline C emits:
/// <list type="bullet">
///   <item><c>I{Aggregate}RpcContract</c> and <c>I{Aggregate}RpcClient</c> → interface proxies (Fusion clients);</item>
///   <item><c>{Aggregate}RpcContractImpl</c> → class proxy (server compute-method interception).</item>
/// </list>
/// The template mirrors <c>ActualLab.Generators.ProxyTypeGenerator</c> of ActualLab.Fusion <b>13.0.12</b> (pinned). The
/// golden test <c>YFex.Messaging.Rpc.Tests/ProxyGoldenTests</c> runs the real ActualLab generator on a source copy of the
/// generated contract and fails on any difference — re-check it whenever the Fusion version changes.
/// </remarks>
internal static class RpcProxyCodeBuilder
{
    private const string Cancel = "global::System.Threading.CancellationToken";

    // ArgumentList has generic item slots up to 4 (ActualLab MaxGenericArgumentListItemCount); beyond, items are cast.
    private const int MaxGenericItems = 4;

    /// <summary>One proxied method: <c>Task&lt;{Wrapper}&lt;T&gt;&gt; Name(params)</c>.</summary>
    private readonly struct ProxyMethod
    {
        public ProxyMethod(string name, (string Type, string Name)[] parameters, string returnType, string keepReturnType)
        {
            Name = name;
            Parameters = parameters;
            ReturnType = returnType;
            KeepReturnType = keepReturnType;
        }

        public string Name { get; }
        /// <summary>Parameter types in ActualLab's format (<c>global::…</c>; top-level special types as keywords).</summary>
        public (string Type, string Name)[] Parameters { get; }
        /// <summary>Full return type, e.g. <c>global::System.Threading.Tasks.Task&lt;global::YFex.Cqrs.Result&lt;…&gt;&gt;</c>.</summary>
        public string ReturnType { get; }
        /// <summary>The unwrapped (Task argument) type in <c>KeepCode</c> format.</summary>
        public string KeepReturnType { get; }
    }

    public static (string Hint, string Source) GenerateContractProxy(ClassToGenerate model)
        => Generate(model, $"I{model.ClassName}RpcContract", isInterface: true, RpcContractCodeBuilder.ContractUsings);

    public static (string Hint, string Source) GenerateClientProxy(ClassToGenerate model)
        => Generate(model, $"I{model.ClassName}RpcClient", isInterface: true, RpcContractCodeBuilder.ContractUsings);

    public static (string Hint, string Source) GenerateImplProxy(ClassToGenerate model)
        => Generate(model, $"{model.ClassName}RpcContractImpl", isInterface: false, RpcContractCodeBuilder.ImplUsings);

    private static (string Hint, string Source) Generate(ClassToGenerate model, string typeName, bool isInterface, string[] usings)
    {
        string hint = string.IsNullOrEmpty(model.Namespace)
            ? $"{typeName}Proxy.g.cs"
            : $"{model.Namespace}.{typeName}Proxy.g.cs";
        return (hint, Build(model, typeName, isInterface, usings));
    }

    private static List<ProxyMethod> Methods(ClassToGenerate model)
    {
        var methods = new List<ProxyMethod>();
        foreach (var q in model.Queries)
            methods.Add(new ProxyMethod(q.MethodName,
                // `string? variant` → ActualLab prints the top-level special type as its keyword, without `?`.
                new[] { (RpcContractCodeBuilder.QueryType(model, q.RecordName), "q"), ("string", "variant"), (Cancel, "ct") },
                "global::System.Threading.Tasks.Task<global::YFex.Cqrs.Result<" + q.ProxyReturnType + ">>",
                "YFex.Cqrs.Result<" + q.KeepReturnType + ">"));
        foreach (var c in model.Commands)
        {
            bool hasResult = c.ProxyResultType is not null;
            methods.Add(new ProxyMethod(c.MethodName,
                new[] { (RpcContractCodeBuilder.CommandType(model, c.RecordName), "cmd"), (Cancel, "ct") },
                hasResult
                    ? "global::System.Threading.Tasks.Task<global::YFex.Cqrs.QueueableResult<" + c.ProxyResultType + ">>"
                    : "global::System.Threading.Tasks.Task<global::YFex.Cqrs.QueueableResult>",
                hasResult ? "YFex.Cqrs.QueueableResult<" + c.KeepResultType + ">" : "YFex.Cqrs.QueueableResult"));
        }
        return methods;
    }

    private static string Build(ClassToGenerate model, string typeName, bool isInterface, string[] usings)
    {
        string typeRef = string.IsNullOrEmpty(model.Namespace) ? typeName : "global::" + model.Namespace + "." + typeName;
        string proxyName = typeName + "Proxy";
        string proxyNs = string.IsNullOrEmpty(model.Namespace) ? "ActualLabProxies" : model.Namespace + ".ActualLabProxies";
        var methods = Methods(model);

        // __cachedInterceptN is shared by methods with the same return type (first index wins), as in ActualLab.
        var interceptField = new string[methods.Count];
        var firstByReturnType = new Dictionary<string, int>();
        for (int i = 0; i < methods.Count; i++)
        {
            if (!firstByReturnType.TryGetValue(methods[i].ReturnType, out var first))
                firstByReturnType[methods[i].ReturnType] = first = i;
            interceptField[i] = "__cachedIntercept" + first;
        }

        var sb = new StringBuilder(4096);
        sb.Append("// <auto-generated/>\n#nullable enable\n#nullable disable warnings\n\n");
        foreach (var u in usings) sb.Append("using ").Append(u).Append(";\n");
        if (usings.Length > 0) sb.Append('\n');
        sb.Append("namespace ").Append(proxyNs).Append(";\n");
        foreach (var code in new[] { "IL2026", "IL2072", "IL2092", "IL2111", "IL3050" })
        {
            sb.Append("#if !NETSTANDARD2_0 && !NET472\n");
            sb.Append("[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"").Append(code)
              .Append("\", Justification = \"Proxy code\")]\n");
            sb.Append("#endif\n");
        }
        sb.Append("#pragma warning disable CS0618\n#pragma warning disable CS0672\n#pragma warning disable CS1591\n");
        sb.Append("public sealed class ").Append(proxyName).Append(" : ");
        if (isInterface) sb.Append("global::ActualLab.Interception.Internal.InterfaceProxy, ");
        sb.Append(typeRef).Append(", global::ActualLab.Interception.IProxy\n{\n");

        // Static fields: cached MethodInfo per method.
        for (int i = 0; i < methods.Count; i++)
        {
            var m = methods[i];
            sb.Append("    private static global::System.Reflection.MethodInfo? __cachedMethod").Append(i)
              .Append(" = __cachedMethod").Append(i)
              .Append(" = global::ActualLab.Interception.Internal.ProxyHelper.GetMethodInfo(typeof(").Append(typeRef)
              .Append("), \"").Append(m.Name).Append("\", new[] { ");
            for (int p = 0; p < m.Parameters.Length; p++)
                sb.Append(p == 0 ? "" : ", ").Append("typeof(").Append(m.Parameters[p].Type).Append(')');
            sb.Append(" });\n");
        }

        // Instance fields: intercepted delegate per method, intercept delegate per distinct return type, interceptor.
        for (int i = 0; i < methods.Count; i++)
        {
            var m = methods[i];
            sb.Append("    private global::System.Func<global::ActualLab.Interception.ArgumentList, ").Append(m.ReturnType)
              .Append(">? __cachedIntercepted").Append(i).Append(";\n");
            if (interceptField[i] == "__cachedIntercept" + i)
                sb.Append("    private global::System.Func<global::ActualLab.Interception.Invocation, ").Append(m.ReturnType)
                  .Append(">? __cachedIntercept").Append(i).Append(";\n");
        }
        sb.Append("    private global::ActualLab.Interception.Interceptor? __interceptor;\n");

        // IProxy.Interceptor
        sb.Append("    global::ActualLab.Interception.Interceptor global::ActualLab.Interception.IProxy.Interceptor\n");
        sb.Append("    {\n");
        sb.Append("        get\n        {\n");
        sb.Append("            if (__interceptor == null)\n");
        sb.Append("                throw global::ActualLab.Interception.Internal.Errors.NoInterceptor();\n");
        sb.Append("            return __interceptor;\n");
        sb.Append("        }\n\n");
        sb.Append("        set\n        {\n");
        sb.Append("            __interceptor = value ?? throw new global::System.ArgumentNullException(\"value\");\n");
        for (int i = 0; i < methods.Count; i++)
        {
            if (interceptField[i] != "__cachedIntercept" + i) continue;
            sb.Append("            __cachedIntercept").Append(i).Append(" = __interceptor.Intercept<")
              .Append(methods[i].ReturnType).Append(">;\n");
        }
        sb.Append("        }\n");
        sb.Append("    }\n");

        // Class proxy: mirror the impl's public constructor (IHandlerInvoker invoker).
        if (!isInterface)
        {
            sb.Append('\n');
            sb.Append("    public ").Append(proxyName)
              .Append("(global::YFex.Messaging.Rpc.IHandlerInvoker invoker) : base(invoker)\n");
            sb.Append("    {\n    }\n");
        }

        // Methods
        for (int i = 0; i < methods.Count; i++)
        {
            var m = methods[i];
            string target = isInterface ? "((" + typeRef + ")ProxyTarget!)" : "base";
            sb.Append('\n');
            var ps = m.Parameters;
            sb.Append("    public ").Append(isInterface ? "" : "override ").Append(m.ReturnType).Append(' ').Append(m.Name).Append('(');
            for (int p = 0; p < ps.Length; p++)
                sb.Append(p == 0 ? "" : ", ").Append(ps[p].Type).Append(' ').Append(ps[p].Name);
            sb.Append(")\n");
            sb.Append("    {\n");
            sb.Append("        var intercepted = __cachedIntercepted").Append(i).Append(" ??= args =>\n");
            sb.Append("        {\n");
            sb.Append("            if (args is global::ActualLab.Interception.ArgumentListG").Append(ps.Length).Append('<');
            for (int p = 0; p < ps.Length && p < MaxGenericItems; p++)
                sb.Append(p == 0 ? "" : ", ").Append(ps[p].Type);
            sb.Append("> ga)\n");
            sb.Append("                return ").Append(target).Append('.').Append(m.Name).Append('(');
            for (int p = 0; p < ps.Length; p++)
            {
                sb.Append(p == 0 ? "" : ", ");
                if (p >= MaxGenericItems) sb.Append('(').Append(ps[p].Type).Append(')');
                sb.Append("ga.Item").Append(p);
            }
            sb.Append(");\n");
            sb.Append("            var sa = (global::ActualLab.Interception.ArgumentListS").Append(ps.Length).Append(")args;\n");
            sb.Append("            return ").Append(target).Append('.').Append(m.Name).Append('(');
            for (int p = 0; p < ps.Length; p++)
                sb.Append(p == 0 ? "" : ", ").Append('(').Append(ps[p].Type).Append(")sa.Item").Append(p);
            sb.Append(");\n");
            sb.Append("        };\n");
            sb.Append("        var invocation = new global::ActualLab.Interception.Invocation(this, __cachedMethod").Append(i)
              .Append("!, global::ActualLab.Interception.ArgumentList.New(");
            for (int p = 0; p < ps.Length; p++)
                sb.Append(p == 0 ? "" : ", ").Append(ps[p].Name);
            sb.Append("), intercepted);\n");
            sb.Append("        if (").Append(interceptField[i]).Append(" == null)\n");
            sb.Append("            throw global::ActualLab.Interception.Internal.Errors.NoInterceptor();\n");
            sb.Append("        return ").Append(interceptField[i]).Append(".Invoke(invocation);\n");
            sb.Append("    }\n");
        }

        // KeepCode (trimming roots) — registered as a module initializer like ActualLab does.
        sb.Append('\n');
        sb.Append("#if !NETSTANDARD2_0 && !NET472\n");
        sb.Append("    [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
        sb.Append("#endif\n");
        sb.Append("    public static void KeepCode()\n");
        sb.Append("    {\n");
        sb.Append("        if (global::ActualLab.Trimming.CodeKeeper.AlwaysFalse)\n");
        sb.Append("        {\n");
        sb.Append("            global::ActualLab.Interception.Trimming.ProxyCodeKeeper.KeepProxy<").Append(typeRef).Append(", ")
          .Append(proxyName).Append(">();\n");
        sb.Append("            global::ActualLab.Trimming.CodeKeeper.Keep<").Append(proxyName).Append(">();\n");
        foreach (var m in methods)
        {
            sb.Append("            global::ActualLab.Interception.Trimming.ProxyCodeKeeper.KeepAsyncMethod<").Append(m.KeepReturnType);
            foreach (var p in m.Parameters) sb.Append(", ").Append(p.Type);
            sb.Append(">(\"").Append(m.Name).Append("\");\n");
        }
        sb.Append("        }\n");
        sb.Append("    }\n");
        sb.Append('}');
        return sb.ToString();
    }
}
