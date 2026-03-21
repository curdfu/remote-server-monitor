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

Write-Host "安装服务..."
& $exePath install
if ($LASTEXITCODE -ne 0) {
    throw "安装服务失败，退出码：$LASTEXITCODE"
}

Write-Host "启动服务..."
sc.exe start RemoteServerMonitor | Out-Null
Write-Host "服务已安装并设置为开机自启。"
