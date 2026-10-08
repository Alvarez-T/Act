using System.Security.Cryptography;
using System.Text;
using YFex.Cryptography;

namespace YFex.Tests.Cryptography;

public sealed class Hash256Tests
{
    // FIPS 180-4 example: SHA-256("abc")
    private const string AbcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static Hash256 Abc()
    {
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        SHA256.HashData("abc"u8, bytes);
        return new Hash256(bytes);
    }

    [Fact]
    public void Wraps_a_digest_and_formats_it_as_lower_case_hex()
    {
        Hash256 digest = Abc();
        digest.ToString().Should().Be(AbcHex);
        digest.ToString("S", null).Should().Be(AbcHex[..Hash256.ShortHexLength]);
        digest.AsSpan().ToArray().Should().Equal(Convert.FromHexString(AbcHex));
    }

    [Fact]
    public void Formats_into_char_and_utf8_spans_without_strings()
    {
        Hash256 digest = Abc();
        Span<char> chars = stackalloc char[Hash256.HexLength];
        digest.TryFormat(chars, out int written, default, null).Should().BeTrue();
        new string(chars[..written]).Should().Be(AbcHex);

        Span<byte> utf8 = stackalloc byte[Hash256.HexLength];
        digest.TryFormat(utf8, out int bytes, default, null).Should().BeTrue();
        Encoding.UTF8.GetString(utf8[..bytes]).Should().Be(AbcHex);

        digest.TryFormat(stackalloc char[Hash256.HexLength - 1], out _, default, null).Should().BeFalse();
    }

    [Fact]
    public void Parses_either_case_and_rejects_bad_text()
    {
        Hash256.Parse(AbcHex).Should().Be(Abc());
        Hash256.Parse(AbcHex.ToUpperInvariant()).Should().Be(Abc());
        Hash256.TryParse(AbcHex[..63], null, out _).Should().BeFalse();
        Hash256.TryParse(AbcHex[..63] + "g", null, out _).Should().BeFalse();
        Hash256.TryParse((string?)null, null, out _).Should().BeFalse();
        FluentActions.Invoking(() => Hash256.Parse("abc")).Should().Throw<FormatException>();
    }

    [Fact]
    public void Equality_hashing_and_fixed_time_comparison_agree()
    {
        Hash256 a = Abc();
        Hash256 b = Hash256.Read(Convert.FromHexString(AbcHex));
        Hash256 other = Hash256.Parse(AbcHex[..62] + "ae");

        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.FixedTimeEquals(b).Should().BeTrue();
        (a != other).Should().BeTrue();
        a.FixedTimeEquals(other).Should().BeFalse();
        new HashSet<Hash256> { a, b, other }.Should().HaveCount(2);
    }

    [Fact]
    public void Default_is_zero_and_reading_checks_the_length()
    {
        default(Hash256).IsZero.Should().BeTrue();
        Hash256.Zero.ToString().Should().Be(new string('0', Hash256.HexLength));
        Abc().IsZero.Should().BeFalse();

        FluentActions.Invoking(() => Hash256.Read(new byte[31])).Should().Throw<ArgumentException>();
        Hash256.TryRead(new byte[31], out _).Should().BeFalse();
        Hash256.TryRead(Convert.FromHexString(AbcHex + "ff"), out Hash256 prefix).Should().BeTrue();
        prefix.Should().Be(Abc());

        Span<byte> destination = stackalloc byte[Hash256.Size];
        Abc().TryWriteBytes(destination[..31]).Should().BeFalse();
        Abc().TryWriteBytes(destination).Should().BeTrue();
        destination.ToArray().Should().Equal(Convert.FromHexString(AbcHex));
    }
}
