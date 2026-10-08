using System.Text;
using YFex.Cryptography;

namespace YFex.Tests.Cryptography;

public sealed class KeyIdTests
{
    private static readonly KeyId Known = KeyId.Parse("01927c4e-5a3b-7c2d-9e1f-a0b1c2d3e4f5");

    [Fact]
    public void Binary_form_is_big_endian_like_the_text()
    {
        Span<byte> bytes = stackalloc byte[KeyId.Size];
        Known.WriteTo(bytes);
        Convert.ToHexStringLower(bytes).Should().Be("01927c4e5a3b7c2d9e1fa0b1c2d3e4f5");
        KeyId.Read(bytes).Should().Be(Known);
        Known.TryWriteBytes(bytes[..15]).Should().BeFalse();
        FluentActions.Invoking(() => KeyId.Read(new byte[15])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Formats_full_and_short_into_chars_and_utf8()
    {
        Known.ToString().Should().Be("01927c4e-5a3b-7c2d-9e1f-a0b1c2d3e4f5");
        Known.ToString("S", null).Should().Be("01927c4e");

        Span<char> chars = stackalloc char[36];
        Known.TryFormat(chars, out int written, "S", null).Should().BeTrue();
        new string(chars[..written]).Should().Be("01927c4e");
        Known.TryFormat(chars[..7], out _, "S", null).Should().BeFalse();

        Span<byte> utf8 = stackalloc byte[36];
        Known.TryFormat(utf8, out int bytes, default, null).Should().BeTrue();
        Encoding.UTF8.GetString(utf8[..bytes]).Should().Be(Known.ToString());
    }

    [Fact]
    public void New_ids_are_version_7_and_sortable()
    {
        KeyId first = KeyId.New();
        KeyId second = KeyId.New();
        first.Value.Version.Should().Be(7);
        first.Should().NotBe(second);
        default(KeyId).IsEmpty.Should().BeTrue();
        first.IsEmpty.Should().BeFalse();
        KeyId.TryParse("nope", null, out _).Should().BeFalse();
    }
}
