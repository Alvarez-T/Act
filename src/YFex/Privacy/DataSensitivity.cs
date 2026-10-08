namespace YFex.Privacy;

/// <summary>
/// Protection level of a value (privacy design 02 §2). Closed set: the privacy-aware libraries own the handling of each level.
/// Ordered by protection strength, so "at least X" policies are a comparison.
/// <para>
/// Developers only <em>write</em> the personal levels (<see cref="Personal"/> … <see cref="Secret"/>) in
/// <see cref="SensitiveAttribute"/>. A type without the attribute is not personal data (<see cref="Internal"/>).
/// </para>
/// </summary>
public enum DataSensitivity : byte
{
    /// <summary>Published data: product codes, NCM/CFOP, a company's CNPJ and legal name, list prices.</summary>
    Public = 0,

    /// <summary>Non-personal operational data: ids, amounts, quantities, technical values. The default.</summary>
    Internal = 1,

    /// <summary>LGPD art. 5 I: name, e-mail, phone, address, CEP, birth date.</summary>
    Personal = 2,

    /// <summary>
    /// LGPD art. 5 I, unique official identifiers with fraud and linkage risk: CPF, RG, CNH, passport, NIS/PIS.
    /// CPF is NOT sensitive data in the sense of art. 5 II.
    /// </summary>
    PersonalIdentifier = 3,

    /// <summary>
    /// LGPD art. 5 II / art. 11: health, biometric, genetic, ethnic origin, religion, political opinion, membership, sex life.
    /// Requires a special category (<see cref="DataCategory.IsSpecialCategory"/>).
    /// </summary>
    SensitivePersonal = 4,

    /// <summary>Card number, bank account, Pix key bound to an account: never logged, encrypted or tokenized at rest.</summary>
    Financial = 5,

    /// <summary>Passwords, tokens, API keys, private keys. Not personal data: credentials. Never logged, displayed or exported.</summary>
    Secret = 6,
}

/// <summary>Helpers over <see cref="DataSensitivity"/>.</summary>
public static class DataSensitivityExtensions
{
    /// <summary>
    /// Personal data in the LGPD sense (a natural person can be identified). <see cref="DataSensitivity.Secret"/> is excluded:
    /// credentials are protected for security reasons, not as personal data.
    /// </summary>
    public static bool IsPersonal(this DataSensitivity sensitivity)
        => sensitivity is >= DataSensitivity.Personal and <= DataSensitivity.Financial;

    /// <summary>Values that never leave the process in any form, not even masked (sensitive, financial, secret).</summary>
    public static bool IsNeverRecorded(this DataSensitivity sensitivity) => sensitivity >= DataSensitivity.SensitivePersonal;

    /// <summary>The stricter of two levels.</summary>
    public static DataSensitivity Max(this DataSensitivity left, DataSensitivity right) => left >= right ? left : right;
}
