using System.IO;
using System.Text.Json;

namespace ShowroomBilling.Desktop.Services;

public interface ISetupWizardCompletionStore
{
    bool IsComplete();

    Task MarkCompleteAsync(CancellationToken cancellationToken = default);
}

public sealed class SetupWizardCompletionStore : ISetupWizardCompletionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShowroomBilling");

    public static string FilePath => Path.Combine(DirectoryPath, "setup-wizard.json");

    public bool IsComplete()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<SetupWizardMarker>(
                File.ReadAllText(FilePath),
                JsonOptions);
            return marker?.Completed == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task MarkCompleteAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var marker = new SetupWizardMarker(true, DateTimeOffset.UtcNow);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, marker, JsonOptions, cancellationToken);
    }

    private sealed record SetupWizardMarker(bool Completed, DateTimeOffset CompletedAtUtc);
}
