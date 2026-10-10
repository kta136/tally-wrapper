using System.IO;
using System.Runtime.CompilerServices;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>
/// Points the app's data folders at a scratch folder before any test runs.
/// Without this the tests would read, and some would rewrite, the settings of
/// whoever runs them: the saved database connection, print preferences, UI
/// density, the bootstrap override and the device token.
/// </summary>
internal static class TestAppData
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "ShowroomBilling.Desktop.Tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Redirect()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable(AppPaths.LocalDirectoryEnvironmentVariable, Path.Combine(Root, "local"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // A file still open at exit is left for the temp folder clean-up.
            }
        };
    }
}
