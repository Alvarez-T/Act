namespace YFex.MVVM.Exceptions;

public class LocatorNotRegisteredException : Exception
{
    public LocatorNotRegisteredException(string locatorName)
        : base($"The {locatorName} was not registered. Ensure that Locators are registered through NavigatRExtensions.ConfigureLocators() or simply by calling the CreateLocator() method of the Locator")
    {
        
    }
}