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

Write-Host "安装服务..."
$installProcess = Start-Process -FilePath $exePath -ArgumentList 'install' -Wait -PassThru
$installExitCode = $installProcess.ExitCode
if ($installExitCode -notin @(0, 11)) {
    throw "安装服务失败，退出码：$installExitCode"
}

if ($installExitCode -eq 11) {
    Write-Host "服务已存在，跳过创建。"
}

$serviceStatus = sc.exe query RemoteServerMonitor
if ($serviceStatus -match 'STATE\s*:\s*4\s+RUNNING') {
    Write-Host "服务已在运行。"
    return
}

Write-Host "启动服务..."
sc.exe start RemoteServerMonitor | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "启动服务失败，退出码：$LASTEXITCODE"
}

Write-Host "服务已安装并设置为开机自启。"
