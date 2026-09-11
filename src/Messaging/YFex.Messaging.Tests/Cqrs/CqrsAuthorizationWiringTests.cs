using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;
using SClaim = System.Security.Claims.Claim;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>
/// Composition-root wiring: <c>AddYFexCqrsAuthorization()</c> rebuilds the CompiledMessagingRegistry
/// with the evaluator + principal factory so <c>RequirePermission</c> actually resolves (rather than
/// failing closed).
/// </summary>
[Trait("Category", "Cqrs")]
public sealed class CqrsAuthorizationWiringTests
{
    private sealed class DeleteNeedsEdit : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>()
                 .RequirePermission(Permission.Parse("orders.edit"));
    }

    private static ClaimsPrincipal Principal(params string[] permissions)
        => new(new ClaimsIdentity(permissions.Select(p => new SClaim("permission", p)), "test"));

    private static Func<ClaimsPrincipal, object, bool> BuildGate()
    {
        var services = new ServiceCollection();
        services.AddYFexAuthorization(_ => { });                        // evaluator + default provider + factory
        services.AddSingleton<IAggregateConfiguration, DeleteNeedsEdit>();
        services.AddYFexCqrsAuthorization();                            // the helper under test

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<CompiledMessagingRegistry>();
        return registry.Commands[typeof(TestAggregate.Commands.DeleteCommand)].Authorize!;
    }

    [Fact]
    public void Wiring_allows_when_principal_has_permission()
    {
        var gate = BuildGate();
        gate(Principal("orders.edit"), new TestAggregate.Commands.DeleteCommand(1)).Should().BeTrue();
    }

    [Fact]
    public void Wiring_denies_when_principal_lacks_permission()
    {
        var gate = BuildGate();
        gate(Principal(), new TestAggregate.Commands.DeleteCommand(1)).Should().BeFalse();
    }
}
