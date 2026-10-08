using YFex.Cryptography;

namespace YFex.Installations;

/// <summary>Library-owned authority roles, independent of hardware, operating system or business domain.</summary>
public enum InstallationRole : byte { Central = 1, SiteBroker = 2, Terminal = 3 }

/// <summary>An installation that has not completed enrollment.</summary>
public sealed record InstallationNotEnrolled;

/// <summary>An enrolled installation and the validity of its transport certificate.</summary>
public sealed record InstallationEnrolled(ValidityPeriod Certificate);

/// <summary>An installation paired provisionally by a site broker until central confirms enrollment.</summary>
public sealed record InstallationProvisional(InstallationId PairedBy, DateTimeOffset ExpiresAt);

/// <summary>An installation revoked by an authority. Historical evidence is evaluated by the runtime.</summary>
public sealed record InstallationRevoked(DateTimeOffset RevokedAt, InstallationRevokedBy By);

/// <summary>The authority that issued a revocation.</summary>
public enum InstallationRevokedBy : byte { Central = 1, SiteBroker = 2 }

/// <summary>Enrollment lifecycle states; each state carries only its applicable data.</summary>
public readonly union InstallationStatus(InstallationNotEnrolled, InstallationEnrolled, InstallationProvisional, InstallationRevoked);

/// <summary>Immutable identity metadata. Scope is canonical tenancy text, interpreted by the runtime.</summary>
/// <remarks>DisplayName is for display only. Possessing this value does not prove enrollment or authority.</remarks>
public sealed record InstallationIdentity(InstallationId Id, InstallationRole Role, string Scope,
    string DisplayName, InstallationStatus Status);
