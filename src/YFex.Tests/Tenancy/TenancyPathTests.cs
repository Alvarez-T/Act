using System.Text;
using YFex.Diagnostics;
using YFex.Tenancy;

namespace YFex.Tests.Tenancy;

public sealed class TenancyPathTests
{
    [Theory]
    [InlineData("0=farmaviva")]
    [InlineData("0=farmaviva/1=loja7")]
    [InlineData("0=acme/3=north/200=t-1")]
    [InlineData("255=x")]
    [InlineData("0=a%2Fb/1=50%25")]
    [InlineData("0=joão/1=中@x=y")]
    [InlineData("0=a/1=b/2=c/3=d/4=e/5=f/6=g/7=h")]
    public void Valid_texts_parse_and_round_trip(string text)
    {
        TenancyPath.TryParse(text, out TenancyPath path).Should().BeTrue();
        path.ToString().Should().Be(text);
        path.IsNone.Should().BeFalse();
        TenancyPath.Parse(text.AsSpan()).Should().Be(path);
        TenancyPath.IsValid(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("acme")]                 // no rank
    [InlineData("=acme")]
    [InlineData("01=acme")]              // leading zero
    [InlineData("256=acme")]             // rank over a byte
    [InlineData("-1=acme")]
    [InlineData(" 0=acme")]
    [InlineData("0=")]                   // empty value
    [InlineData("0=acme/")]              // trailing separator
    [InlineData("/0=acme")]
    [InlineData("0=acme//1=b")]
    [InlineData("1=b/0=acme")]           // ranks must increase
    [InlineData("0=a/0=b")]
    [InlineData("0=a%")]                 // bad escapes
    [InlineData("0=a%2")]
    [InlineData("0=a%2f")]
    [InlineData("0=a%41")]
    [InlineData("0=a\tb")]               // control characters
    [InlineData("0=a\u007F")]
    [InlineData("0=a\u0085")]
    [InlineData("0=a/1=b/2=c/3=d/4=e/5=f/6=g/7=h/8=i")]   // 9 levels
    public void Invalid_texts_are_rejected(string text)
    {
        TenancyPath.TryParse(text, out _).Should().BeFalse();
        FluentActions.Invoking(() => TenancyPath.Parse(text)).Should().Throw<FormatException>();
    }

    [Fact]
    public void Lone_surrogates_are_rejected()
    {
        TenancyPath.TryParse("0=a" + (char)0xD800, out _).Should().BeFalse();
        TenancyPath.TryParse("0=" + (char)0xDC00 + "a", out _).Should().BeFalse();
    }

    [Fact]
    public void Length_limit_is_512_utf8_bytes()
    {
        TenancyPath.TryParse("0=" + new string('a', 510), out _).Should().BeTrue();
        TenancyPath.TryParse("0=" + new string('a', 511), out _).Should().BeFalse();
        TenancyPath.TryParse("0=" + new string('é', 255), out _).Should().BeTrue();      // 2 + 510 bytes
        TenancyPath.TryParse("0=" + new string('é', 256), out _).Should().BeFalse();
    }

    [Fact]
    public void None_is_default_and_empty_text()
    {
        TenancyPath none = default;
        none.IsNone.Should().BeTrue();
        none.Should().Be(TenancyPath.None);
        none.ToString().Should().BeEmpty();
        none.Depth.Should().Be(0);
        TenancyPath.TryParse("", out TenancyPath empty).Should().BeTrue();
        empty.Should().Be(TenancyPath.None);
        TenancyPath.TryParse((string?)null, out TenancyPath fromNull).Should().BeTrue();
        fromNull.IsNone.Should().BeTrue();
        TenancyPath.Parse(null).IsNone.Should().BeTrue();
    }

    [Fact]
    public void Depth_parent_and_within()
    {
        var store = TenancyPath.Parse("0=acme/1=loja7");
        var company = TenancyPath.Parse("0=acme");
        store.Depth.Should().Be(2);
        store.Parent.Should().Be(company);
        company.Parent.Should().Be(TenancyPath.None);
        TenancyPath.Parse("0=a%2Fb/1=c").Depth.Should().Be(2);    // escaped '/' is not a separator

        store.IsWithin(company).Should().BeTrue();
        store.IsWithin(store).Should().BeTrue();
        store.IsWithin(TenancyPath.None).Should().BeTrue();
        company.IsWithin(store).Should().BeFalse();
        TenancyPath.None.IsWithin(company).Should().BeFalse();
        TenancyPath.Parse("0=acme/1=loja70").IsWithin(TenancyPath.Parse("0=acme/1=loja7")).Should().BeFalse();
        TenancyPath.Parse("0=acmex").IsWithin(company).Should().BeFalse();
    }

    [Fact]
    public void Equality_is_ordinal()
    {
        TenancyPath.Parse("0=acme").Should().Be(TenancyPath.Parse("0=acme"));
        TenancyPath.Parse("0=acme").Should().NotBe(TenancyPath.Parse("0=ACME"));
    }

    [Theory]
    [InlineData("", "0000")]
    [InlineData("0=acme", "0600303D61636D65")]
    [InlineData("0=a/1=b", "0700303D612F313D62")]
    public void Canonical_golden_bytes(string text, string hex)
    {
        var path = TenancyPath.Parse(text);
        Span<byte> buffer = stackalloc byte[TenancyPath.MaxCanonicalLength];
        TenancyPath.TryWriteCanonical(path, buffer, out int written).Should().BeTrue();
        Convert.ToHexString(buffer[..written]).Should().Be(hex);
        TenancyPath.TryReadCanonical(Convert.FromHexString(hex), out TenancyPath read, out int consumed).Should().BeTrue();
        read.Should().Be(path);
        consumed.Should().Be(written);
        TenancyPath.TryWriteCanonical(path, buffer[..(written - 1)], out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("01")]                     // truncated length
    [InlineData("0200")]                   // length larger than input
    [InlineData("0200303D")]               // "0=" is not a path
    [InlineData("0200C328")]               // invalid UTF-8
    [InlineData("0102")]                   // length over 512 (0x0201)
    public void Canonical_rejects_bytes_that_break_a_rule(string hex)
        => TenancyPath.TryReadCanonical(Convert.FromHexString(hex), out _, out _).Should().BeFalse();

    [Fact]
    public void Formats_without_allocating_and_logs_its_text()
    {
        var path = TenancyPath.Parse("0=farmaviva/1=loja7");
        var chars = new char[64];
        path.TryFormat(chars, out int written).Should().BeTrue();
        new string(chars, 0, written).Should().Be("0=farmaviva/1=loja7");
        path.TryFormat(chars.AsSpan(0, 3), out _).Should().BeFalse();

        var bytes = new byte[TenancyPath.MaxCanonicalLength];
        for (int i = 0; i < 100; i++) { path.TryFormat(chars, out _); TenancyPath.TryWriteCanonical(path, bytes, out _); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) path.TryFormat(chars, out _);
        for (int i = 0; i < 1000; i++) TenancyPath.TryWriteCanonical(path, bytes, out _);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);

        Span<byte> record = stackalloc byte[128];
        var writer = new LogWriter(record);
        writer.Write(2, path);
        var reader = new LogFieldReader(writer.Written);
        reader.TryRead(out LogField field).Should().BeTrue();
        field.GetText(out _).ToString().Should().Be("0=farmaviva/1=loja7");
    }

    [Fact]
    public void Property_random_paths_round_trip_text_and_bytes()
    {
        var random = new Random(77);
        string[] pieces = ["a", "Z", "0", "-", "=", "@", " ", "é", "中", "😀", "%25", "%2F"];
        Span<byte> buffer = stackalloc byte[TenancyPath.MaxCanonicalLength];
        for (int i = 0; i < 3000; i++)
        {
            var text = new StringBuilder();
            int levels = random.Next(1, TenancyPath.MaxLevels + 1);
            int rank = random.Next(0, 20);
            for (int level = 0; level < levels && rank <= 255; level++)
            {
                if (level > 0) text.Append('/');
                text.Append(rank).Append('=');
                int count = random.Next(1, 12);
                for (int p = 0; p < count; p++) text.Append(pieces[random.Next(pieces.Length)]);
                rank += random.Next(1, 40);
            }
            string candidate = text.ToString();
            if (Encoding.UTF8.GetByteCount(candidate) > TenancyPath.MaxLength) continue;

            TenancyPath.TryParse(candidate, out TenancyPath path).Should().BeTrue($"case {i}: {candidate}");
            TenancyPath.TryWriteCanonical(path, buffer, out int written).Should().BeTrue();
            TenancyPath.TryReadCanonical(buffer[..written], out TenancyPath read, out _).Should().BeTrue();
            read.Should().Be(path);
            read.ToString().Should().Be(candidate);
        }
    }
}
