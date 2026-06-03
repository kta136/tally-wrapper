using System.Security.Cryptography;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Contracts.Maintenance;

namespace ShowroomBilling.Api.Security;

public sealed class MaintenanceTokenStore
{
    public string TokenPath => Path.Combine(
        DatabaseConfigurationStore.DirectoryPath,
        MaintenanceTokenConstants.FileName);

    private string LegacyNestedTokenPath => Path.Combine(
        DatabaseConfigurationStore.DirectoryPath,
        "ShowroomBilling",
        MaintenanceTokenConstants.FileName);

    public bool Validate(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        foreach (var path in new[] { TokenPath, LegacyNestedTokenPath })
        {
            if (ValidateFromPath(path, provided))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValidateFromPath(string path, string provided)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var expected = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(provided.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
