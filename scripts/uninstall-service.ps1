param(
    [string]$PublishDir = "$(Join-Path $PSScriptRoot '..\artifacts\publish\win-x64')",
    [string]$ExecutableName = 'Monitor.Service.exe'
)

$ErrorActionPreference = 'Stop'

$publishDir = [System.IO.Path]::GetFullPath($PublishDir)
$exePath = Join-Path $publishDir $ExecutableName

if (-not (Test-Path $exePath)) {
    throw "未找到可执行文件：$exePath"
}

Write-Host "卸载服务..."
& $exePath uninstall
if ($LASTEXITCODE -ne 0) {
    throw "卸载服务失败，退出码：$LASTEXITCODE"
}

Write-Host "服务已卸载。"
