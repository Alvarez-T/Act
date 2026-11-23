using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex.Reasons;

/// <summary>
/// JSON form of a <see cref="WhyReport"/> (⚑RX27): what AI agents (YFex.Capabilities, YFex.Automation) and thin clients
/// receive, with the same data a UI renders. Source-generated (AOT, no reflection); camelCase names, enums as their names
/// (<c>"Limit"</c>), empty members left out. Build the report with a masked catalog when it leaves the user's screen, so values of
/// <c>[Sensitive]</c> types are masked (⚑RX26).
/// </summary>
/// <example><code>
/// var report = decision.Explain(catalog.Masked, culture).ForField(SaleSituation.Fields.Installments).ToWhy();
/// string json = WhyJson.Serialize(report);
/// // {"title":"Parcelas","items":[{"title":"Parcelas","kind":"Limit","detail":"até 10x","key":"rule:installments.first-line"}],…}
/// WhyReport back = WhyJson.Deserialize(json);
/// </code></example>
public static class WhyJson
{
    /// <summary>The report as JSON.</summary>
    public static string Serialize(WhyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, WhyJsonContext.Default.WhyReport);
    }

    /// <summary>Writes the report to <paramref name="writer"/>.</summary>
    public static void Write(Utf8JsonWriter writer, WhyReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);
        JsonSerializer.Serialize(writer, report, WhyJsonContext.Default.WhyReport);
    }

    /// <summary>Reads a report written by <see cref="Serialize"/>.</summary>
    /// <exception cref="JsonException">The text is not a report.</exception>
    public static WhyReport Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, WhyJsonContext.Default.WhyReport) ?? throw new JsonException("The JSON is null, not a why report.");
    }

    /// <summary>The source-generated type information, for callers that compose it into their own serializer options.</summary>
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<WhyReport> TypeInfo => WhyJsonContext.Default.WhyReport;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(WhyReport))]
internal sealed partial class WhyJsonContext : JsonSerializerContext;
