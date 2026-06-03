[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'ShowroomBilling.Api',
    [string]$FirewallDisplayName = 'Showroom Billing API LAN',
    [switch]$RemoveConfig
)

$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ShouldProcess($ServiceName, 'Uninstall Windows service')) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus('Stopped', '00:00:20')
        }
        sc.exe delete $ServiceName | Out-Null
    }

    Get-NetFirewallRule -DisplayName $FirewallDisplayName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule

    if ($RemoveConfig -and (Test-Path 'C:\ProgramData\ShowroomBilling')) {
        Remove-Item -Recurse -Force 'C:\ProgramData\ShowroomBilling'
    }

    Write-Host "Uninstalled $ServiceName."
}
