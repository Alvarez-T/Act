namespace YFex.Cqrs;

public interface ICommandProvider
{
    public ICommand ProvideCommandFor<T>();
}
