# Removes what TallyWrapper.Server.exe installs on the Tally server.
#
# The tray is the only installer (run TallyWrapper.Server.exe to install or
# repair). It has no uninstall, so this script is its counterpart: keep the
# names below in step with ServerInstaller.cs and ServerTrayOptions.cs.
#
# Run it elevated, signed in as the user whose tray starts at sign-in, and
# exit the tray icon first. Running TallyWrapper.Server.exe again reinstalls.
#
# Examples:
#   .\tools\uninstall-server-api-service.ps1
#   .\tools\uninstall-server-api-service.ps1 -RemoveConfig   # also deletes the saved database connection, logs and the API binary

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'ShowroomBilling.Api',
    [string]$ConfigRoot = 'C:\ProgramData\ShowroomBilling',
    [switch]$RemoveConfig
)

$ErrorActionPreference = 'Stop'

# ServerInstaller.FirewallDisplayName and LegacyFirewallDisplayName.
$firewallRules = @('Tally Wrapper API LAN', 'Showroom Billing API LAN')
# ServerInstaller.RunValueName, under the signed-in user's Run key.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'ShowroomBilling.ServerTray'

if ($PSCmdlet.ShouldProcess($ServiceName, 'Uninstall Windows service')) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus('Stopped', '00:00:20')
        }
        sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc delete failed with exit code $LASTEXITCODE" }
    }

    foreach ($rule in $firewallRules) {
        Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }

    if (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name $runValue
    }

    Write-Host "Uninstalled $ServiceName, its firewall rule and the tray's start-up entry."
}

if ($RemoveConfig -and (Test-Path -LiteralPath $ConfigRoot)) {
    # The folder holds the saved database connection, the maintenance token,
    # the logs and the API binary. Only delete a folder the tray laid out.
    $resolved = (Resolve-Path -LiteralPath $ConfigRoot).Path
    $looksInstalled =
        (Test-Path -LiteralPath (Join-Path $resolved 'bin\ShowroomBilling.Api.exe')) -or
        (Test-Path -LiteralPath (Join-Path $resolved 'maintenance_token.txt'))
    if (-not $looksInstalled) {
        throw "'$resolved' does not look like a Tally Wrapper server folder (no bin\ShowroomBilling.Api.exe, no maintenance_token.txt). Nothing was deleted."
    }

    if ($PSCmdlet.ShouldProcess($resolved, 'Delete the server configuration folder')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Host "Deleted $resolved."
    }
}
elseif (-not $RemoveConfig) {
    Write-Host "Kept $ConfigRoot (saved database connection, logs, API binary). Pass -RemoveConfig to delete it."
}
