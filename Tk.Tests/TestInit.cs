using System.Runtime.CompilerServices;

namespace Tk.Tests;

internal static class TestInit
{
    [ModuleInitializer]
    public static void DisableAnsi()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
    }
}
