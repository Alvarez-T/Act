namespace YFex;

/// <summary>
/// A value type that can find itself inside free text (privacy 06 §4, decided 2026-11-05): a document number in an exception message,
/// a phone in a scraped page, a code typed into a search box. Each type writes its own scan (check digits, boundaries), so the logic
/// exists once and every consumer reuses it: redaction (YFex.Privacy <c>TextScrubber.Add&lt;T&gt;()</c>), telemetry, scraping,
/// search routing.
/// <para>
/// Static abstract: called through a type parameter (<c>where T : IExtractable&lt;T&gt;</c>), so there is no boxing and no virtual
/// call, and native AOT needs no reflection. Spans only: a <see cref="string"/> converts implicitly without copying, and callers scan
/// the rest of a text by slicing (<c>text[(start + length)..]</c>).
/// </para>
/// <code>
/// if (Cpf.TryExtract(message, out Cpf cpf, out int start, out int length)) { … }
/// </code>
/// </summary>
public interface IExtractable<TSelf> where TSelf : IExtractable<TSelf>
{
    /// <summary>
    /// The first valid occurrence in <paramref name="text"/>: the parsed value and where it is (<paramref name="start"/> relative to
    /// <paramref name="text"/>). Implementations validate (check digits, ranges), reject candidates glued to other digits or letters,
    /// and allocate nothing when there is no match.
    /// </summary>
    static abstract bool TryExtract(ReadOnlySpan<char> text, out TSelf value, out int start, out int length);
}
