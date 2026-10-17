using Microsoft.Extensions.DependencyInjection;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// <see cref="CallContextClientHeaders"/> (added for Authorization Phase 3b): the headers <see cref="FusionMessageBus"/> sends, available to
/// plain proxy calls (the snapshot issuer) so they carry the token and the installation assertion too.
/// </summary>
public sealed class CallContextClientHeadersTests
{
    [Fact]
    public async Task Without_a_call_context_there_are_no_headers()
    {
        var headers = new CallContextClientHeaders(new ServiceCollection().BuildServiceProvider());

        (await headers.GetAsync()).Should().BeNull();
        (await headers.CallAsync(() => Task.FromResult(42))).Should().Be(42);
    }

    [Fact]
    public async Task The_token_and_extra_headers_are_built_per_call()
    {
        var builder = new CallContextClientBuilder()
            .SendToken((_, _) => ValueTask.FromResult<string?>("token-1"))
            .SendHeader("yfex-installation", (_, _) => ValueTask.FromResult<string?>("assertion"))
            .SendHeader("x-empty", (_, _) => ValueTask.FromResult<string?>(null));
        var services = new ServiceCollection().AddSingleton(builder).BuildServiceProvider();

        var headers = (await new CallContextClientHeaders(services).GetAsync())!;

        headers.Select(header => (header.Name, header.Value)).Should().Equal(
            (CallContextHeaders.Authorization, "Bearer token-1"), ("yfex-installation", "assertion"));
    }
}
