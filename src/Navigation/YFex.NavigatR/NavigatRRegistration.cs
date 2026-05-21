namespace YFex.NavigatR;

public static partial class NavigatRRegistration
{
    public static void RegisterAll(RouteRegistry registry)
        => RegisterGenerated(registry);

    static partial void RegisterGenerated(RouteRegistry registry);
}