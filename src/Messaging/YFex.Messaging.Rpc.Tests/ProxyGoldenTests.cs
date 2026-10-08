using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using YFex.Cqrs.SourceGenerator;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// GOLDEN test for validation D30: the CQRS generator emits the Fusion proxies of the generated RPC contract itself
/// (ActualLab.Generators is syntax-only and can't see generated types; Fusion 13 finds proxies only by naming convention).
/// Here the REAL ActualLab.Generators 13.0.12 runs on a source copy of the generated contract/impl, and its output must be
/// byte-identical (modulo line endings) to what <c>RpcProxyCodeBuilder</c> emitted. Any Fusion upgrade that changes the
/// proxy template fails this test.
/// </summary>
public class ProxyGoldenTests
{
    private const string NamespacedAggregate = """
        using System;
        using System.Collections.Generic;
        using YFex.Cqrs;

        namespace Golden.Orders;

        public partial class OrderAggregate
        {
            public static partial class Queries
            {
                public sealed partial record GetOrderLines(int Id) : IQuery<List<string>>;
                public sealed partial record SearchOrders(string Term) : IQuery<List<string>>;   // same result type → shared intercept field
                public sealed partial record CountOrdersQuery(string? Term) : IQuery<int>;
                public sealed partial record FindDiscount(Guid Id) : IQuery<decimal?>;          // Nullable<T> expansion
                public sealed partial record GetTags(int Id) : IQuery<string[]>;
            }

            public static partial class Commands
            {
                public sealed partial record PlaceOrder(string Sku, int Quantity) : ICommand<Guid>;
                public sealed partial record CancelOrderCommand(int Id) : ICommand;
                public sealed partial record RenameOrder(int Id, string Name) : ICommand<int>;
                public sealed partial record ArchiveOrder(int Id) : ICommand;                    // second void → shared field
            }
        }
        """;

    private const string GlobalAggregate = """
        using YFex.Cqrs;

        public partial class StockAggregate
        {
            public static partial class Queries
            {
                public sealed partial record GetStock(string Sku) : IQuery<int>;
            }

            public static partial class Commands
            {
                public sealed partial record Reserve(string Sku, int Quantity) : ICommand<bool>;
            }
        }
        """;

