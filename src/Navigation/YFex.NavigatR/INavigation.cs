// INavigation moved to YFex.UI.Abstractions (shared UI contracts), alongside
// IDialog / IToast / IMessageBox / INotification.
// This alias keeps unqualified `INavigation` resolving inside the YFex.NavigatR
// project without touching Navigator / NavigatorHost.
global using INavigation = YFex.UI.Abstractions.INavigation;
