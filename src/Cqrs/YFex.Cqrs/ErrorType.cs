namespace YFex.Cqrs;

public enum ErrorType
{
    None,
    NotFound,
    Fail,
    Unauthorized,
    ValidationProblem,
    Conflict
}