    public static TheoryData<string, string> Cases() => new()
    {
        { NamespacedAggregate, "Golden.Orders.IOrderAggregateRpcContractProxy.g.cs" },
        { NamespacedAggregate, "Golden.Orders.IOrderAggregateRpcClientProxy.g.cs" },
        { NamespacedAggregate, "Golden.Orders.OrderAggregateRpcContractImplProxy.g.cs" },
        { GlobalAggregate, "IStockAggregateRpcContractProxy.g.cs" },
        { GlobalAggregate, "IStockAggregateRpcClientProxy.g.cs" },
        { GlobalAggregate, "StockAggregateRpcContractImplProxy.g.cs" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emitted_proxy_is_identical_to_ActualLab_output(string aggregateSource, string proxyHint)
    {
        // 1. Our generator: contract, impl and the proxies it emits.
        var ours = RunGenerator(new CQRSGenerator(), Compilation(Parse(aggregateSource, "Aggregate.cs")));
        ours.Should().ContainKey(proxyHint, "Pipeline C must emit the proxy");

        // 2. ActualLab's generator on a SOURCE copy of the generated contract + impl (as if hand-written).
        var sourceCopy = ours.Where(kv => kv.Key.EndsWith("RpcContract.g.cs", StringComparison.Ordinal)
                                          || kv.Key.EndsWith("RpcContractImpl.g.cs", StringComparison.Ordinal))
            .Select(kv => Parse(kv.Value, kv.Key))
            .Prepend(Parse(aggregateSource, "Aggregate.cs"))
            .ToArray();
        sourceCopy.Should().HaveCount(3);
        var actualLab = RunGenerator(LoadActualLabProxyGenerator(), Compilation(sourceCopy));

        actualLab.Should().ContainKey(proxyHint);
        Normalize(ours[proxyHint]).Should().Be(Normalize(actualLab[proxyHint]));
    }

    /// <summary>
    /// YFex.Messaging.Rpc's own services (<c>ICallPermissions</c>, D29; <c>ICentralTime</c>, time T2) ship ActualLab's proxy output
    /// checked in verbatim (the library doesn't reference ActualLab.Generators): regenerate from the source file and compare.
    /// </summary>
    [Theory]
    [InlineData("YFex.Messaging.Rpc.ICallPermissionsProxy.g.cs", "ICallPermissionsProxy.cs", "CallContext", "CallPermissions.cs")]
    [InlineData("YFex.Messaging.Rpc.CallPermissionsServiceProxy.g.cs", "CallPermissionsServiceProxy.cs", "CallContext", "CallPermissions.cs")]
    [InlineData("YFex.Messaging.Rpc.ICentralTimeProxy.g.cs", "ICentralTimeProxy.cs", "Time", "CentralTime.cs")]
    public void Library_proxies_are_identical_to_ActualLab_output(string hint, string checkedInFile, string folder, string sourceFile)
    {
        var dir = Path.Combine(RepoMessagingDir(), "YFex.Messaging.Rpc", folder);
        var source = Parse(File.ReadAllText(Path.Combine(dir, sourceFile)), sourceFile);
        // The library builds with <ImplicitUsings>: mirror them or `Task` wouldn't bind (ActualLab then skips the methods).
        var implicitUsings = Parse("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, "GlobalUsings.g.cs");

        var actualLab = RunGenerator(LoadActualLabProxyGenerator(), Compilation(implicitUsings, source));

        actualLab.Should().ContainKey(hint);
        var checkedIn = Path.Combine(dir, "ActualLabProxies", checkedInFile);
        if (!File.Exists(checkedIn) || Normalize(File.ReadAllText(checkedIn)) != Normalize(actualLab[hint]))
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, checkedInFile + ".expected"), actualLab[hint]);
        File.Exists(checkedIn).Should().BeTrue($"{checkedInFile} must be checked in (expected output written next to the test binaries)");
        Normalize(File.ReadAllText(checkedIn)).Should().Be(Normalize(actualLab[hint]));
    }

    private static string RepoMessagingDir([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;

    [Fact]
    public void Generated_contract_and_proxies_compile()
    {
        var compilation = Compilation(Parse(NamespacedAggregate, "Aggregate.cs"));
        var driver = CSharpGeneratorDriver.Create(new[] { new CQRSGenerator().AsSourceGenerator() },
            parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Roslyn plumbing ──────────────────────────────────────────────────────

    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Preview).WithPreprocessorSymbols("YFEX_RPC");

    private static SyntaxTree Parse(string source, string path) => CSharpSyntaxTree.ParseText(source, ParseOptions, path);

    private static CSharpCompilation Compilation(params SyntaxTree[] trees)
        => CSharpCompilation.Create("Golden", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static Dictionary<string, string> RunGenerator(IIncrementalGenerator generator, Compilation compilation)
    {
        var driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: ParseOptions)
            .RunGenerators(compilation);
        var result = driver.GetRunResult().Results.Single();
        result.Exception.Should().BeNull();
        return result.GeneratedSources.ToDictionary(s => s.HintName, s => s.SourceText.ToString());
    }

    private static IIncrementalGenerator LoadActualLabProxyGenerator()
    {
        var path = typeof(ProxyGoldenTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ActualLabGenerators").Value!;
        File.Exists(path).Should().BeTrue($"ActualLab.Generators must be restored at {path}");
        var type = Assembly.LoadFrom(path).GetType("ActualLab.Generators.ProxyGenerator", throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(type)!;
    }

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        // Make sure every assembly the generated code touches is loaded, then reference the whole runtime + them.
        _ = new[]
        {
            typeof(YFex.Cqrs.Result).Assembly, typeof(IHandlerInvoker).Assembly,
            typeof(ActualLab.Fusion.IComputeService).Assembly, typeof(ActualLab.Rpc.RpcHub).Assembly,
            typeof(ActualLab.Interception.IProxy).Assembly, typeof(ActualLab.CommandR.ICommander).Assembly,
            typeof(YFex.Time.TimeReply).Assembly,
        };
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location))
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
    });

    private static string Normalize(string source)
        => source.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
