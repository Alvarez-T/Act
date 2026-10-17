using Microsoft.Extensions.DependencyInjection;
using YFex.Cryptography;
using YFex.Cryptography.Testing;
using YFex.Time;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// Time T4, signed central anchor: central signs its <see cref="TimeReply"/> over the client's nonce; the client accepts only replies
/// its directory of central's keys verifies. Real client↔server RPC round trips (two hubs, MemoryPack wire).
/// </summary>
public class SignedCentralTimeTests
{
    private static readonly KeyRingName Ring = new(CentralTimeSigner.DefaultRing);

    [Fact]
    public async Task Signed_reply_anchors_the_client_and_stays_verifiable_as_evidence()
    {
        await using var rpc = new TimeFixture(SigningCentral);
        SigningKeyRing centralKeys = await rpc.ServerServices.GetRequiredService<KeyRingProvider>().GetSigningKeysAsync(Ring, default);
        var source = new CentralTimeSource(rpc.ClientServices.GetRequiredService<ICentralTime>(), Verifier(centralKeys));
        var clock = new TrustedTimeProvider();
        using var synchronizer = new TimeSynchronizer(clock, [source], new TimeOptions { SampleTimeout = TimeSpan.FromSeconds(10) });

        TrustedInstant now = await synchronizer.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(15));

        now.IsAuthenticated.Should().BeTrue();
        now.Source.Should().Be(TimeSourceKind.CentralAnchor);
        (now.Value - DateTimeOffset.UtcNow).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
        source.RequiresSignature.Should().BeTrue();
        SignedTimeReply evidence = source.LastSignedReply!;
        evidence.Signature.Key.Should().Be(centralKeys.Current.Id);
        SignedTimeReply.TryDecode(evidence.Encode(), out SignedTimeReply stored).Should().BeTrue();   // kept, read back later
        stored.Verify(Verifier(centralKeys)).Should().Be(SignatureCheck.Valid);
    }

    [Fact]
    public async Task Central_without_a_signing_key_gives_no_signed_sample()
    {
        await using var rpc = new TimeFixture(server => server.AddYFexTime(time => time.TrustDeviceClock()));
        var source = new CentralTimeSource(rpc.ClientServices.GetRequiredService<ICentralTime>(), Verifier(OtherKeys()));

        (await source.SampleAsync(TimeProvider.System, default).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
        (await rpc.ClientServices.GetRequiredService<ICentralTime>().GetSignedTimeAsync(new byte[32])).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reply_signed_by_an_unknown_key_is_no_sample()
    {
        await using var rpc = new TimeFixture(SigningCentral);
        var source = new CentralTimeSource(rpc.ClientServices.GetRequiredService<ICentralTime>(), Verifier(OtherKeys()));

        (await source.SampleAsync(TimeProvider.System, default).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
        source.LastSignedReply.Should().BeNull();
    }

    [Fact]
    public async Task A_replayed_reply_for_another_nonce_is_no_sample()
    {
        using var key = InMemorySigningKey.Generate();
        var reply = new TimeReply(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10), TimeSourceKind.System,
            TimeFlags.None, 2);
        byte[] recorded = SignedTimeReply.Sign(key, new byte[32], reply).Encode();             // a genuine reply, captured earlier
        var source = new CentralTimeSource(new ReplayingCentral(recorded), Verifier(new SingleKey(key)));

        (await source.SampleAsync(TimeProvider.System, default)).Should().BeNull();
    }

    [Fact]
    public void Tampered_time_fails_the_signature_and_malformed_forms_do_not_decode()
    {
        using var key = InMemorySigningKey.Generate();
        var reply = new TimeReply(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 14, 12, 0, 0, 2,
            TimeSpan.Zero), TimeSpan.FromMilliseconds(10), TimeSourceKind.System, TimeFlags.None, 2);
        byte[] nonce = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        SignedTimeReply signed = SignedTimeReply.Sign(key, nonce, reply);
        var verifier = Verifier(new SingleKey(key));

        signed.Verify(verifier).Should().Be(SignatureCheck.Valid);
        (signed with { Reply = reply with { Sent = reply.Sent.AddDays(-1) } }).Verify(verifier).Should().Be(SignatureCheck.Invalid);
        (signed with { Nonce = new byte[32] }).Verify(verifier).Should().Be(SignatureCheck.Invalid);

        byte[] encoded = signed.Encode();
        encoded[0].Should().Be(SignedTimeReply.FormatVersion);
        SignedTimeReply.TryDecode(encoded, out SignedTimeReply read).Should().BeTrue();
        read.Reply.Should().Be(reply);
        read.Nonce.ToArray().Should().Equal(nonce);
        SignedTimeReply.TryDecode(encoded.AsSpan(0, encoded.Length - 1), out _).Should().BeFalse();
        SignedTimeReply.TryDecode([.. encoded, 0], out _).Should().BeFalse();
        SignedTimeReply.TryDecode(CentralTimeWire.Write(reply), out _).Should().BeFalse();   // the unsigned version-1 form
        FluentActions.Invoking(() => SignedTimeReply.Sign(key, new byte[8], reply)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Builder_creates_a_signed_source()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICentralTime>(new ReplayingCentral([]));
        services.AddYFexTime(time => time.UseSignedCentralAnchor(_ => OtherKeys()));
        using ServiceProvider provider = services.BuildServiceProvider();

        var source = (CentralTimeSource)provider.GetRequiredService<TimeSynchronizer>().Sources.Single();

        source.RequiresSignature.Should().BeTrue();
        source.IsAuthenticated.Should().BeTrue();
    }

    private static void SigningCentral(IServiceCollection server)
    {
        server.AddYFexTime(time => time.TrustDeviceClock(stratum: 2));
        server.AddYFexCryptography(cryptography => cryptography.StoreKeyRingsIn(_ => new InMemoryKeyRingStore()));
        server.AddCentralTimeSigning();
    }

    private static SignatureVerifier Verifier(IPublicKeyDirectory keys) => new(keys, [SignatureAlgorithm.EcdsaP256Sha256]);

    private static IPublicKeyDirectory OtherKeys() => new SingleKey(InMemorySigningKey.Generate());

    private sealed class SingleKey(ISigningKey key) : IPublicKeyDirectory
    {
        public bool TryFind(KeyId id, out PublicKeyRecord record)
        {
            record = new PublicKeyRecord(key.Id, key.Algorithm, key.SubjectPublicKeyInfo, ValidityPeriod.Starting(DateTimeOffset.MinValue), RevokedAt: null);
            return id == key.Id;
        }
    }

    private sealed class ReplayingCentral(byte[] answer) : ICentralTime
    {
        public Task<byte[]> GetTimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> GetSignedTimeAsync(byte[] nonce, CancellationToken cancellationToken = default) => Task.FromResult(answer);
    }

    private sealed class TimeFixture(Action<IServiceCollection>? configureServer)
        : RpcFixture("alice", Users.Acme, serverAuthorization: null, configureServer);
}
