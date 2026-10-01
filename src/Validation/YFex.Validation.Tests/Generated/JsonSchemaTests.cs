using System.Text.Json;

namespace YFex.Validation.Tests.Generated;

/// <summary>Phase 6 — JSON Schema export of validatable types (AI tool parameters, forms) from generated shapes + constraints.</summary>
public class JsonSchemaTests
{
    private static JsonElement Schema<T>() where T : IValidatable<T> => JsonDocument.Parse(JsonSchemaWriter.ToJson<T>()).RootElement;
    private static JsonElement Prop(JsonElement schema, string name) => schema.GetProperty("properties").GetProperty(name);
    private static string[] Required(JsonElement schema) => schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public void Static_constraints_become_json_schema_keywords()
    {
        var s = Schema<CustomerDto>();
        s.GetProperty("title").GetString().Should().Be("CustomerDto");
        Required(s).Should().Equal("Name", "Email", "Document");                // warnings (Nickname) and conditional (CompanyName) are not "required"
        Prop(s, "Name").GetProperty("maxLength").GetInt32().Should().Be(80);
        Prop(s, "Email").GetProperty("format").GetString().Should().Be("email");
        Prop(s, "Document").GetProperty("x-format").GetString().Should().Be("digits");
        Prop(s, "Age").GetProperty("type").GetString().Should().Be("integer");
        Prop(s, "Age").GetProperty("maximum").GetInt32().Should().Be(150);
        Prop(s, "Balance").GetProperty("x-type").GetString().Should().Be("money");
        Prop(s, "BirthDate").GetProperty("formatMinimum").GetString().Should().Be("1900-01-01");
        Prop(s, "BirthDate").GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().Equal("string", "null");
    }

    [Fact]
    public void Collections_and_nested_objects_are_described_with_their_own_constraints()
    {
        var s = Schema<CustomerDto>();
        var addresses = Prop(s, "Addresses");
        addresses.GetProperty("type").GetString().Should().Be("array");
        Required(addresses.GetProperty("items")).Should().Equal("City");
        Prop(addresses.GetProperty("items"), "City").GetProperty("maxLength").GetInt32().Should().Be(60);
        var tags = Prop(s, "Tags");
        tags.GetProperty("minItems").GetInt32().Should().Be(1);                  // NotEmpty
        tags.GetProperty("items").GetProperty("maxLength").GetInt32().Should().Be(10);   // [Each] Length(1, 10)
        Prop(s, "Billing").GetProperty("title").GetString().Should().Be("AddressDto");
    }

    [Fact]
    public void Live_constraints_merge_most_restrictive()
    {
        // e.g. the current rules decision says: at most 40 years for this flow, page size ≤ 20.
        ConstraintLookup live = (ReadOnlySpan<char> path, out FieldConstraints c) =>
        {
            c = path switch
            {
                "Age" => FieldConstraints.Create(max: RawValue.From(40)),
                "PageSize" => FieldConstraints.Create(max: RawValue.From(20)),
                _ => default,
            };
            return path is "Age" or "PageSize";
        };
        var json = JsonSchemaWriter.ToJson(CustomerDto.Shape!, JsonSchemaWriter.Merge(CustomerDto.TryGetConstraints, live));
        var s = JsonDocument.Parse(json).RootElement;
        Prop(s, "Age").GetProperty("maximum").GetInt32().Should().Be(40);
        Prop(s, "Age").GetProperty("minimum").GetInt32().Should().Be(0);         // the static minimum stays
        Prop(s, "PageSize").GetProperty("maximum").GetInt32().Should().Be(20);
    }

    [Fact]
    public void The_registry_describes_types_by_clr_type()
    {
        JsonSchemas.TryGet(typeof(CustomerDto), out var json).Should().BeTrue();   // filled by the module initializer
        json.Should().Contain("\"title\":\"CustomerDto\"");
        JsonSchemas.TryGet(typeof(string), out _).Should().BeFalse();
    }
}
