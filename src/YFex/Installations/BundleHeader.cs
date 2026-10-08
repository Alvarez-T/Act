using YFex.Cryptography;

namespace YFex.Installations;

/// <summary>An application-extensible bundle kind. Its owner defines the payload and its meaning.</summary>
public readonly record struct BundleKind(string Name);

/// <summary>The kind of audience addressed by a bundle.</summary>
public enum BundleAudienceKind : byte { Installation = 1, Scope = 2, Tenant = 3 }

/// <summary>Installation id text or canonical scope text, interpreted and validated by the runtime.</summary>
public readonly record struct BundleAudience(BundleAudienceKind Kind, string Value);

/// <summary>
/// Metadata authenticated by a bundle signature. Byte encoding, hashing and verification belong to the runtime.
/// </summary>
public readonly record struct BundleHeader(BundleKind Kind, string Stream, long Version, BundleAudience Audience,
    DateTimeOffset IssuedAt, DateTimeOffset ValidUntil, KeyId KeyId, Hash256 PayloadHash);
