using System.Globalization;
using YFex.Localization;

namespace YFex.Tests.Localization;

public class MessageCatalogTests
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private sealed class DictSource(Dictionary<(string, string), string> templates,
        Dictionary<(string, string), string>? fields = null) : IMessageSource
    {
        public bool TryGetTemplate(string code, string cultureName, out string template)
            => templates.TryGetValue((code, cultureName), out template!);

        public bool TryGetFieldName(string fieldKey, string cultureName, out string name)
        {
            name = "";
            return fields is not null && fields.TryGetValue((fieldKey, cultureName), out name!);
        }
    }

    private static readonly DictSource BuiltIn = new(new()
    {
        [("len", "")] = "{field} must have between {0} and {1} characters",
        [("len", "pt")] = "{field} deve ter entre {0} e {1} caracteres",
        [("range", "")] = "{field} max {1:N2}",
        [("braces", "")] = "{{literal}} {0} {9} {x}",
    });

    [Fact]
    public void Formats_WithCultureFallback_AndFieldName()
    {
        var catalog = new MessageCatalog(BuiltIn);
        RawValue[] args = [RawValue.From(2), RawValue.From(80)];

        catalog.Format("len", args, PtBr, "CustomerVm.FullName")
            .Should().Be("Full name deve ter entre 2 e 80 caracteres");
        catalog.Format("len", args, En, "CustomerVm.FullName")
            .Should().Be("Full name must have between 2 and 80 characters");
    }

    [Fact]
    public void ArgFormats_AndEscapes()
    {
        var catalog = new MessageCatalog(BuiltIn);
        catalog.Format("range", [RawValue.From(0), RawValue.From(1234.5m)], PtBr, "X.Total")
            .Should().Be("Total max 1.234,50");
        catalog.Format("braces", [RawValue.From("a")], En)
            .Should().Be("{literal} a {9} {x}", "unknown placeholders stay literal");
    }

    [Fact]
    public void HigherLayer_Overrides_AndUnknownCode_RendersCode()
    {
        var tenant = new DictSource(new() { [("len", "pt")] = "Tamanho inválido" },
            new() { [("CustomerVm.Doc", "pt-BR")] = "CPF/CNPJ" });
        var catalog = new MessageCatalog(BuiltIn).WithLayer(tenant, 0);

        catalog.Format("len", [], PtBr).Should().Be("Tamanho inválido");
        catalog.Format("nope.code", [], PtBr).Should().Be("nope.code");
        catalog.FieldName("CustomerVm.Doc", PtBr).Should().Be("CPF/CNPJ");
        catalog.Layers.Length.Should().Be(2);
    }

    [Fact]
    public void TryFormat_ReportsTooSmall()
    {
        var catalog = new MessageCatalog(BuiltIn);
        Span<char> small = stackalloc char[5];
        catalog.TryFormat("len", [RawValue.From(2), RawValue.From(80)], En, small, out _).Should().BeFalse();
        Span<char> big = stackalloc char[200];
        catalog.TryFormat("len", [RawValue.From(2), RawValue.From(80)], En, big, out int w, "A.Name").Should().BeTrue();
        big[..w].ToString().Should().Be("Name must have between 2 and 80 characters");
    }

    [Theory]
    [InlineData("SaleVm.ManualDiscount", "Manual discount")]
    [InlineData("Name", "Name")]
    [InlineData("X.CPFNumber", "CPF number")]
    [InlineData("X.Lines[2]", "Lines")]
    public void Humanize(string key, string expected) => MessageCatalog.Humanize(key).Should().Be(expected);
}
