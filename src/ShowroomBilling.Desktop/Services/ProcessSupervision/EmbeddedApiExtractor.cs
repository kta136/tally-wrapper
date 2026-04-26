using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace ShowroomBilling.Desktop.Services.ProcessSupervision;

/// <summary>
/// Extracts the API child-process payload (Kestrel host + dependencies) embedded
/// in this assembly as a zip resource. The embedded copy is produced by the publish
/// script and the resulting single-exe ships everything needed to run the API.
///
/// On first launch (or when the embedded payload hash differs from the previously
/// extracted one) we expand to <c>%LOCALAPPDATA%\ShowroomBilling\runtime\api\</c>.
/// Subsequent launches skip extraction and reuse the existing folder.
/// </summary>
public static class EmbeddedApiExtractor
{
    private const string ResourceName = "EmbeddedApi.zip";
    private const string ApiExeName = "ShowroomBilling.Api.exe";

    public sealed record ExtractionResult(string ApiExePath, string WorkingDirectory);

    /// <summary>
    /// Returns the extracted API exe path if an embedded payload is present in this
    /// assembly. Returns null when no resource is embedded (e.g. dev builds), in which
    /// case the caller should fall back to the configured ChildProcesses.Api.ExecutablePath.
    /// </summary>
    public static ExtractionResult? ExtractIfPresent()
    {
        var asm = Assembly.GetExecutingAssembly();

        // Probe once: if the resource isn't present (dev builds), bail before
        // touching disk so we don't add latency for non-prod launches.
        using (var probe = asm.GetManifestResourceStream(ResourceName))
        {
            if (probe is null) return null;
        }

        // Hash the resource via a streaming SHA-256. Earlier code spooled the
        // entire zip (~100-500 MB on prod builds) into a MemoryStream to both
        // hash and re-read for ZipArchive — that doubled peak memory for no
        // reason. Now we hash with no allocation, and only re-open the
        // resource a second time when we actually need to extract.
        string hash;
        using (var hashStream = asm.GetManifestResourceStream(ResourceName)
                                ?? throw new InvalidOperationException("Embedded API resource disappeared between probes."))
        {
            hash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
        }

        var rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShowroomBilling", "runtime", "api");
        var hashFile = Path.Combine(rootDir, ".payload-hash");
        var apiExe = Path.Combine(rootDir, ApiExeName);

        if (File.Exists(apiExe) && File.Exists(hashFile))
        {
            try
            {
                if (string.Equals(File.ReadAllText(hashFile).Trim(), hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new ExtractionResult(apiExe, rootDir);
                }
            }
            catch
            {
                // Fall through to a fresh extraction.
            }
        }

        // Wipe and re-extract. We never overlay a partially-stale extraction because
        // the zip layout may add or remove files between releases.
        if (Directory.Exists(rootDir))
        {
            try { Directory.Delete(rootDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(rootDir);

        using (var resource = asm.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException("Embedded API resource disappeared between probes."))
        using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(rootDir, overwriteFiles: true);
        }
        File.WriteAllText(hashFile, hash);

        if (!File.Exists(apiExe))
        {
            // Should not happen — the publish script always includes the API exe at
            // the zip root. If it does, treat it as no embedded payload and let the
            // caller fall back to its configured path.
            return null;
        }

        return new ExtractionResult(apiExe, rootDir);
    }
}
