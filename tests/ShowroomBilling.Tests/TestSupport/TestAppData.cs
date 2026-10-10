using System.Runtime.CompilerServices;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Tests.TestSupport;

/// <summary>
/// Points the app's data folders at a scratch folder before any test runs.
/// The contract tests boot the real API, which would otherwise read the saved
/// database connection of whoever runs them and write its logs into their
/// profile.
/// </summary>
internal static class TestAppData
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "ShowroomBilling.Tests", Guid.NewGuid().ToString("N"));

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
