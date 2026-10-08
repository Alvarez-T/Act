namespace YFex.Privacy;

/// <summary>Non-generic view of a classified value, for object-typed paths (UI bindings). Same pattern as <see cref="IRawValue"/>.</summary>
public interface IClassifiedData
{
    /// <summary>The classification of this value.</summary>
    DataClassification Classify();

    /// <summary>Writes the display mask of this value. False when <paramref name="destination"/> is too small.</summary>
    bool TryMask(Span<char> destination, out int written);
}

/// <summary>
/// Classification and masking of a type, implemented by GENERATED code on every <see cref="SensitiveAttribute"/> type
/// (YFex.Privacy.Generator). Static abstract members: no boxing, no virtual call, callable through a type parameter.
/// </summary>
public interface IClassifiedData<TSelf> : IClassifiedData where TSelf : IClassifiedData<TSelf>
{
    /// <summary>Strictest classification of the type.</summary>
    static abstract DataClassification Classification { get; }

    /// <summary>Classification of one value (unions may refine per case). Default: <see cref="Classification"/>.</summary>
    static virtual DataClassification Classify(in TSelf value) => TSelf.Classification;

    /// <summary>Upper bound of the <see cref="TryWriteMasked"/> output, so callers can <c>stackalloc</c>.</summary>
    static abstract int MaxMaskedLength { get; }

    /// <summary>
    /// Writes the display mask (02 §7). Fails closed: a value that does not fit the mask is written in its hidden form.
    /// Returns false only when <paramref name="destination"/> is too small.
    /// </summary>
    static abstract bool TryWriteMasked(in TSelf value, Span<char> destination, out int written);

    DataClassification IClassifiedData.Classify() => TSelf.Classify((TSelf)this);

    bool IClassifiedData.TryMask(Span<char> destination, out int written) => TSelf.TryWriteMasked((TSelf)this, destination, out written);
}

/// <summary>Calls the static members of <see cref="IClassifiedData{TSelf}"/> through a type parameter.</summary>
public static class Classified
{
    /// <summary>The type's (strictest) classification.</summary>
    public static DataClassification Static<T>() where T : IClassifiedData<T> => T.Classification;

    /// <summary>The classification of <paramref name="value"/>.</summary>
    public static DataClassification Of<T>(in T value) where T : IClassifiedData<T> => T.Classify(in value);

    /// <summary>Writes the display mask of <paramref name="value"/>.</summary>
    public static bool TryMask<T>(in T value, Span<char> destination, out int written) where T : IClassifiedData<T>
        => T.TryWriteMasked(in value, destination, out written);

    /// <summary>The display mask as a new string (allocates: UI and tests, not hot paths).</summary>
    public static string Mask<T>(in T value) where T : IClassifiedData<T>
    {
        int length = T.MaxMaskedLength;
        Span<char> buffer = length <= 256 ? stackalloc char[length] : new char[length];
        return T.TryWriteMasked(in value, buffer, out int written) ? new string(buffer[..written]) : Masks.Hidden;
    }
}
