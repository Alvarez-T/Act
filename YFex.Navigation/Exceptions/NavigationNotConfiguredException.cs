namespace YFex.Navigation.Exceptions;

public class NavigationNotConfiguredException() : Exception(
    "The Navigation method was not configured. Ensure that you've configured the INavigation properly");

