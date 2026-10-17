using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs.Configuration;
using YFex.Cryptography;
using YFex.Messaging.Rpc;

namespace YFex.Cqrs;

/// <summary>
/// Approvals of operations (authorization-consolidation 06 §1, decisions A2/A5): a supervisor approves ONE operation, named by its digest,
/// for a permission they hold. The digest comes from the <see cref="ErrorKind.ApprovalRequired"/> answer of the operation itself (a rules
/// decision digest, or <c>.RequireApproval</c>'s message type + bound value). Executed with the APPROVER's credentials; the client puts the
/// returned token into the operation's <see cref="IApprovalBound.Approvals"/> and resends it.
/// <code>
/// // cashier: VoidSale → ApprovalRequired(sales.void.approve, digest D)
/// // supervisor (same terminal, own login, or remote):
/// ApprovalToken token = await bus.CommandAsync&lt;Approvals.Commands.Approve, ApprovalToken&gt;(new(SalesPermissions.SaleVoidApprove, d));
/// // cashier resends VoidSale with Approvals = [token]
/// </code>
/// </summary>
public partial class Approvals
{
    public static partial class Commands
    {
        /// <summary>
        /// Issues a server <see cref="ApprovalToken"/> for <see cref="Permission"/> bound to <see cref="Digest"/>. The caller must hold the
        /// permission in its scope (the gate checks it) and must not be impersonating or acting on someone's behalf.
        /// </summary>
        [MemoryPackable]
        public sealed partial record Approve([property: MemoryPackAllowSerialize] Permission Permission, [property: MemoryPackAllowSerialize] Hash256 Digest)
            : ICommand<ApprovalToken>;
    }
}

/// <summary>The approver must hold the permission the message names (an empty or unknown id is never held).</summary>
public sealed class ApproveRequirement : IResourcePermissionRequirement<Approvals.Commands.Approve>
{
    Permission? IResourcePermissionRequirement<Approvals.Commands.Approve>.PermissionFor(Approvals.Commands.Approve command)
        => command.Permission;
}

/// <summary>Baseline (client + server): <c>Approve</c> needs an authenticated caller holding the named permission.</summary>
public sealed class ApprovalsBaseline : IAggregateConfiguration<Approvals>
{
    public void Configure(AggregateConfigurationBuilder<Approvals> b)
        => b.Command<Approvals.Commands.Approve, ApprovalToken>().RequireAuthenticated().Authorize<ApproveRequirement>();
}

/// <summary>Server handler of <see cref="Approvals.Commands.Approve"/>.</summary>
public sealed class ApproveHandler(IApprovalTokenIssuer issuer, IPrincipalAccessor principal) : ICommandHandler<Approvals.Commands.Approve, ApprovalToken>
{
    public async Task<ApprovalToken> HandleAsync(Approvals.Commands.Approve command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        // The gate already checked that the APPROVER holds the permission (ApproveRequirement).
        var caller = principal.Current;
        if (!caller.IsAuthenticated || caller.Actor.IsAnonymous)
            throw new ErrorResultException(new Unauthenticated("Approver identity unknown."));
        // Issuing approvals is restricted while impersonating or acting for someone else (06 §3).
        if (caller.Relation != ActorRelation.None)
            throw new ErrorResultException(new Forbidden(new Denial(DenialKind.RestrictedWhileImpersonating, command.Permission)));
        if (command.Digest.IsZero)
            throw new ErrorResultException(new Failed("An approval needs the digest of the operation it approves."));
        return await issuer.IssueAsync(new ApprovalGrant(caller.Actor.Subject, caller.Scope, command.Permission, command.Digest), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Registration of <see cref="Approvals"/>.</summary>
public static class CqrsApprovalsExtensions
{
    /// <summary>
    /// Server: the approval tokens (<c>AddYFexApprovals</c>: issuer, verifier, options), the <see cref="ApproveHandler"/> and the gate
    /// requirement. Build the messaging registry with <see cref="ApprovalsBaseline"/> and add the in-process invoker
    /// (<see cref="AddApprovals"/>) or the RPC server.
    /// <code>
    /// services.AddYFexCqrsApprovals(a =&gt; a.AllowOffline(SalesPermissions.DiscountOverride, TimeSpan.FromHours(48)));
    /// </code>
    /// </summary>
    public static IServiceCollection AddYFexCqrsApprovals(this IServiceCollection services, Action<ApprovalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddYFexApprovals(configure);
        services.AddYFexApprovalRequirements();
        services.TryAddTransient<ICommandHandler<Approvals.Commands.Approve, ApprovalToken>, ApproveHandler>();
        return services;
    }

    /// <summary>The gate requirement of <see cref="ApprovalsBaseline"/> (client and server registries both carry the baseline). Idempotent.</summary>
    public static IServiceCollection AddYFexApprovalRequirements(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ApproveRequirement>();
        return services;
    }

    /// <summary>In-process invoker of <see cref="Approvals.Commands.Approve"/> (LocalDispatcher hosts).</summary>
    public static LocalHandlerInvokerRegistryBuilder AddApprovals(this LocalHandlerInvokerRegistryBuilder builder)
        => builder.AddCommand<Approvals.Commands.Approve, ApprovalToken>();
}
