using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Registers the <c>tallywrapper:</c> link scheme for the signed-in Windows user, so that Windows
/// starts this exe (or reaches the copy already running) with the link:
/// <c>HKCU\Software\Classes\tallywrapper</c>, with <c>URL Protocol</c> and
/// <c>shell\open\command</c> = <c>"&lt;this exe&gt;" "%1"</c>. It is written at every start of the
/// published app and rewritten when it points elsewhere.
/// <para>
/// Only the published app registers, so that a test, a scratch run or a development build never
/// takes the links away from it. All four must hold: a Release build; the Production environment
/// (<c>DOTNET_ENVIRONMENT</c> unset or Production; <c>run-scratch.ps1</c> and the VS Code launches set
/// Development); no <c>SHOWROOM_BILLING_APPDATA</c> or <c>SHOWROOM_BILLING_LOCALAPPDATA</c> override (the
/// tests and <c>run-scratch.ps1</c> set them); and a single-file exe, which is what
/// <c>tools\publish-prod.ps1</c> makes and a build's <c>bin</c> folder is not.
/// </para>
/// </summary>
public static class HandoverUrlScheme
{
    public const string ClassesKey = @"Software\Classes";

    public static bool ShouldRegister(
        string environmentName,
        string? dataDirectoryOverride,
        string? localDirectoryOverride,
        bool isDebugBuild,
        bool isSingleFile)
        => !isDebugBuild
           && isSingleFile
           && string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrEmpty(dataDirectoryOverride)
           && string.IsNullOrEmpty(localDirectoryOverride);

    /// <summary>The guard above, for this process.</summary>
    public static bool ShouldRegisterThisProcess(string environmentName)
    {
#if DEBUG
        const bool isDebugBuild = true;
#else
        const bool isDebugBuild = false;
#endif
        // A single-file app's assemblies have no file of their own.
#pragma warning disable IL3000
        var isSingleFile = string.IsNullOrEmpty(typeof(HandoverUrlScheme).Assembly.Location);
#pragma warning restore IL3000
        return ShouldRegister(
            environmentName,
            Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable),
            Environment.GetEnvironmentVariable(AppPaths.LocalDirectoryEnvironmentVariable),
            isDebugBuild,
            isSingleFile);
    }

    /// <summary>The command Windows runs for a link: this exe, with the link as its only argument.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>
    /// Writes the scheme under <paramref name="classes"/> (<c>HKCU\Software\Classes</c> for real)
    /// unless it already runs <paramref name="exePath"/>. True when something was written.
    /// </summary>
    public static bool Register(RegistryKey classes, string exePath)
    {
        ArgumentNullException.ThrowIfNull(classes);
        var command = CommandFor(exePath);
        using (var existing = classes.OpenSubKey($@"{HandoverLinkReader.Scheme}\shell\open\command"))
        {
            if (string.Equals(existing?.GetValue(null) as string, command, StringComparison.OrdinalIgnoreCase)
                && classes.OpenSubKey(HandoverLinkReader.Scheme) is { } current)
            {
                using (current)
                {
                    if (current.GetValue("URL Protocol") is string)
                    {
                        return false;
                    }
                }
            }
        }

        using var scheme = classes.CreateSubKey(HandoverLinkReader.Scheme);
        scheme.SetValue(null, "URL:Tally Wrapper hand-over");
        scheme.SetValue("URL Protocol", string.Empty);
        using (var icon = scheme.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue(null, $"\"{exePath}\",0");
        }

        using (var open = scheme.CreateSubKey(@"shell\open\command"))
        {
            open.SetValue(null, command);
        }

        return true;
    }
}
