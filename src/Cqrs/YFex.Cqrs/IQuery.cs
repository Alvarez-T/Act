namespace YFex.Cqrs;

public interface IQuery<out TResult> : IQueryFaker<TResult>
{
}
