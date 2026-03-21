param(
    [string]$DeployDir = "$(Join-Path $PSScriptRoot '..\artifacts\publish\win-x64')",
    [string]$WwwrootDir = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$frontendDir = Join-Path $repoRoot 'src\Monitor.Frontend'
$targetWwwroot = if ([string]::IsNullOrWhiteSpace($WwwrootDir)) {
    Join-Path ([System.IO.Path]::GetFullPath($DeployDir)) 'wwwroot'
}
else {
    [System.IO.Path]::GetFullPath($WwwrootDir)
}

Write-Host '[1/2] 构建前端...'
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

Write-Host '[2/2] 发布前端静态文件...'
if (Test-Path $targetWwwroot) {
    Get-ChildItem -Path $targetWwwroot -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $targetWwwroot -Force | Out-Null
}

Copy-Item -Path (Join-Path $frontendDir 'dist\*') -Destination $targetWwwroot -Recurse -Force

Write-Host "前端发布完成：$targetWwwroot"
