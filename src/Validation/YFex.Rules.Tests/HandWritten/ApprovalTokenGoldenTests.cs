using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace YFex.Rules.Tests.HandWritten;

/// <summary>
/// Approval token v1 goldens (cryptography 05 §2 L3, 09 §3; authorization ⚑Z12 Phase 4): <c>Golden/approval-token-v1.tsv</c> was
/// generated on 2026-10-04 by this code before the token family moves to YFex.Authorization. The moved code (and its
/// <c>ApprovalTokenVersion1Codec</c>) must keep passing the same checks: payloads byte-identical, HMAC tags identical, device
/// signatures verifying.
/// </summary>
public class ApprovalTokenGoldenTests
{
    [Fact]
    public void Current_code_reproduces_the_v1_goldens()
    {
        var golden = ApprovalTokenGolden.Load();
        var server = new HmacOverrideTokens(new Dictionary<string, byte[]> { [golden.HmacKeyId] = golden.HmacKey }, golden.HmacKeyId,
            TimeSpan.FromMinutes(15));
        using var device = ECDsa.Create();
        device.ImportSubjectPublicKeyInfo(golden.DevicePublicKey, out _);

        golden.Tokens.Should().HaveCount(5);
        foreach (var t in golden.Tokens)
        {
            var token = new OverrideToken(t.Kind, t.Approver, t.Permission, t.Fingerprint, new DateTimeOffset(t.IssuedAtTicks, TimeSpan.Zero),
                new DateTimeOffset(t.ExpiresAtTicks, TimeSpan.Zero), t.KeyId, t.Signature);
            token.SignedPayload().Should().Equal(t.Payload, t.Name);

            if (t.Kind == OverrideTokenKind.Server)
            {
                server.Issue(t.Approver, t.Permission, t.Fingerprint, token.IssuedAt).Signature.ToArray().Should().Equal(t.Signature, t.Name);
            }
            else
            {
                t.KeyId.Should().Be(golden.DeviceId);
                device.VerifyData(t.Payload, t.Signature, HashAlgorithmName.SHA256).Should().BeTrue(t.Name);
            }
        }
    }
}

/// <summary>Reads <c>Golden/approval-token-v1.tsv</c> (format in its header).</summary>
internal sealed record ApprovalTokenGolden(string HmacKeyId, byte[] HmacKey, string DeviceId, byte[] DevicePublicKey,
    IReadOnlyList<ApprovalTokenGolden.Token> Tokens)
{
    public sealed record Token(string Name, OverrideTokenKind Kind, string Approver, string Permission, ulong Fingerprint,
        long IssuedAtTicks, long ExpiresAtTicks, string KeyId, byte[] Payload, byte[] Signature);

    public static string PathOf([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Golden", "approval-token-v1.tsv");

    public static ApprovalTokenGolden Load()
    {
        string hmacId = "", deviceId = "";
        byte[] hmacKey = [], devicePublicKey = [];
        var tokens = new List<Token>();
        foreach (string line in File.ReadAllLines(PathOf()))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            switch (f[0], f[1])
            {
                case ("key", "hmac"):
                    hmacId = f[2];
                    hmacKey = Convert.FromHexString(f[3]);
                    break;
                case ("key", "device"):
                    deviceId = f[2];
                    devicePublicKey = Convert.FromHexString(f[4]);
                    break;
                case ("token", _):
                    tokens.Add(new Token(f[1], Enum.Parse<OverrideTokenKind>(f[2]), f[3], f[4], Convert.ToUInt64(f[5], 16), long.Parse(f[6]),
                        long.Parse(f[7]), f[8], Convert.FromHexString(f[9]), Convert.FromHexString(f[10])));
                    break;
            }
        }
        return new ApprovalTokenGolden(hmacId, hmacKey, deviceId, devicePublicKey, tokens);
    }
}
