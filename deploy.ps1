param(
    [string]$DeployDir = 'C:\GreenSoft\remote-server-monitor-win-x64',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$ServiceName = 'RemoteServerMonitor',
    [switch]$RestartService = $true,
    [switch]$PreserveAppSettings = $true,
    [string]$NpmCacheDir = 'D:\Code\agent\codex\.npm-cache'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$deployScript = Join-Path $repoRoot 'scripts\publish-and-deploy.ps1'

if (-not (Test-Path $deployScript)) {
    throw "Deploy script not found: $deployScript"
}

$deployArguments = @(
    '-ExecutionPolicy', 'Bypass',
    '-File', $deployScript,
    '-DeployDir', $DeployDir,
    '-Configuration', $Configuration,
    '-Runtime', $Runtime,
    '-ServiceName', $ServiceName,
    '-NpmCacheDir', $NpmCacheDir
)

if ($RestartService) {
    $deployArguments += '-RestartService'
}

if ($PreserveAppSettings) {
    $deployArguments += '-PreserveAppSettings'
}

& powershell @deployArguments

if ($LASTEXITCODE -ne 0) {
    throw "Deployment failed. Exit code: $LASTEXITCODE"
}
