using System.Text.Json;

namespace ShowroomBilling.Tests;

public sealed class ConfigFilesTests
{
    [Fact]
    public void Desktop_Local_Config_Contains_Only_Allowed_Sections()
    {
        var desktopSections = ReadTopLevelSections("src/ShowroomBilling.Desktop/appsettings.json");

        Assert.Equal(
            ["ChildProcesses", "DesktopBootstrap", "Logging"],
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
    public void Desktop_Xaml_Takes_Its_Colours_From_The_Token_Sheet()
    {
        // CLAUDE.md: "Always use tokens, never hardcode hex." A colour that is
        // not in DesignTokens.xaml should be added there, not inlined in a view.
        var desktop = Path.Combine(FindSolutionRoot(), "src", "ShowroomBilling.Desktop");
        var hexColour = new System.Text.RegularExpressions.Regex("\"#[0-9A-Fa-f]{3,8}\"");
        var offenders = Directory
            .EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && Path.GetFileName(path) != "DesignTokens.xaml")
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(entry => hexColour.IsMatch(entry.line))
                .Select(entry => $"{Path.GetRelativePath(desktop, path)}:{entry.index + 1}: {entry.line.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
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
