using System.Text;
using System.Text.Json;
using YFex.Reasons;

namespace YFex.Tests.Reasons;

/// <summary><see cref="WhyKey"/>: grammar, scheme / id, parse and format round trips, JSON (validation 14 §3.5, ⚑WH1).</summary>
public class WhyKeyTests
{
    [Fact]
    public void Create_joins_scheme_and_id()
    {
        var key = WhyKey.Create("rule", "sale.installments#7f3c0a");

        key.Value.Should().Be("rule:sale.installments#7f3c0a");
        key.Scheme.ToString().Should().Be("rule");
        key.Id.ToString().Should().Be("sale.installments#7f3c0a");
        key.HasScheme("rule").Should().BeTrue();
        key.HasScheme("stage").Should().BeFalse();
        key.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Ids_may_contain_colons_and_slashes()
    {
        var key = new WhyKey("cond:sale/loyalty.gold:2");
        key.Scheme.ToString().Should().Be("cond");
        key.Id.ToString().Should().Be("sale/loyalty.gold:2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("rule")]
    [InlineData(":id")]
    [InlineData("rule:")]
    [InlineData("Rule:x")]
    [InlineData("ru le:x")]
    [InlineData("rule:a b")]
    [InlineData("rule:a\tb")]
    public void Malformed_text_is_rejected(string text)
    {
        WhyKey.TryCreate(text, out var key).Should().BeFalse();
        key.IsEmpty.Should().BeTrue();
        WhyKey.TryParse(text.AsSpan(), null, out _).Should().BeFalse();
        FluentActions.Invoking(() => new WhyKey(text)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_validates_both_parts()
    {
        FluentActions.Invoking(() => WhyKey.Create("Rule", "x")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => WhyKey.Create("rule", "")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_and_format_round_trip()
    {
        var key = WhyKey.Create("stage", "sale/payment");

        WhyKey.Parse(key.ToString()).Should().Be(key);
        WhyKey.Parse(key.ToString().AsSpan()).Should().Be(key);
        $"{key}".Should().Be("stage:sale/payment");

        Span<char> chars = stackalloc char[32];
        key.TryFormat(chars, out int written, default, null).Should().BeTrue();
        chars[..written].ToString().Should().Be("stage:sale/payment");
        key.TryFormat(chars[..4], out _, default, null).Should().BeFalse();

        Span<byte> utf8 = stackalloc byte[32];
        key.TryFormat(utf8, out int bytes, default, null).Should().BeTrue();
        Encoding.UTF8.GetString(utf8[..bytes]).Should().Be("stage:sale/payment");

        FluentActions.Invoking(() => WhyKey.Parse("nope".AsSpan())).Should().Throw<FormatException>();
    }

    [Fact]
    public void Equality_is_ordinal_and_default_is_empty()
    {
        WhyKey.Create("group", "sale/Installments").Should().Be(new WhyKey("group:sale/Installments"));
        WhyKey.Create("group", "sale/Installments").Should().NotBe(new WhyKey("group:sale/installments"));
        default(WhyKey).Value.Should().BeEmpty();
        default(WhyKey).Scheme.IsEmpty.Should().BeTrue();
        default(WhyKey).HasScheme("rule").Should().BeFalse();
    }

    [Fact]
    public void Json_is_the_stable_text()
    {
        var key = WhyKey.Create("rule", "loyalty.gold");
        string json = JsonSerializer.Serialize(key);
        json.Should().Be("\"rule:loyalty.gold\"");
        JsonSerializer.Deserialize<WhyKey>(json).Should().Be(key);
        JsonSerializer.Serialize(default(WhyKey)).Should().Be("null");
        FluentActions.Invoking(() => JsonSerializer.Deserialize<WhyKey>("\"bad\"")).Should().Throw<JsonException>();
    }
}
