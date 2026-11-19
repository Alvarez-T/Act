namespace YFex.Tests.Contacts;

/// <summary><see cref="PhoneRegion"/> numbering plans on <see cref="PhoneNumber"/>: region parsing, area code, line type,
/// national format and extraction from free text (the cases the domain's <c>Telefone</c> handled, plus NANP).</summary>
public class PhoneRegionTests
{
    private static readonly PhoneRegion[] Brazil = [PhoneRegion.Brazil];
    private static readonly PhoneRegion[] UnitedStates = [PhoneRegion.UnitedStates];

    [Theory]
    [InlineData("(11) 98765-4321", "+5511987654321", "11", true)]
    [InlineData("11 3456-7890", "+551134567890", "11", false)]
    [InlineData("0 21 11 98765-4321", "+5511987654321", "11", true)]      // trunk 0 + carrier code
    [InlineData("011 98765-4321", "+5511987654321", "11", true)]          // trunk 0
    [InlineData("55 11 98765-4321", "+5511987654321", "11", true)]        // country code without +
    [InlineData("+55 (21) 2345-6789", "+552123456789", "21", false)]
    public void Brazil_parses_national_and_international_forms(string text, string e164, string ddd, bool mobile)
    {
        PhoneNumber.TryParse(text, PhoneRegion.Brazil, out PhoneNumber phone).Should().BeTrue();
        phone.E164.Should().Be(e164);
        phone.Region.Should().BeSameAs(PhoneRegion.Brazil);
        phone.AreaCode.ToString().Should().Be(ddd);
        phone.IsMobile.Should().Be(mobile);
    }

    [Theory]
    [InlineData("(10) 98765-4321")]      // DDD 10 is not assigned
    [InlineData("(20) 3456-7890")]       // DDD 20 is not assigned
    [InlineData("(11) 8765-4321")]       // landlines start 2-5
    [InlineData("(11) 88765-4321")]      // 9-digit numbers are mobiles, which start with 9
    [InlineData("98765-4321")]           // no DDD
    [InlineData("+55 11 1234")]          // international, but not a Brazilian number
    public void Brazil_rejects_numbers_outside_its_plan(string text)
        => PhoneNumber.TryParse(text, PhoneRegion.Brazil, out _).Should().BeFalse();

    [Theory]
    [InlineData("(212) 555-0100", "+12125550100")]
    [InlineData("1 212 555 0100", "+12125550100")]                        // long-distance 1
    [InlineData("+1 (415) 867-5309", "+14158675309")]
    public void UnitedStates_parses(string text, string e164)
    {
        PhoneNumber.TryParse(text, PhoneRegion.UnitedStates, out PhoneNumber phone).Should().BeTrue();
        phone.E164.Should().Be(e164);
        phone.Region.Should().BeSameAs(PhoneRegion.UnitedStates);
        phone.IsMobile.Should().BeNull();
    }

    [Theory]
    [InlineData("(112) 555-0100")]       // area code starts with 1
    [InlineData("(292) 555-0100")]       // middle digit 9
    [InlineData("(411) 555-0100")]       // N11 service code
    [InlineData("(212) 155-0100")]       // exchange starts with 1
    [InlineData("(212) 911-0100")]       // N11 exchange
    public void UnitedStates_rejects_numbers_outside_NANP(string text)
        => PhoneNumber.TryParse(text, PhoneRegion.UnitedStates, out _).Should().BeFalse();

    [Fact]
    public void International_numbers_are_checked_by_their_own_plan()
    {
        PhoneNumber.TryParse("+1 212 555 0100", PhoneRegion.Brazil, out PhoneNumber us).Should().BeTrue();
        us.Region.Should().BeSameAs(PhoneRegion.UnitedStates);
        PhoneNumber.TryParse("+1 112 555 0100", PhoneRegion.Brazil, out _).Should().BeFalse();
        PhoneNumber.TryParse("+351 912 345 678", PhoneRegion.Brazil, out PhoneNumber pt).Should().BeTrue();   // no plan: E.164 shape
        pt.Region.Should().BeNull();
    }

    [Fact]
    public void N_formats_with_the_region_mask()
    {
        PhoneNumber.Parse("+5511987654321").ToString("N", null).Should().Be("(11) 98765-4321");
        PhoneNumber.Parse("+551134567890").ToString("N", null).Should().Be("(11) 3456-7890");
        PhoneNumber.Parse("+12125550100").ToString("N", null).Should().Be("(212) 555-0100");
        PhoneNumber.Parse("+351912345678").ToString("N", null).Should().Be("+351912345678");
        $"{PhoneNumber.Parse("+5511987654321"):N}".Should().Be("(11) 98765-4321");
    }

