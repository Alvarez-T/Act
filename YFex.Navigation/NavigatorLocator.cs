namespace YFex.MVVM.Providers;

public static class NavigatorLocator
{
    private static INavigatorProvider? _navigatorProvider;

    public static void CreateLocator(INavigatorProvider navigatorProvider)
    {
        if (_navigatorProvider is not null)
            throw new LocatorAlreadyRegisteredException(nameof(ViewModelLocator));

        _navigatorProvider = navigatorProvider;
    }

    public static TNavigator GetNavigator<TNavigator>() where TNavigator : class, INavigator
        => _navigatorProvider?.GetNavigator<TNavigator>() ?? throw new LocatorNotRegisteredException(nameof(ViewModelLocator));

    public static INavigator GetDefaultNavigator()
        => _navigatorProvider?.GetDefaultNavigator() ?? throw new LocatorNotRegisteredException(nameof(ViewModelLocator));
}