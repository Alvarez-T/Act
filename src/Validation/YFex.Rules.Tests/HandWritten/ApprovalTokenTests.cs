using System.Security.Cryptography;

namespace YFex.Rules.Tests.HandWritten;

/// <summary>D34: online HMAC approvals + capped, flagged offline device approvals.</summary>
public class ApprovalTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(-3));

    private static Decision<HSale> NeedsApproval(decimal discount)
        => HandSaleRules.Evaluate(HSaleBuilder.Default with { ManualDiscount = Percentual.FromPercentage(discount) });

    private static HmacOverrideTokens Hmac()
        => new(new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) }, "k1", TimeSpan.FromMinutes(15));

    [Fact]
    public void Server_token_is_bound_to_the_fingerprint_permission_and_expiry()
    {
        var d = NeedsApproval(8);
        var request = d.Approvals[0];
        var tokens = Hmac();
        var token = tokens.Issue("mgr42", request.Permission.Value, d.Fingerprint, Now);

        tokens.Verify(in token, in request, d.Fingerprint, Now.AddMinutes(1)).Should().Be(new ApprovalVerification(ApprovalCheck.Valid));
        tokens.Verify(in token, in request, d.Fingerprint, Now.AddMinutes(16)).Check.Should().Be(ApprovalCheck.Expired);

        var changed = NeedsApproval(12);                                     // seller raised the discount after the approval
        tokens.Verify(in token, changed.Approvals[0], changed.Fingerprint, Now).Check.Should().Be(ApprovalCheck.FingerprintMismatch);

        var forged = token with { ApproverId = "seller7" };
        tokens.Verify(in forged, in request, d.Fingerprint, Now).Check.Should().Be(ApprovalCheck.InvalidSignature);
    }

    [Fact]
    public void Device_token_is_capped_and_always_flagged()
    {
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Keys { ["pos-7"] = deviceKey.ExportSubjectPublicKeyInfo() };
        var verifier = new CompositeOverrideTokenVerifier(Hmac(),
            new DeviceOverrideTokenVerifier(keys, [new OfflineApprovalCap("sales.discount.override", RawValue.From(Percentual.FromPercentage(10)), TimeSpan.FromDays(2))]));
        var device = new DeviceOverrideTokenIssuer("pos-7", deviceKey, TimeSpan.FromMinutes(15));

        var within = NeedsApproval(8);
        var t1 = device.Issue("mgr42", "sales.discount.override", within.Fingerprint, Now);
        verifier.Verify(in t1, within.Approvals[0], within.Fingerprint, Now.AddHours(5))            // replayed hours later
            .Should().Be(new ApprovalVerification(ApprovalCheck.Valid, Flagged: true));

        var over = NeedsApproval(12);                                                                // above the offline cap (10%)
        var t2 = device.Issue("mgr42", "sales.discount.override", over.Fingerprint, Now);
        verifier.Verify(in t2, over.Approvals[0], over.Fingerprint, Now).Check.Should().Be(ApprovalCheck.OverOfflineCap);

        verifier.Verify(in t1, within.Approvals[0], within.Fingerprint, Now.AddDays(3)).Check.Should().Be(ApprovalCheck.Expired);

        keys.Remove("pos-7");                                                                        // device revoked
        verifier.Verify(in t1, within.Approvals[0], within.Fingerprint, Now).Check.Should().Be(ApprovalCheck.InvalidSignature);
    }

    private sealed class Keys : Dictionary<string, byte[]>, IDeviceKeyStore
    {
        public byte[]? GetPublicKey(string deviceId) => TryGetValue(deviceId, out var k) ? k : null;
    }
}
