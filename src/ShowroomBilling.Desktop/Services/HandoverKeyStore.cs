using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// The hand-over key of this PC: 32 bytes, typed as 43 characters of base64url. It is kept in
/// <c>handover-key.dat</c> under <see cref="AppPaths.LocalDirectory"/>, protected with Windows DPAPI
/// for the signed-in Windows user, and never in the cloud settings, the database or a log. With no
/// key, hand-over is off. See docs/14_settings_storage_contract.md.
/// </summary>
public sealed class HandoverKeyStore
{
    public const int KeyLength = 32;
    public const int KeyTextLength = 43;
    public const string FileName = "handover-key.dat";

    // Ties the protected bytes to this use, so the file cannot stand in for another DPAPI blob.
    private static readonly byte[] Entropy = "ShowroomBilling.HandoverKey.v1"u8.ToArray();

    private readonly string _path;
    private readonly object _gate = new();

    public HandoverKeyStore()
        : this(Path.Combine(AppPaths.LocalDirectory, FileName))
    {
    }

    internal HandoverKeyStore(string path)
    {
        _path = path;
    }

    /// <summary>Raised after the key is saved or removed.</summary>
    public event EventHandler? Changed;

    public bool HasKey => Load() is not null;

    /// <summary>The key's 32 bytes, or null when this PC has none or the file cannot be read by this Windows user.</summary>
    public byte[]? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                var key = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                return key.Length == KeyLength ? key : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>Saves a key typed or pasted by the operator. Returns the sentence to show when it is not a key.</summary>
    public string? Save(string? keyText)
    {
        if (!TryParse(keyText, out var key, out var problem))
        {
            return problem;
        }

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            File.Move(temporary, _path, overwrite: true);
            CryptographicOperations.ZeroMemory(key);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public void Remove()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A key is 43 characters of base64url (letters, digits, - and _) that decode to 32 bytes.</summary>
    public static bool TryParse(string? keyText, out byte[] key, out string problem)
    {
        key = [];
        problem = "A hand-over key is 43 letters, digits, - and _ characters. Copy it again.";
        var text = keyText?.Trim() ?? string.Empty;
        if (text.Length != KeyTextLength || !HandoverLinkReader.IsBase64Url(text))
        {
            return false;
        }

        var bytes = new byte[KeyLength];
        if (!Base64Url.TryDecodeFromChars(text, bytes, out var written) || written != KeyLength)
        {
            return false;
        }

        key = bytes;
        problem = string.Empty;
        return true;
    }
}
