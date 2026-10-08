using System.Runtime.CompilerServices;
using YFex.Diagnostics;

namespace YFex.Installations;

internal static class InstallationLogValues
{
#pragma warning disable CA2255 // Same startup registration convention as the other core value types.
    [ModuleInitializer]
    internal static void Register() => LogValueWriters.Register<InstallationId>();
#pragma warning restore CA2255
}
