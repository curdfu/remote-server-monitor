param(
    [switch]$RemoveLockFile,
    [switch]$SkipBuild,
    [string]$NpmCacheDir = 'D:\Code\agent\codex\.npm-cache'
)

$ErrorActionPreference = 'Stop'

$frontendDir = $PSScriptRoot
$nodeModulesDir = Join-Path $frontendDir 'node_modules'
$packageLockPath = Join-Path $frontendDir 'package-lock.json'

Write-Host "Frontend directory: $frontendDir" -ForegroundColor Cyan

if (Test-Path $nodeModulesDir) {
    Write-Host 'Removing node_modules...' -ForegroundColor Yellow
    Remove-Item -Path $nodeModulesDir -Recurse -Force
}
else {
    Write-Host 'node_modules does not exist. Skip removal.' -ForegroundColor DarkYellow
}

if ($RemoveLockFile -and (Test-Path $packageLockPath)) {
    Write-Host 'Removing package-lock.json...' -ForegroundColor Yellow
    Remove-Item -Path $packageLockPath -Force
}

if (-not [string]::IsNullOrWhiteSpace($NpmCacheDir)) {
    New-Item -ItemType Directory -Force -Path $NpmCacheDir | Out-Null
    $env:npm_config_cache = $NpmCacheDir
    Write-Host "Using npm cache directory: $NpmCacheDir" -ForegroundColor DarkCyan
}

Push-Location $frontendDir
try {
    Write-Host 'Running npm install...' -ForegroundColor Green
    & npm.cmd install
    if ($LASTEXITCODE -ne 0) {
        throw "npm install failed with exit code: $LASTEXITCODE"
    }

    if (-not $SkipBuild) {
        Write-Host 'Running npm run build...' -ForegroundColor Green
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build failed with exit code: $LASTEXITCODE"
        }
    }
}
finally {
    Pop-Location
}

Write-Host 'Frontend dependency repair completed.' -ForegroundColor Green