    [Fact]
    public void A_custom_region_checks_length_by_its_mask()
    {
        var portugal = new PhoneRegion("PT", 351, "### ### ###");
        PhoneNumber.TryParse("912 345 678", portugal, out PhoneNumber phone).Should().BeTrue();
        phone.E164.Should().Be("+351912345678");
        PhoneNumber.TryParse("912 345", portugal, out _).Should().BeFalse();
        portugal.FormatNational("912345678").Should().Be("912 345 678");
    }

    [Theory]
    [InlineData("ligar (11) 98765-4321 hoje", "(11) 98765-4321", "+5511987654321", true)]
    [InlineData("fixo 11 3456-7890.", "11 3456-7890", "+551134567890", false)]
    [InlineData("whats +55 11 987654321", "+55 11 987654321", "+5511987654321", true)]
    [InlineData("tel:11987654321", "11987654321", "+5511987654321", true)]
    [InlineData("tel +5511987654321", "+5511987654321", "+5511987654321", true)]
    [InlineData("contato (21)2345-6789", "(21)2345-6789", "+552123456789", false)]
    [InlineData("dois: 11 98765-4321 12 3456-7890", "11 98765-4321", "+5511987654321", true)]
    public void Brazilian_phones_are_found_in_text(string text, string expected, string e164, bool mobile)
    {
        PhoneNumber.TryExtract(text, Brazil, out PhoneNumber phone, out int start, out int length).Should().BeTrue();
        text.Substring(start, length).Should().Be(expected);
        phone.E164.Should().Be(e164);
        phone.IsMobile.Should().Be(mobile);
    }

    [Theory]
    [InlineData("Venda finalizada em 48 ms")]
    [InlineData("timestamp 1730812345")]                        // 10 bare digits: too ambiguous
    [InlineData("ligar 98765-4321")]                            // no DDD
    [InlineData("chave NFe 35240511144477735190550010000012341000012345")]
    [InlineData("EAN 7891114447773")]
    [InlineData("emitida em 2026-11-05 13:45")]
    [InlineData("pedido 119876543210")]                         // glued to another digit
    [InlineData("NF 0012345678")]                               // DDD 00
    public void Brazilian_lookalikes_are_not_phones(string text)
        => PhoneNumber.TryExtract(text, Brazil, out _, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("call (212) 555-0100 today", "(212) 555-0100", "+12125550100")]
    [InlineData("office 415.867.5309", "415.867.5309", "+14158675309")]
    [InlineData("intl +1 212 555 0100", "+1 212 555 0100", "+12125550100")]
    public void US_phones_are_found_in_text(string text, string expected, string e164)
    {
        PhoneNumber.TryExtract(text, UnitedStates, out PhoneNumber phone, out int start, out int length).Should().BeTrue();
        text.Substring(start, length).Should().Be(expected);
        phone.E164.Should().Be(e164);
    }

    [Theory]
    [InlineData("order 2125550100 shipped")]                    // bare digits
    [InlineData("emitida em 2026-11-05 13:45")]
    public void US_lookalikes_are_not_phones(string text)
        => PhoneNumber.TryExtract(text, UnitedStates, out _, out _, out _).Should().BeFalse();

    [Fact]
    public void The_region_order_decides_ambiguous_numbers()
    {
        const string text = "fone (21) 2555-0100";
        PhoneNumber.TryExtract(text, [PhoneRegion.Brazil, PhoneRegion.UnitedStates], out PhoneNumber phone, out _, out _).Should().BeTrue();
        phone.CountryCallingCode.Should().Be(55);
    }

    [Fact]
    public void IExtractable_uses_the_default_regions()
    {
        IReadOnlyList<PhoneRegion> before = PhoneRegion.Default;
        try
        {
            PhoneRegion.Default = [PhoneRegion.UnitedStates];
            Extract<PhoneNumber>("ligar (11) 98765-4321").Should().BeFalse();
            Extract<PhoneNumber>("call (212) 555-0100").Should().BeTrue();
            PhoneRegion.Default = [PhoneRegion.Brazil];
            Extract<PhoneNumber>("ligar (11) 98765-4321").Should().BeTrue();
        }
        finally
        {
            PhoneRegion.Default = before;
        }
    }

    [Fact]
    public void No_match_allocates_nothing()
    {
        long allocated = -1;
        for (int round = 0; round < 20 && allocated != 0; round++)
        {
            allocated = AllocatedByNoMatchScans(20_000);
            if (allocated != 0) Thread.Sleep(100);
        }
        allocated.Should().Be(0);
    }

    private static long AllocatedByNoMatchScans(int iterations)
    {
        const string text = "Venda finalizada em 48 ms no caixa 3, pedido 3f2504e0, total 1.234,56, EAN 7891114447773, em 2026-11-05 13:45";
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            PhoneNumber.TryExtract(text, [PhoneRegion.Brazil, PhoneRegion.UnitedStates], out _, out _, out _);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static bool Extract<T>(string text) where T : IExtractable<T> => T.TryExtract(text, out _, out _, out _);
}
