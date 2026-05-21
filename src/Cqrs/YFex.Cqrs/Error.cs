using System;
using System.Collections.Generic;
using System.Text;

namespace YFex.Cqrs;

public record ErrorDetail(ErrorType ErrorType, string Message);

public readonly struct Error
{
    public List<ErrorDetail> Details { get; } = new();
    public ErrorType Type => Details.FirstOrDefault()?.ErrorType ?? ErrorType.None;
    public string Message => Details.FirstOrDefault()?.Message ?? string.Empty;

    public Error(ErrorType errorType, string message = "")
    {
        Details = new List<ErrorDetail> { new(errorType, message) };
    }

    public static implicit operator Error(string message) => new(ErrorType.None, message);
}
