namespace YFex.Cryptography;

/// <summary>
/// Signature algorithm written as one byte in every signed format (crypto agility, cryptography 05 §8, 08). Closed set owned by
/// YFex.Cryptography; values are never reused. <b>Type only</b> (⚑K2): signing and verification live in the library.
/// </summary>
public enum SignatureAlgorithm : byte
{
    /// <summary>ECDsa over NIST P-256 with SHA-256, IEEE P1363 encoding (64 bytes). The default.</summary>
    EcdsaP256Sha256 = 1,

    /// <summary>
    /// RSASSA-PKCS1-v1_5 with SHA-256 (JWA <c>RS256</c>), <b>verify only</b>: for public keys published by someone else (OpenID providers,
    /// passkeys). YFex never signs with RSA; verifiers accept it only when it is in their allowed algorithms.
    /// </summary>
    RsaPkcs1Sha256 = 2,

    /// <summary>RSASSA-PSS with SHA-256, MGF1-SHA-256 and a 32-byte salt (JWA <c>PS256</c>), <b>verify only</b> like <see cref="RsaPkcs1Sha256"/>.</summary>
    RsaPssSha256 = 3,

    /// <summary>Reserved (cryptography ⚑K10): ML-DSA-65 (FIPS 204), 3 309-byte signatures. Not implemented yet.</summary>
    MLDsa65 = 16,

    /// <summary>Reserved (cryptography ⚑K10): composite ML-DSA-65 + ECDsa P-256 (hybrid). Not implemented yet.</summary>
    CompositeMLDsa65EcdsaP256 = 17,
}

/// <summary>
/// A signature that travels apart from what it signs: bundles, envelopes, anchors, receipts, approval tokens. The signed bytes
/// are always <c>SHA-256(domain ‖ 0x00 ‖ canonical payload)</c> (cryptography 05 §5); the domain is never on the wire.
/// <b>Type only</b> (⚑K2).
/// </summary>
/// <param name="Algorithm">How <paramref name="Value"/> was produced.</param>
/// <param name="Key">The exact signing key version (look it up in a public-key directory).</param>
/// <param name="Value">The signature bytes (64 for <see cref="SignatureAlgorithm.EcdsaP256Sha256"/>).</param>
public readonly record struct DetachedSignature(SignatureAlgorithm Algorithm, KeyId Key, ReadOnlyMemory<byte> Value);
