param(
    [switch]$IncludeNodeModules
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

$directoriesToRemove = New-Object System.Collections.Generic.List[string]

$directoriesToRemove.Add((Join-Path $repoRoot 'artifacts'))
$directoriesToRemove.Add((Join-Path $repoRoot '.dotnet-cli-home'))

Get-ChildItem -Path $repoRoot -Directory -Recurse -Force |
    Where-Object { $_.Name -in @('bin', 'obj', 'dist') } |
    ForEach-Object { $directoriesToRemove.Add($_.FullName) }

if ($IncludeNodeModules) {
    Get-ChildItem -Path $repoRoot -Directory -Recurse -Force |
        Where-Object { $_.Name -eq 'node_modules' } |
        ForEach-Object { $directoriesToRemove.Add($_.FullName) }
}

$uniqueDirectories = $directoriesToRemove |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique

foreach ($directory in $uniqueDirectories) {
    if (Test-Path $directory) {
        Write-Host "删除：$directory"
        Remove-Item -Path $directory -Recurse -Force
    }
}

Write-Host '清理完成。'
