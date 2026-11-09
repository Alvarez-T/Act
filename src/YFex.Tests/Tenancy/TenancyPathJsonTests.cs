using System.Text.Json;
using YFex.Tenancy;

namespace YFex.Tests.Tenancy;

public sealed class TenancyPathJsonTests
{
    private sealed record Holder(TenancyPath Home, TenancyPath Other);

    [Fact]
    public void Serializes_as_the_text_and_none_as_null()
    {
        var holder = new Holder(TenancyPath.Parse("0=acme/1=loja%2F7"), TenancyPath.None);
        string json = JsonSerializer.Serialize(holder);
        json.Should().Be("{\"Home\":\"0=acme/1=loja%2F7\",\"Other\":null}");
        JsonSerializer.Deserialize<Holder>(json).Should().Be(holder);
    }

    [Fact]
    public void Rejects_a_non_canonical_text()
        => FluentActions.Invoking(() => JsonSerializer.Deserialize<Holder>("{\"Home\":\"01=acme\",\"Other\":null}"))
            .Should().Throw<JsonException>();
}
