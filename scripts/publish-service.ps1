param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "$(Join-Path $PSScriptRoot '..\artifacts\publish\win-x64')"
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$frontendDir = Join-Path $repoRoot 'src\Monitor.Frontend'
$serviceProject = Join-Path $repoRoot 'src\Monitor.Service\Monitor.Service.csproj'
$outputDir = [System.IO.Path]::GetFullPath($OutputDir)
$publishWwwroot = Join-Path $outputDir 'wwwroot'
$installScriptSource = Join-Path $PSScriptRoot 'install-service.ps1'
$uninstallScriptSource = Join-Path $PSScriptRoot 'uninstall-service.ps1'

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:MSBuildEnableWorkloadResolver = 'false'

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null

Write-Host "[1/3] 构建前端..."
Push-Location $frontendDir
try {
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) {
        throw "前端构建失败，退出码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host "[2/3] 发布服务..."
& dotnet publish $serviceProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $outputDir
if ($LASTEXITCODE -ne 0) {
    throw "服务发布失败，退出码：$LASTEXITCODE"
}

Write-Host "[3/3] 复制前端静态文件..."
if (Test-Path $publishWwwroot) {
    Remove-Item -Path $publishWwwroot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishWwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $frontendDir 'dist\*') -Destination $publishWwwroot -Recurse -Force
Copy-Item -Path $installScriptSource -Destination (Join-Path $outputDir 'install-service.ps1') -Force
Copy-Item -Path $uninstallScriptSource -Destination (Join-Path $outputDir 'uninstall-service.ps1') -Force

Write-Host "发布完成： $outputDir"
Write-Host "可执行文件： $(Join-Path $outputDir 'Monitor.Service.exe')"

