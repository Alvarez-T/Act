using Xunit;
using YFex.Installations;

namespace YFex.Tests.Installations;

[CollectionDefinition("Local installation", DisableParallelization = true)]
public sealed class LocalInstallationCollection;

[Collection("Local installation")]
public sealed class InstallationIdentityTests
{
    private static InstallationIdentity Identity() => new(new(Guid.NewGuid()), InstallationRole.Terminal,
        "", "CAIXA-02", new InstallationNotEnrolled());

    [Fact]
    public void Lifecycle_data_is_available_through_exhaustive_union_matching()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        InstallationStatus[] statuses = [new InstallationNotEnrolled(), new InstallationEnrolled(new(now, now.AddDays(90))),
            new InstallationProvisional(new(Guid.NewGuid()), now.AddHours(72)), new InstallationRevoked(now, InstallationRevokedBy.Central), default];
        string[] descriptions = statuses.Select(status => status switch
        {
            InstallationNotEnrolled => "not enrolled",
            InstallationEnrolled enrolled => enrolled.Certificate.Duration == TimeSpan.FromDays(90) ? "enrolled" : "wrong validity",
            InstallationProvisional provisional => provisional.ExpiresAt == now.AddHours(72) ? "provisional" : "wrong expiry",
            InstallationRevoked revoked => revoked.By == InstallationRevokedBy.Central ? "revoked" : "wrong issuer",
            null => "unset"
        }).ToArray();
        Assert.Equal(["not enrolled", "enrolled", "provisional", "revoked", "unset"], descriptions);
    }

    [Fact]
    public void Publish_notifies_once_per_value_change_after_updating_the_snapshot()
    {
        var identity = Identity();
        var received = new List<InstallationIdentity>();
        void OnChanged(InstallationIdentity changed)
        {
            Assert.Same(changed, LocalInstallation.Current);
            received.Add(changed);
        }
        LocalInstallation.Changed += OnChanged;
        try
        {
            LocalInstallation.Publish(identity);
            LocalInstallation.Publish(identity with { });
            LocalInstallation.Publish(identity with { Status = new InstallationNotEnrolled() });
            var enrolled = identity with { Scope = "0=farmaviva/1=loja-centro",
                Status = new InstallationEnrolled(new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(90))) };
            LocalInstallation.Publish(enrolled);
            Assert.Equal(2, received.Count);
            Assert.Equal(identity.Id, received[1].Id);
        }
        finally { LocalInstallation.Changed -= OnChanged; }
    }

    [Fact]
    public void Concurrent_equal_publications_raise_one_event()
    {
        var identity = Identity();
        int count = 0;
        void OnChanged(InstallationIdentity _) => Interlocked.Increment(ref count);
        LocalInstallation.Changed += OnChanged;
        try
        {
            Parallel.For(0, 128, _ => LocalInstallation.Publish(identity with { }));
            Assert.Equal(1, count);
            Assert.Equal(identity, LocalInstallation.Current);
        }
        finally { LocalInstallation.Changed -= OnChanged; }
    }

    [Fact]
    public void Null_publication_does_not_clear_the_identity()
    {
        var identity = Identity();
        LocalInstallation.Publish(identity);
        Assert.Throws<ArgumentNullException>(() => LocalInstallation.Publish(null!));
        Assert.Same(identity, LocalInstallation.Current);
    }

    [Fact]
    public void Observer_can_publish_a_followup_without_a_deadlock()
    {
        var identity = Identity();
        var renamed = identity with { DisplayName = "CAIXA-02-RENAMED" };
        int count = 0;
        void OnChanged(InstallationIdentity changed)
        {
            count++;
            if (changed.DisplayName == identity.DisplayName) LocalInstallation.Publish(renamed);
        }
        LocalInstallation.Changed += OnChanged;
        try
        {
            LocalInstallation.Publish(identity);
            Assert.Equal(2, count);
            Assert.Same(renamed, LocalInstallation.Current);
        }
        finally { LocalInstallation.Changed -= OnChanged; }
    }

    [Fact]
    public void Observer_failure_does_not_roll_back_a_published_identity()
    {
        var identity = Identity();
        void OnChanged(InstallationIdentity _) => throw new InvalidOperationException("observer");
        LocalInstallation.Changed += OnChanged;
        try
        {
            Assert.Throws<InvalidOperationException>(() => LocalInstallation.Publish(identity));
            Assert.Same(identity, LocalInstallation.Current);
        }
        finally { LocalInstallation.Changed -= OnChanged; }
    }
}
