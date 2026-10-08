using Microsoft.Extensions.DependencyInjection;
using YFex.Time;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// Time T2: the central anchor over a real client↔server RPC round trip (two hubs, MemoryPack wire). The server answers with its
/// trusted clock; the client's trusted clock anchors to it through <see cref="CentralTimeSource"/>.
/// </summary>
public class CentralTimeTests
{
    [Fact]
    public async Task Central_with_a_trusted_clock_answers_as_an_anchored_server()
    {
        await using var rpc = new TimeFixture(server => server.AddYFexTime(time => time.TrustDeviceClock(stratum: 2)));
        var central = rpc.ClientServices.GetRequiredService<ICentralTime>();

        DateTimeOffset before = DateTimeOffset.UtcNow;
        byte[] answer = await central.GetTimeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        DateTimeOffset after = DateTimeOffset.UtcNow;

        CentralTimeWire.Read(answer, out TimeReply reply).Should().BeTrue();
        reply.Stratum.Should().Be(2);
        reply.Flags.Should().Be(TimeFlags.None);
        reply.Received.Should().BeOnOrAfter(before.AddMilliseconds(-50)).And.BeOnOrBefore(reply.Sent);
        reply.Sent.Should().BeOnOrBefore(after.AddMilliseconds(50));
        reply.Uncertainty.Should().Be(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task Client_trusted_clock_anchors_to_central_over_rpc()
    {
        await using var rpc = new TimeFixture(server => server.AddYFexTime(time => time.TrustDeviceClock(stratum: 2)));
        var clock = new TrustedTimeProvider();
        var source = new CentralTimeSource(rpc.ClientServices.GetRequiredService<ICentralTime>(), authenticated: true);
        using var synchronizer = new TimeSynchronizer(clock, [source], new TimeOptions { SampleTimeout = TimeSpan.FromSeconds(10) });

        TrustedInstant now = await synchronizer.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(15));

        now.IsAuthenticated.Should().BeTrue();
        now.Source.Should().Be(TimeSourceKind.CentralAnchor);
        now.Uncertainty.Should().BeLessThan(TimeSpan.FromSeconds(1));
        (now.Value - DateTimeOffset.UtcNow).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
        clock.CreateReply(clock.GetTimestamp()).Stratum.Should().Be(3);                 // central 2 → this client 3
    }

    [Fact]
    public async Task Central_without_a_trusted_clock_never_anchors_anyone()
    {
        await using var rpc = new TimeFixture(configureServer: null);
        var clock = new TrustedTimeProvider();
        var source = new CentralTimeSource(rpc.ClientServices.GetRequiredService<ICentralTime>(), authenticated: true);
        using var synchronizer = new TimeSynchronizer(clock, [source], new TimeOptions { SampleTimeout = TimeSpan.FromSeconds(10) });

        TrustedInstant now = await synchronizer.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(15));

        now.IsAnchored.Should().BeFalse();                                              // stratum 16 → evidence only
    }

    [Fact]
    public async Task Disconnected_client_has_no_answer_without_waiting_for_a_reconnect()
    {
        var central = new ThrowingCentral();
        var source = new CentralTimeSource(central, authenticated: true, new OfflineServer());

        TimeSample? sample = await source.SampleAsync(TimeProvider.System, CancellationToken.None);

        sample.Should().BeNull();
        central.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("wss://api.farma.com.br", true)]
    [InlineData("ws://localhost:5000", false)]
    public void Central_anchor_is_authenticated_only_over_tls(string endpoint, bool authenticated)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new YFexMessagingRpcClientOptions { WebSocketEndpoint = new Uri(endpoint) });
        services.AddSingleton<ICentralTime>(new ThrowingCentral());
        services.AddYFexTime(time => time.UseCentralAnchor());
        using var provider = services.BuildServiceProvider();

        ITimeSource source = provider.GetRequiredService<TimeSynchronizer>().Sources.Single();

        source.Should().BeOfType<CentralTimeSource>();
        source.IsAuthenticated.Should().Be(authenticated);
        source.Kind.Should().Be(TimeSourceKind.CentralAnchor);
    }

    [Fact]
    public void Wire_form_round_trips_and_rejects_garbage()
    {
        var reply = new TimeReply(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 5, 12, 0, 0, 3, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(15), TimeSourceKind.System, TimeFlags.AuthenticatedFloor, 3);

        byte[] bytes = CentralTimeWire.Write(reply);

        bytes.Should().HaveCount(28);
        bytes[0].Should().Be(1);
        CentralTimeWire.Read(bytes, out TimeReply read).Should().BeTrue();
        read.Should().Be(reply);
        CentralTimeWire.Read(bytes.AsSpan(0, 27), out _).Should().BeFalse();
        bytes[0] = 2;
        CentralTimeWire.Read(bytes, out _).Should().BeFalse();
        byte[] outOfRange = CentralTimeWire.Write(reply);
        BitConverter.TryWriteBytes(outOfRange.AsSpan(1), long.MaxValue);
        CentralTimeWire.Read(outOfRange, out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_invalid_answer_is_no_sample()
    {
        var source = new CentralTimeSource(new GarbageCentral(), authenticated: true);

        (await source.SampleAsync(TimeProvider.System, CancellationToken.None)).Should().BeNull();
    }

    private sealed class TimeFixture(Action<IServiceCollection>? configureServer)
        : RpcFixture("alice", Users.Acme, serverAuthorization: null, configureServer);

    private sealed class OfflineServer : IServerConnection
    {
        public ServerConnectionState State => ServerConnectionState.Disconnected;
        public event Action<ServerConnectionChange>? Changed { add { } remove { } }
    }

    private sealed class GarbageCentral : ICentralTime
    {
        public Task<byte[]> GetTimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 9, 9, 9 });
    }

    private sealed class ThrowingCentral : ICentralTime
    {
        public int Calls { get; private set; }

        public Task<byte[]> GetTimeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("must not be called");
        }
    }
}
