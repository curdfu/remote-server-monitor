param(
    [Parameter(Mandatory = $true)]
    [string]$DeployDir,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$ServiceName = 'RemoteServerMonitor',
    [switch]$RestartService = $true,
    [switch]$PreserveAppSettings = $true,
    [string]$NpmCacheDir = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deployment-helpers.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishScript = Join-Path $PSScriptRoot 'publish-service.ps1'
$resolvedDeployDir = Get-DeployFullPath $DeployDir
$deploymentId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')
$stagingDir = Join-Path (Join-Path $repoRoot 'artifacts\deploy-staging') $deploymentId
$deployParent = [System.IO.Directory]::GetParent($resolvedDeployDir)
if ($null -eq $deployParent) { throw 'The deploy directory cannot be a drive root.' }
$backupRoot = Join-Path $deployParent.FullName ((Split-Path -Leaf $resolvedDeployDir) + '.deploy-backups')
$backupDir = Join-Path $backupRoot $deploymentId

Assert-DeploymentPaths $stagingDir $resolvedDeployDir $backupDir
if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) { throw "Publish script not found: $publishScript" }
if (Test-Path -LiteralPath $stagingDir) { throw "Staging directory already exists: $stagingDir" }

Write-Host "Building complete publish output in $stagingDir ..."
$previousNpmCache = [Environment]::GetEnvironmentVariable('npm_config_cache', 'Process')
try {
    if (-not [string]::IsNullOrWhiteSpace($NpmCacheDir)) {
        $env:npm_config_cache = $NpmCacheDir
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publishScript `
        -Configuration $Configuration -Runtime $Runtime -OutputDir $stagingDir
    if ($LASTEXITCODE -ne 0) { throw "publish-service.ps1 failed (exit $LASTEXITCODE). Staging retained: $stagingDir" }
}
finally {
    [Environment]::SetEnvironmentVariable('npm_config_cache', $previousNpmCache, 'Process')
}

# Validation is also repeated by Invoke-SafeDeployment, before querying or stopping the service.
Invoke-SafeDeployment -StageDir $stagingDir -DeployDir $resolvedDeployDir -BackupDir $backupDir `
    -ServiceName $ServiceName -RestartService ([bool]$RestartService) -PreserveAppSettings ([bool]$PreserveAppSettings)
Write-Host "Validated staging output retained: $stagingDir"
