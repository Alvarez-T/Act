namespace YFex.Tests.Paths;

public class FieldPathTests
{
    [Theory]
    [InlineData("Name", new int[0], "Name")]
    [InlineData("Lines[].Quantity", new[] { 3 }, "Lines[3].Quantity")]
    [InlineData("Orders[].Lines[].Qty", new[] { 1, 4 }, "Orders[1].Lines[4].Qty")]
    [InlineData("Matrix[][]", new[] { 1, 2 }, "Matrix[1][2]")]
    [InlineData("Lines[].Quantity", new int[0], "Lines[].Quantity")]
    public void Formats(string template, int[] indices, string expected)
    {
        var path = FieldPath.Of(template, IndexPath.Of(indices));
        path.ToString().Should().Be(expected);
        path.FormattedEquals(expected).Should().BeTrue();
        path.FormattedEquals(expected + "x").Should().BeFalse();
    }

    [Fact]
    public void Default_IsObjectLevel()
    {
        FieldPath p = default;
        p.IsEmpty.Should().BeTrue();
        p.ToString().Should().BeEmpty();
        p.FormattedEquals("").Should().BeTrue();
    }

    [Theory]
    [InlineData("Orders[1].Lines[4].Qty", "Orders[].Lines[].Qty", new[] { 1, 4 })]
    [InlineData("Name", "Name", new int[0])]
    [InlineData("Lines[12]", "Lines[]", new[] { 12 })]
    public void Parses(string text, string template, int[] indices)
    {
        FieldPath.TryParse(text, out var p).Should().BeTrue();
        p.Template.Should().Be(template);
        p.Should().Be(FieldPath.Of(template, IndexPath.Of(indices)));
        p.ToString().Should().Be(text);
    }

    [Theory]
    [InlineData("Lines[x]")]
    [InlineData("Lines[-1]")]
    [InlineData("Lines[]")]
    [InlineData("Na me")]
    [InlineData("A[1].B[2].C[3].D[4].E[5]")]
    public void RejectsInvalid(string text)
    {
        FieldPath.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void WithIndex_AndPrefixed()
    {
        FieldPath.Of("Lines[].Quantity").WithIndex(2).ToString().Should().Be("Lines[2].Quantity");
        FieldPath.Of("City").Prefixed("Address").ToString().Should().Be("Address.City");
        FieldPath.Of("Quantity").Prefixed("Lines[]", 2).ToString().Should().Be("Lines[2].Quantity");
        FieldPath.Of("Lines[].Qty", 4).Prefixed(FieldPath.Of("Orders[]", 1)).ToString().Should().Be("Orders[1].Lines[4].Qty");
        FieldPath.Empty.Prefixed("Address").ToString().Should().Be("Address");
    }

    [Fact]
    public void IndexPath_LimitsDepth()
    {
        var p = IndexPath.Of(1, 2, 3).Append(4);
        p.Count.Should().Be(4);
        var act = () => p.Append(5);
        act.Should().Throw<InvalidOperationException>();
        IndexPath.Of(2, 3).Prepend(1).Should().Be(IndexPath.Of(1, 2, 3));
    }

    [Fact]
    public void Equality()
    {
        FieldPath.Of("Lines[].Qty", 1).Should().Be(FieldPath.Of("Lines[].Qty", 1));
        FieldPath.Of("Lines[].Qty", 1).Should().NotBe(FieldPath.Of("Lines[].Qty", 2));
        FieldPath.Of("Lines[].Qty", 1).GetHashCode().Should().Be(FieldPath.Of("Lines[].Qty", 1).GetHashCode());
    }
}
