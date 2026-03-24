param(
    [Parameter(Mandatory = $true)]
    [string]$DeployDir,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$ServiceName = 'RemoteServerMonitor',
    [switch]$RestartService = $true,
    [switch]$PreserveAppSettings = $true,
    [string]$NpmCacheDir = 'D:\Code\agent\codex\.npm-cache'
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==== $Message ====" -ForegroundColor Cyan
}

function Get-ServiceStateSafe {
    param([string]$Name)

    $output = & sc.exe query $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        return [PSCustomObject]@{
            Exists = $false
            State = 'UNKNOWN'
            Raw = ($output | Out-String)
        }
    }

    $stateLine = ($output | Out-String) -split "`r?`n" |
        Where-Object { $_ -match 'STATE\s*:' } |
        Select-Object -First 1

    $state = if ($stateLine -match 'STATE\s*:\s*\d+\s+([A-Z_]+)') {
        $Matches[1]
    }
    else {
        'UNKNOWN'
    }

    return [PSCustomObject]@{
        Exists = $true
        State = $state
        Raw = ($output | Out-String)
    }
}

function Wait-ForServiceState {
    param(
        [string]$Name,
        [string]$ExpectedState,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $serviceState = Get-ServiceStateSafe -Name $Name
        if ($serviceState.Exists -and $serviceState.State -eq $ExpectedState) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "Timed out waiting for service '$Name' to reach state '$ExpectedState'."
}

function Stop-ServiceSafe {
    param([string]$Name)

    $serviceState = Get-ServiceStateSafe -Name $Name
    if (-not $serviceState.Exists) {
        Write-Host "Service $Name does not exist. Skip stop."
        return $false
    }

    if ($serviceState.State -eq 'STOPPED') {
        Write-Host "Service $Name is already stopped."
        return $true
    }

    Write-Host "Stopping service $Name ..."
    & sc.exe stop $Name | Out-Null
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1062) {
        throw "Failed to stop service $Name. Exit code: $LASTEXITCODE"
    }

    Wait-ForServiceState -Name $Name -ExpectedState 'STOPPED'
    return $true
}

function Start-ServiceSafe {
    param([string]$Name)

    $serviceState = Get-ServiceStateSafe -Name $Name
    if (-not $serviceState.Exists) {
        Write-Host "Service $Name does not exist. Skip start."
        return
    }

    Write-Host "Starting service $Name ..."
    & sc.exe start $Name | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start service $Name. Exit code: $LASTEXITCODE"
    }

    Wait-ForServiceState -Name $Name -ExpectedState 'RUNNING'
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$publishScript = Join-Path $PSScriptRoot 'publish-service.ps1'
$stagingDir = Join-Path $repoRoot 'artifacts\publish\win-x64-deploy-temp'
$resolvedDeployDir = [System.IO.Path]::GetFullPath($DeployDir)
$targetWwwroot = Join-Path $resolvedDeployDir 'wwwroot'
$stagingWwwroot = Join-Path $stagingDir 'wwwroot'
$targetAppSettings = Join-Path $resolvedDeployDir 'appsettings.json'
$stagingAppSettings = Join-Path $stagingDir 'appsettings.json'

if (-not (Test-Path $publishScript)) {
    throw "Publish script not found: $publishScript"
}

if (-not [string]::IsNullOrWhiteSpace($NpmCacheDir)) {
    New-Item -ItemType Directory -Force -Path $NpmCacheDir | Out-Null
    $env:npm_config_cache = $NpmCacheDir
}

Write-Step 'Build publish artifacts'
if (Test-Path $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force
}

& powershell -ExecutionPolicy Bypass -File $publishScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputDir $stagingDir

if ($LASTEXITCODE -ne 0) {
    throw "publish-service.ps1 failed. Exit code: $LASTEXITCODE"
}

Write-Step 'Prepare deploy directory'
New-Item -ItemType Directory -Force -Path $resolvedDeployDir | Out-Null

$serviceWasHandled = $false
if ($RestartService) {
    $serviceWasHandled = Stop-ServiceSafe -Name $ServiceName
}

Write-Step 'Copy backend files'
Get-ChildItem -Path $stagingDir -File | ForEach-Object {
    if ($PreserveAppSettings -and $_.Name -eq 'appsettings.json' -and (Test-Path $targetAppSettings)) {
        Write-Host 'Keep existing appsettings.json. Skip overwrite.'
        return
    }

    Copy-Item -Path $_.FullName -Destination (Join-Path $resolvedDeployDir $_.Name) -Force
}

Write-Step 'Copy frontend static files'
if (Test-Path $targetWwwroot) {
    Get-ChildItem -Path $targetWwwroot -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Force -Path $targetWwwroot | Out-Null
}

Copy-Item -Path (Join-Path $stagingWwwroot '*') -Destination $targetWwwroot -Recurse -Force

if (-not $PreserveAppSettings -and (Test-Path $stagingAppSettings)) {
    Copy-Item -Path $stagingAppSettings -Destination $targetAppSettings -Force
}

if ($RestartService -and $serviceWasHandled) {
    Write-Step 'Restart service'
    Start-ServiceSafe -Name $ServiceName
}

Write-Step 'Deploy complete'
Write-Host "Staging directory: $stagingDir"
Write-Host "Deploy directory: $resolvedDeployDir"
Write-Host "Frontend directory: $targetWwwroot"
if ($RestartService) {
    $finalState = Get-ServiceStateSafe -Name $ServiceName
    if ($finalState.Exists) {
        Write-Host "Service state: $($finalState.State)"
    }
}

