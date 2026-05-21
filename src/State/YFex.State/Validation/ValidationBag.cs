using System.Collections.Generic;

namespace YFex.State.Validation;

/// <summary>
/// Stores the latest <see cref="ValidationResult"/> per property name.
/// <see cref="ErrorCount"/> is an O(1) counter, not a scan, so <see cref="HasErrors"/> is O(1).
/// <see cref="ValidationChanged"/> fires whenever any property's validation state changes,
/// allowing MVVM adapters (<c>INotifyDataErrorInfo</c>) and Blazor bridges to react.
/// </summary>
public sealed class ValidationBag
{
    private readonly Dictionary<string, ValidationResult> _results = new();
    private int _errorCount;

    /// <summary>Fires when a property's validation result is set or cleared. Argument is the property name.</summary>
    public event Action<string>? ValidationChanged;

    public bool HasErrors => _errorCount > 0;
    public int ErrorCount => _errorCount;

    /// <summary>Stores a validation failure for <paramref name="propertyName"/>.</summary>
    public void Set(string propertyName, string message,
        ValidationSeverity severity = ValidationSeverity.Error)
    {
        bool wasError = _results.TryGetValue(propertyName, out var prev)
            && !prev.IsSuccess && prev.Severity == ValidationSeverity.Error;
        bool isError = severity == ValidationSeverity.Error;

        _results[propertyName] = new ValidationResult(message, propertyName, severity);

        if (isError && !wasError) _errorCount++;
        else if (!isError && wasError) _errorCount--;

        ValidationChanged?.Invoke(propertyName);
    }

    /// <summary>Clears the validation result for <paramref name="propertyName"/>.</summary>
    public void Clear(string propertyName)
    {
        if (!_results.TryGetValue(propertyName, out var prev)) return;

        if (!prev.IsSuccess && prev.Severity == ValidationSeverity.Error) _errorCount--;
        _results.Remove(propertyName);

        ValidationChanged?.Invoke(propertyName);
    }

    /// <summary>Clears all validation results and fires a single event with an empty string.</summary>
    public void ClearAll()
    {
        _results.Clear();
        _errorCount = 0;
        ValidationChanged?.Invoke(string.Empty);
    }

    /// <summary>Returns the error message for <paramref name="propertyName"/>, or null if valid.</summary>
    public string? GetError(string propertyName) =>
        _results.TryGetValue(propertyName, out var r) && !r.IsSuccess ? r.Message : null;

    /// <summary>
    /// Returns validation messages for <paramref name="propertyName"/>.
    /// Null or empty returns all messages — suitable for entity-level error display.
    /// Compatible with <c>INotifyDataErrorInfo.GetErrors</c>.
    /// </summary>
    public IEnumerable<string> GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            foreach (var r in _results.Values)
                if (!r.IsSuccess) yield return r.Message!;
        }
        else if (_results.TryGetValue(propertyName, out var r) && !r.IsSuccess)
        {
            yield return r.Message!;
        }
    }

    public IEnumerable<ValidationResult> All => _results.Values;
}
