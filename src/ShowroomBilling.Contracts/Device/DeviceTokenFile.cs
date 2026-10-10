using System.Security.Cryptography;

namespace ShowroomBilling.Contracts.Device;

/// <summary>
/// The device-token file both processes share. Whichever starts first creates
/// it (normally the Desktop, which then spawns the API); the other reads it.
/// </summary>
public static class DeviceTokenFile
{
    /// <summary>Returns the token stored at <paramref name="path"/>, writing a new 32-byte one if there is none.</summary>
    public static string ReadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, token);
        return token;
    }
}
