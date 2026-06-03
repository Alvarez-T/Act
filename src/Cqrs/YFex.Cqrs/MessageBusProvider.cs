//using Wolverine;

//namespace YFex.Cqrs;

//internal static class MessageBusProvider
//{
//    private static readonly AsyncLocal<IMessageBus?> _scope = new();

//    // Accessor for Facades
//    internal static IMessageBus Current => _scope.Value
//        ?? throw new InvalidOperationException("Wolverine Bus is not active. Did you forget 'app.UseStaticWolverine()'? in Program.cs?");

//    // Internal methods for Middleware & Tests
//    internal static void Set(IMessageBus bus) => _scope.Value = bus;
//    internal static void Clear() => _scope.Value = null;
//}