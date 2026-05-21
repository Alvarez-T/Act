namespace YFex.Cqrs;

public interface IQueryProvider
{
    public IQuery<TResult> ProvideQueryFor<T, TResult>();
}
