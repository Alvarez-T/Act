using System.Globalization;
using System.Text;
using YFex.Localization;

namespace YFex.Validation.Testing;

/// <summary>Thrown by <see cref="ValidationAssert"/>; test frameworks report it as a failure.</summary>
public sealed class ValidationAssertionException(string message) : Exception(message);

/// <summary>
/// Framework-agnostic assertions over validation results. Paths are formatted paths
/// (<c>"Lines[2].Quantity"</c>, <c>""</c> = object level); codes are rule codes.
/// <code>
/// ValidationAssert.Valid(cmd);
/// Validator.Validate(bad).ShouldHave("Name", "required").ShouldHaveOnly("Name:required");
/// </code>
/// </summary>
public static class ValidationAssert
{
    /// <summary>Asserts that <paramref name="value"/> has no Error; returns the result (warnings allowed).</summary>
    public static ValidationResult Valid<T>(in T value) where T : IValidatable<T>
    {
        var result = Validator.Validate(in value);
        if (!result.IsValid) throw new ValidationAssertionException("Expected a valid value but got:\n" + Describe(result));
        return result;
    }

    /// <summary>Asserts that <paramref name="value"/> has at least one Error; returns the result.</summary>
    public static ValidationResult Invalid<T>(in T value) where T : IValidatable<T>
    {
        var result = Validator.Validate(in value);
        if (result.IsValid) throw new ValidationAssertionException("Expected an invalid value but validation passed" + (result.Violations.IsEmpty ? "." : ":\n" + Describe(result)));
        return result;
    }

    /// <summary>Asserts a violation with <paramref name="code"/> at <paramref name="path"/> (and severity, when given).</summary>
    public static ValidationResult ShouldHave(this ValidationResult result, string path, RuleCode code, Severity? severity = null)
    {
        foreach (ref readonly Violation v in result.Violations)
            if (v.Path.FormattedEquals(path) && v.Code == code && (severity is null || v.Severity == severity))
                return result;
        throw new ValidationAssertionException($"Expected violation '{code}' at '{path}'{(severity is null ? "" : $" ({severity})")} but got:\n{Describe(result)}");
    }

    /// <summary>Asserts no violation at <paramref name="path"/>.</summary>
    public static ValidationResult ShouldNotHaveAt(this ValidationResult result, string path)
    {
        foreach (ref readonly Violation v in result.Violations)
            if (v.Path.FormattedEquals(path))
                throw new ValidationAssertionException($"Expected no violation at '{path}' but got:\n{Describe(result)}");
        return result;
    }

    /// <summary>Asserts the exact ordered list of violations as <c>"path:code"</c> entries.</summary>
    public static ValidationResult ShouldHaveOnly(this ValidationResult result, params string[] pathColonCode)
    {
        var actual = new List<string>(result.Violations.Count);
        foreach (ref readonly Violation v in result.Violations) actual.Add(v.Path + ":" + v.Code);
        if (!actual.SequenceEqual(pathColonCode, StringComparer.Ordinal))
            throw new ValidationAssertionException(
                $"Expected [{string.Join(", ", pathColonCode)}] but got [{string.Join(", ", actual)}]");
        return result;
    }

    /// <summary>Asserts that every violation code has a message in <paramref name="catalog"/> for <paramref name="culture"/>
    /// (catches missing catalog entries in tests).</summary>
    public static ValidationResult ShouldHaveMessages(this ValidationResult result, MessageCatalog catalog, CultureInfo culture)
    {
        foreach (ref readonly Violation v in result.Violations)
            if (v.Detail is null && !catalog.TryGetTemplate(v.Code.Value ?? "", culture, out _))
                throw new ValidationAssertionException($"No message for code '{v.Code}' ({culture.Name}).");
        return result;
    }

    /// <summary>One line per violation: <c>Error  Lines[2].Qty  range (1, 99)</c>.</summary>
    public static string Describe(in ValidationResult result)
    {
        if (result.Violations.IsEmpty) return "  (no violations)";
        var sb = new StringBuilder();
        foreach (ref readonly Violation v in result.Violations)
        {
            sb.Append("  ").Append(v.Severity.ToString().PadRight(8)).Append(v.Path.IsEmpty ? "<object>" : v.Path.ToString())
              .Append("  ").Append(v.Code);
            if (v.Args.Count > 0)
            {
                sb.Append(" (");
                for (int i = 0; i < v.Args.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(v.Args[i].ToString(null, CultureInfo.InvariantCulture));
                }
                sb.Append(')');
            }
            if (v.Detail is not null) sb.Append(" \"").Append(v.Detail).Append('"');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
