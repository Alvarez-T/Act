using System.Runtime.InteropServices;

namespace YFex.Privacy;

/// <summary>The role a classified value plays (privacy design 02 §4). Closed set.</summary>
[Flags]
public enum DataClassificationFlags : byte
{
    None = 0,

    /// <summary>Surrogate id of a data subject (<c>CustomerId</c>): locates the subject's records.</summary>
    SubjectKey = 1 << 0,

    /// <summary>Natural identifier (CPF, e-mail, phone): resolves to a data subject.</summary>
    Identifier = 1 << 1,

    /// <summary>The classification depends on the value (unions): call <see cref="IClassifiedData{TSelf}.Classify"/>.</summary>
    VariesByValue = 1 << 2,

    /// <summary>Unstructured text: scrubbed when it leaves the process.</summary>
    FreeText = 1 << 3,

    /// <summary>Decided by a naming convention, not declared.</summary>
    ByConvention = 1 << 4,

    /// <summary>Declared on a record/entity type: every member is treated at least at this level (02 §5.5).</summary>
    RecordLevel = 1 << 5,
}

/// <summary>
/// Sensitivity + category + flags of a value, in 4 bytes. Passed by value everywhere; equality is structural.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DataClassification : IEquatable<DataClassification>
{
    private readonly DataSensitivity _sensitivity;
    private readonly DataClassificationFlags _flags;
    private readonly ushort _categoryId;

    public DataClassification(DataSensitivity sensitivity, DataCategory category, DataClassificationFlags flags = DataClassificationFlags.None)
    {
        ArgumentNullException.ThrowIfNull(category);
        _sensitivity = sensitivity;
        _categoryId = category.Id;
        _flags = flags;
    }

    /// <summary>Not personal data (<see cref="DataSensitivity.Internal"/>, <see cref="DataCategory.None"/>).</summary>
    public static DataClassification NotPersonal => new(DataSensitivity.Internal, DataCategory.None);

    public DataSensitivity Sensitivity => _sensitivity;

    public ushort CategoryId => _categoryId;

    public DataCategory Category => DataCategory.FromId(_categoryId);

    public DataClassificationFlags Flags => _flags;

    /// <summary>Personal data in the LGPD sense.</summary>
    public bool IsPersonal => _sensitivity.IsPersonal();

    public bool Has(DataClassificationFlags flag) => (_flags & flag) != 0;

    /// <summary>The stricter of the two (sensitivity wins; the stricter one's category is kept; flags are combined).</summary>
    public static DataClassification Max(DataClassification left, DataClassification right)
    {
        DataClassification stricter = right._sensitivity > left._sensitivity ? right : left;
        return new DataClassification(stricter._sensitivity, stricter.Category, left._flags | right._flags);
    }

    /// <summary>This classification raised to at least <paramref name="floor"/> (record-level classification).</summary>
    public DataClassification AtLeast(DataSensitivity floor)
        => floor > _sensitivity ? new DataClassification(floor, Category, _flags) : this;

    public bool Equals(DataClassification other)
        => _sensitivity == other._sensitivity && _categoryId == other._categoryId && _flags == other._flags;

    public override bool Equals(object? obj) => obj is DataClassification other && Equals(other);

    public override int GetHashCode() => (int)_sensitivity | (_categoryId << 8) | ((int)_flags << 24);

    public static bool operator ==(DataClassification left, DataClassification right) => left.Equals(right);

    public static bool operator !=(DataClassification left, DataClassification right) => !left.Equals(right);

    public override string ToString() => _flags == DataClassificationFlags.None
        ? $"{_sensitivity}/{Category.Key}"
        : $"{_sensitivity}/{Category.Key} [{_flags}]";
}
