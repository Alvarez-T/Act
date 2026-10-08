using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using YFex.Networking;
using YFex.Privacy;

namespace YFex.Tests.Networking;

public sealed class MacAddressTests
{
    private static readonly MacAddress Sample = new(0x001A_2B3C_4D5EUL);

    [Theory]
    [InlineData("00:1A:2B:3C:4D:5E")]
    [InlineData("00-1a-2b-3c-4d-5e")]
    [InlineData("001A2B3C4D5E")]
    [InlineData("001a2b3c4d5e")]
    [InlineData("001a.2b3c.4d5e")]
    [InlineData("  00:1A:2B:3C:4D:5E\t")]
    public void Parses_every_common_form(string text)
    {
        MacAddress.TryParse(text, out MacAddress mac).Should().BeTrue();
        mac.Should().Be(Sample);
        MacAddress.TryParse(Encoding.UTF8.GetBytes(text), out MacAddress utf8).Should().BeTrue();
        utf8.Should().Be(Sample);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00:1A:2B:3C:4D")]
    [InlineData("00:1A-2B:3C:4D:5E")]
    [InlineData("00.1A.2B.3C.4D.5E")]
    [InlineData("00:1A:2B:3C:4D:5G")]
    [InlineData("001A2B3C4D5E00")]
    [InlineData("001a-2b3c-4d5e")]
    [InlineData("00:1A:2B:3C:4D:5É")]
    public void Rejects_malformed_text(string text)
    {
        MacAddress.TryParse(text, out _).Should().BeFalse();
        MacAddress.TryParse(Encoding.UTF8.GetBytes(text), out _).Should().BeFalse();
        FluentActions.Invoking(() => MacAddress.Parse(text)).Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData(null, "00:1A:2B:3C:4D:5E")]
    [InlineData("C", "00:1A:2B:3C:4D:5E")]
    [InlineData("c", "00:1a:2b:3c:4d:5e")]
    [InlineData("D", "00-1A-2B-3C-4D-5E")]
    [InlineData("d", "00-1a-2b-3c-4d-5e")]
    [InlineData("N", "001A2B3C4D5E")]
    [InlineData("n", "001a2b3c4d5e")]
    public void Formats(string? format, string expected)
    {
        Sample.ToString(format).Should().Be(expected);
        Span<byte> utf8 = stackalloc byte[MacAddress.MaxFormattedLength];
        Sample.TryFormat(utf8, out int written, format).Should().BeTrue();
        Encoding.UTF8.GetString(utf8[..written]).Should().Be(expected);
        $"{Sample}".Should().Be("00:1A:2B:3C:4D:5E");
    }

    [Fact]
    public void Formatting_reports_a_short_destination_and_rejects_unknown_formats()
    {
        Span<char> small = stackalloc char[16];
        Sample.TryFormat(small, out int written).Should().BeFalse();
        written.Should().Be(0);
        Sample.TryFormat(small[..12], out written, "N").Should().BeTrue();
        FluentActions.Invoking(() => Sample.ToString("X")).Should().Throw<FormatException>();
    }

    [Fact]
    public void Round_trips_bytes_and_physical_address()
    {
        byte[] raw = [0x02, 0x00, 0x5E, 0x10, 0x00, 0x01];
        MacAddress mac = MacAddress.FromBytes(raw);
        Span<byte> back = stackalloc byte[MacAddress.Size];
        mac.TryWriteBytes(back).Should().BeTrue();
        back.ToArray().Should().Equal(raw);
        mac.TryWriteBytes(stackalloc byte[5]).Should().BeFalse();

        PhysicalAddress physical = mac.ToPhysicalAddress();
        physical.GetAddressBytes().Should().Equal(raw);
        MacAddress.FromPhysicalAddress(physical).Should().Be(mac);
        MacAddress.TryFromPhysicalAddress(PhysicalAddress.None, out _).Should().BeFalse();
        MacAddress.TryFromPhysicalAddress(new PhysicalAddress(new byte[8]), out _).Should().BeFalse();
        FluentActions.Invoking(() => MacAddress.FromBytes(new byte[5])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exposes_vendor_prefix_and_flag_bits()
    {
        Sample.VendorPrefix.Should().Be(0x001A2Bu);
        Sample.IsMulticast.Should().BeFalse();
        Sample.IsLocallyAdministered.Should().BeFalse();

        MacAddress.Parse("02:00:5E:10:00:01").IsLocallyAdministered.Should().BeTrue();
        MacAddress.Parse("01:00:5E:00:00:FB").IsMulticast.Should().BeTrue();     // mDNS IPv4 group
        MacAddress.Broadcast.IsBroadcast.Should().BeTrue();
        MacAddress.Broadcast.IsMulticast.Should().BeTrue();
        MacAddress.Broadcast.ToString().Should().Be("FF:FF:FF:FF:FF:FF");
    }

    [Fact]
    public void Value_is_48_bits_with_ordering_and_equality()
    {
        FluentActions.Invoking(() => new MacAddress(1UL << 48)).Should().Throw<ArgumentOutOfRangeException>();
        ((ulong)Sample).Should().Be(0x001A_2B3C_4D5EUL);
        ((MacAddress)0x001A_2B3C_4D5EUL).Should().Be(Sample);
        (Sample < MacAddress.Broadcast).Should().BeTrue();
        (Sample == MacAddress.Parse("00-1A-2B-3C-4D-5E")).Should().BeTrue();
        Sample.GetHashCode().Should().Be(MacAddress.Parse("001A2B3C4D5E").GetHashCode());
        default(MacAddress).ToString().Should().Be("00:00:00:00:00:00");
    }

    [Fact]
    public void Json_is_the_colon_text_also_as_a_dictionary_key()
    {
        JsonSerializer.Serialize(Sample).Should().Be("\"00:1A:2B:3C:4D:5E\"");
        JsonSerializer.Deserialize<MacAddress>("\"00-1a-2b-3c-4d-5e\"").Should().Be(Sample);
        JsonSerializer.Deserialize<MacAddress>("\"00\\u003A1A:2B:3C:4D:5E\"").Should().Be(Sample);   // escaped text path

        var map = new Dictionary<MacAddress, int> { [Sample] = 1 };
        string json = JsonSerializer.Serialize(map);
        json.Should().Be("{\"00:1A:2B:3C:4D:5E\":1}");
        JsonSerializer.Deserialize<Dictionary<MacAddress, int>>(json)![Sample].Should().Be(1);

        FluentActions.Invoking(() => JsonSerializer.Deserialize<MacAddress>("42")).Should().Throw<JsonException>();
        FluentActions.Invoking(() => JsonSerializer.Deserialize<MacAddress>("\"nope\"")).Should().Throw<JsonException>();
    }

    [Fact]
    public void Is_personal_data_masked_to_the_vendor_prefix()
    {
        Classified.Static<MacAddress>().Sensitivity.Should().Be(DataSensitivity.Personal);
        Classified.Static<MacAddress>().Category.Should().Be(DataCategory.Identification);
        Classified.Mask(Sample).Should().Be("00:1A:2B:**:**:**");

        var attribute = typeof(MacAddress).GetCustomAttributes(typeof(SensitiveAttribute), false).Cast<SensitiveAttribute>().Single();
        attribute.Sensitivity.Should().Be(DataSensitivity.Personal);
        attribute.Category.Should().Be("identification");
    }

    [Fact]
    public void Parse_and_format_do_not_allocate()
    {
        Span<char> buffer = stackalloc char[MacAddress.MaxFormattedLength];
        MacAddress.TryParse("00:1A:2B:3C:4D:5E".AsSpan(), out _);
        Sample.TryFormat(buffer, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            MacAddress.TryParse("00:1A:2B:3C:4D:5E".AsSpan(), out MacAddress parsed);
            parsed.TryFormat(buffer, out _);
            MacAddress.TryParse("001a.2b3c.4d5e"u8, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).Should().BeLessThan(1);
    }
}

public sealed class NetworkConditionsTests
{
    [Fact]
    public void Default_is_unknown()
    {
        NetworkConditions.Unknown.Internet.Should().Be(InternetReachability.Unknown);
        NetworkConditions.Unknown.Should().Be(default(NetworkConditions));
    }

    [Theory]
    [InlineData(NetworkCost.Unrestricted, NetworkMedium.Ethernet, NetworkConditionFlags.None, false)]
    [InlineData(NetworkCost.Unknown, NetworkMedium.WiFi, NetworkConditionFlags.None, false)]
    [InlineData(NetworkCost.Metered, NetworkMedium.WiFi, NetworkConditionFlags.None, true)]
    [InlineData(NetworkCost.OverDataLimit, NetworkMedium.Ethernet, NetworkConditionFlags.None, true)]
    [InlineData(NetworkCost.Unknown, NetworkMedium.Cellular, NetworkConditionFlags.None, true)]
    [InlineData(NetworkCost.Unrestricted, NetworkMedium.WiFi, NetworkConditionFlags.Roaming, true)]
    public void Prefers_small_transfers_on_costly_links(NetworkCost cost, NetworkMedium medium, NetworkConditionFlags flags, bool expected)
        => new NetworkConditions(InternetReachability.Internet, medium, cost, flags, default).PrefersSmallTransfers.Should().Be(expected);
}
