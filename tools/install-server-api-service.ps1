[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'ShowroomBilling.Api',
    [string]$DisplayName = 'Tally Wrapper API',
    [string]$ApiPath = (Join-Path $PSScriptRoot '..\publish\server\api\TallyWrapper.Api.exe'),
    [string]$ConfigRoot = 'C:\ProgramData\ShowroomBilling',
    [string]$LanCidr = '192.168.0.0/16',
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$SkipFirewall
)

$ErrorActionPreference = 'Stop'
$resolvedApi = (Resolve-Path $ApiPath).Path

New-Item -ItemType Directory -Force -Path $ConfigRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ConfigRoot 'logs') | Out-Null

$maintenanceTokenPath = Join-Path $ConfigRoot 'maintenance_token.txt'
if (-not (Test-Path $maintenanceTokenPath)) {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes) | Set-Content -Encoding ASCII $maintenanceTokenPath
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Uninstall it first or choose another -ServiceName."
}

$binaryPath = "`"$resolvedApi`""
$newServiceArgs = @{
    Name = $ServiceName
    DisplayName = $DisplayName
    BinaryPathName = $binaryPath
    StartupType = 'Automatic'
    Description = 'Tally Wrapper API service hosted on the Tally server.'
}
if ($PSBoundParameters.ContainsKey('Credential')) {
    $newServiceArgs.Credential = $Credential
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Install Windows service')) {
    New-Service @newServiceArgs | Out-Null

    $envValues = @(
        'ASPNETCORE_ENVIRONMENT=Production',
        'DOTNET_ENVIRONMENT=Production',
        'ASPNETCORE_URLS=http://0.0.0.0:5107',
        "SHOWROOM_BILLING_SERVICE_NAME=$ServiceName",
        "SHOWROOM_BILLING_APPDATA=$ConfigRoot",
        "Logging__File__Directory=$(Join-Path $ConfigRoot 'logs')",
        'Database__AutoMigrateOnStartup=true',
        'DeviceAuth__Mode=TrustedLan',
        "DeviceAuth__TrustedNetworks__0=$LanCidr"
    )
    New-ItemProperty `
        -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
        -Name Environment `
        -PropertyType MultiString `
        -Value $envValues `
        -Force | Out-Null

    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

    if (-not $SkipFirewall) {
        New-NetFirewallRule `
            -DisplayName "$DisplayName LAN" `
            -Direction Inbound `
            -Action Allow `
            -Protocol TCP `
            -LocalPort 5107 `
            -RemoteAddress $LanCidr `
            -Program $resolvedApi `
            -ErrorAction Stop | Out-Null
    }

    Start-Service $ServiceName
    Write-Host "Installed and started $ServiceName."
    Write-Host "Config root: $ConfigRoot"
    Write-Host "Maintenance token: $maintenanceTokenPath"
    Write-Host "API URL: http://<this-server>:5107"
}
