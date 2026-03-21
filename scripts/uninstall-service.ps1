param(
    [string]$PublishDir = $PSScriptRoot,
    [string]$ExecutableName = 'Monitor.Service.exe'
)

$ErrorActionPreference = 'Stop'

$publishDir = [System.IO.Path]::GetFullPath($PublishDir)
$exePath = Join-Path $publishDir $ExecutableName

if (-not (Test-Path $exePath)) {
    throw "未找到可执行文件：$exePath"
}

Write-Host "卸载服务..."
$uninstallProcess = Start-Process -FilePath $exePath -ArgumentList 'uninstall' -Wait -PassThru
$uninstallExitCode = $uninstallProcess.ExitCode
if ($uninstallExitCode -ne 0) {
    throw "卸载服务失败，退出码：$uninstallExitCode"
}

Write-Host "服务已卸载。"
