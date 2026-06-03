using System.Text.Json;
using ShowroomBilling.Application.Settings;

namespace ShowroomBilling.Tests;

public sealed class ConfigFilesTests
{
    [Fact]
    public void Desktop_Local_Config_Contains_Only_Allowed_Sections()
    {
        var desktopSections = ReadTopLevelSections("src/ShowroomBilling.Desktop/appsettings.json");

        Assert.Equal(
            ["ChildProcesses", "DesktopBootstrap", "DesktopLocalPreferences", "Logging"],
            desktopSections.OrderBy(section => section));
    }

    [Fact]
    public void Desktop_Api_Child_Process_Is_Pinned_To_Production_Port_And_Environment()
    {
        using var document = ReadJson("src/ShowroomBilling.Desktop/appsettings.json");
        var root = document.RootElement;
        var api = root
            .GetProperty("ChildProcesses")
            .GetProperty("Api");

        Assert.Equal("--urls http://127.0.0.1:5107", api.GetProperty("Arguments").GetString());
        Assert.Equal(
            "LocalEmbedded",
            root.GetProperty("DesktopBootstrap").GetProperty("ConnectionMode").GetString());
        Assert.Equal(
            "http://localhost:5107",
            root.GetProperty("DesktopBootstrap").GetProperty("ServerApiBaseUrl").GetString());
        Assert.Equal(
            "Production",
            api.GetProperty("EnvironmentVariables").GetProperty("ASPNETCORE_ENVIRONMENT").GetString());

        var ports = api.GetProperty("AlreadyRunningProbePorts")
            .EnumerateArray()
            .Select(x => x.GetInt32())
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal([5107, 7107], ports);
    }

    [Fact]
    public void Desktop_Development_Config_Uses_Separate_Dev_Api_Port_And_Environment()
    {
        using var document = ReadJson("src/ShowroomBilling.Desktop/appsettings.Development.json");
        var root = document.RootElement;
        var api = root.GetProperty("ChildProcesses").GetProperty("Api");

        Assert.Equal(
            "http://localhost:5108",
            root.GetProperty("DesktopBootstrap").GetProperty("ApiBaseUrl").GetString());
        Assert.Equal(
            "LocalEmbedded",
            root.GetProperty("DesktopBootstrap").GetProperty("ConnectionMode").GetString());
        Assert.Equal(
            "http://localhost:5108",
            root.GetProperty("DesktopBootstrap").GetProperty("ServerApiBaseUrl").GetString());
        Assert.Equal("--urls http://127.0.0.1:5108", api.GetProperty("Arguments").GetString());
        Assert.Equal(
            "Development",
            api.GetProperty("EnvironmentVariables").GetProperty("ASPNETCORE_ENVIRONMENT").GetString());

        var ports = api.GetProperty("AlreadyRunningProbePorts")
            .EnumerateArray()
            .Select(x => x.GetInt32())
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal([5108, 7108], ports);
    }

    [Fact]
    public void Settings_Storage_Contract_Marks_Shared_Settings_As_Cloud_Owned()
    {
        var contract = new SettingsStorageContract();

        Assert.True(contract.IsCloudOwned("connection.host"));
        Assert.True(contract.IsCloudOwned("numbering.invoice_prefix"));
        Assert.True(contract.IsCloudOwned("print.terms_and_conditions"));
        Assert.True(contract.IsCloudOwned("karat_mapping.rows"));
        Assert.False(contract.IsCloudOwned("desktop.bootstrap.api_base_url"));
    }

    private static IReadOnlyList<string> ReadTopLevelSections(string relativePath)
    {
        using var document = ReadJson(relativePath);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static JsonDocument ReadJson(string relativePath)
    {
        var solutionRoot = FindSolutionRoot();
        var fullPath = Path.Combine(solutionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var json = File.ReadAllText(fullPath);
        return JsonDocument.Parse(json);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShowroomBilling.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate solution root.");
    }
}
