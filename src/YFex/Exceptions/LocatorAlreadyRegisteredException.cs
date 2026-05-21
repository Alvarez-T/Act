namespace YFex.MVVM.Exceptions;

public class LocatorAlreadyRegisteredException : Exception
{
    public LocatorAlreadyRegisteredException(string locatorName)
        : base($"The {locatorName} is already registered. Only one registration of Locators is allowed")
    {
        
    }
}