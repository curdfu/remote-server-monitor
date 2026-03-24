param(
    [string]$BaseUrl = 'http://127.0.0.1:5188',
    [string]$ServiceName = 'RemoteServerMonitor',
    [int]$SampleCount = 6,
    [int]$IntervalSeconds = 1,
    [int]$HistoryWindowMinutes = 15,
    [int]$TopN = 10,
    [string]$TrafficUrl = '',
    [int]$TrafficRequests = 3,
    [switch]$PauseForManualTraffic,
    [string]$ReportPath = ''
)

$ErrorActionPreference = 'Stop'

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "==== $Title ====" -ForegroundColor Cyan
}

function Format-Bytes {
    param([double]$Value)

    if ($Value -lt 1024) { return ('{0:N0} B' -f $Value) }
    if ($Value -lt 1MB) { return ('{0:N2} KB' -f ($Value / 1KB)) }
    if ($Value -lt 1GB) { return ('{0:N2} MB' -f ($Value / 1MB)) }
    return ('{0:N2} GB' -f ($Value / 1GB))
}

function Invoke-JsonGet {
    param([string]$Url)

    $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 10
    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        throw "Empty response: $Url"
    }

    return $response.Content | ConvertFrom-Json
}

function Test-IsLocalUrl {
    param([string]$Url)

    try {
        $uri = [Uri]$Url
        return $uri.Host -in @('127.0.0.1', 'localhost', '::1')
    }
    catch {
        return $false
    }
}

function Get-ServiceQueryOutput {
    param([string]$Name)

    $output = & sc.exe query $Name 2>&1
    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output | Out-String)
    }
}

function Get-ServiceConfigOutput {
    param([string]$Name)

    $output = & sc.exe qc $Name 2>&1
    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output | Out-String)
    }
}

function Get-ServiceState {
    param([string]$Name)

    $result = Get-ServiceQueryOutput -Name $Name
    if ($result.ExitCode -ne 0) {
        return [PSCustomObject]@{
            Exists = $false
            State = 'UNKNOWN'
            Raw = $result.Output.Trim()
        }
    }

    $stateLine = $result.Output -split "`r?`n" |
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
        Raw = $result.Output.Trim()
    }
}

function Get-ServiceExecutableDirectory {
    param([string]$Name)

    $result = Get-ServiceConfigOutput -Name $Name
    if ($result.ExitCode -ne 0) {
        return $null
    }

    $line = $result.Output -split "`r?`n" |
        Where-Object { $_ -match 'BINARY_PATH_NAME' } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($line)) {
        return $null
    }

    if ($line -match 'BINARY_PATH_NAME\s*:\s*"([^"]+)"') {
        return Split-Path -Parent $Matches[1]
    }

    if ($line -match 'BINARY_PATH_NAME\s*:\s*([^\s]+)') {
        return Split-Path -Parent $Matches[1]
    }

    return $null
}

function Get-RecentEtwFailures {
    param([string]$LogDirectory)

    if ([string]::IsNullOrWhiteSpace($LogDirectory) -or -not (Test-Path $LogDirectory)) {
        return @()
    }

    $patterns = @(
        'Failed to start ETW network collector',
        'insufficient privileges',
        'insufficient resources'
    )

    $latestLogs = Get-ChildItem -Path $LogDirectory -Filter 'monitor-*.log' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 3

    if (-not $latestLogs) {
        return @()
    }

    $matches = foreach ($file in $latestLogs) {
        Select-String -Path $file.FullName -Pattern $patterns -SimpleMatch |
            ForEach-Object {
                [PSCustomObject]@{
                    File = $file.Name
                    Line = $_.Line.Trim()
                }
            }
    }

    return @($matches)
}

function Sample-Realtime {
    param(
        [string]$ResolvedBaseUrl,
        [int]$Count,
        [int]$DelaySeconds
    )

    $items = New-Object System.Collections.Generic.List[object]

    for ($index = 1; $index -le $Count; $index++) {
        $payload = Invoke-JsonGet -Url "$ResolvedBaseUrl/api/network/realtime"

        $items.Add([PSCustomObject]@{
            Index = $index
            SampleTime = $payload.sampleTime
            TotalUploadBytesPerSecond = [double]$payload.totalUploadBytesPerSecond
            TotalDownloadBytesPerSecond = [double]$payload.totalDownloadBytesPerSecond
            WanUploadBytesPerSecond = [double]$payload.wanUploadBytesPerSecond
            WanDownloadBytesPerSecond = [double]$payload.wanDownloadBytesPerSecond
            LanUploadBytesPerSecond = [double]$payload.lanUploadBytesPerSecond
            LanDownloadBytesPerSecond = [double]$payload.lanDownloadBytesPerSecond
        }) | Out-Null

        if ($index -lt $Count) {
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    return $items.ToArray()
}

function Invoke-TrafficGeneration {
    param(
        [string]$Url,
        [int]$RequestCount
    )

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return @()
    }

    $results = New-Object System.Collections.Generic.List[object]

    for ($index = 1; $index -le $RequestCount; $index++) {
        $tempFile = Join-Path $env:TEMP ("monitor-etw-check-{0}-{1}.tmp" -f $PID, $index)
        try {
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30 -OutFile $tempFile | Out-Null
            $stopwatch.Stop()

            $size = if (Test-Path $tempFile) { (Get-Item $tempFile).Length } else { 0 }
            $results.Add([PSCustomObject]@{
                Index = $index
                Url = $Url
                Bytes = $size
                DurationMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 0)
                Success = $true
                Error = $null
            }) | Out-Null
        }
        catch {
            $results.Add([PSCustomObject]@{
                Index = $index
                Url = $Url
                Bytes = 0
                DurationMs = 0
                Success = $false
                Error = $_.Exception.Message
            }) | Out-Null
        }
        finally {
            Remove-Item -Path $tempFile -Force -ErrorAction SilentlyContinue
        }
    }

    return $results.ToArray()
}

$resolvedBaseUrl = $BaseUrl.TrimEnd('/')
$report = [ordered]@{
    CheckedAt = (Get-Date).ToString('o')
    BaseUrl = $resolvedBaseUrl
    Service = $null
    Health = $null
    RecentEtwFailures = @()
    BaselineRealtimeSamples = @()
    TrafficGeneration = @()
    ActiveRealtimeSamples = @()
    AppSummaries = @()
    Verdict = [ordered]@{}
}

Write-Section 'ETW network verification'
Write-Host "BaseUrl: $resolvedBaseUrl"
Write-Host "SampleCount: $SampleCount, IntervalSeconds: $IntervalSeconds, HistoryWindowMinutes: $HistoryWindowMinutes"

$isLocalUrl = Test-IsLocalUrl -Url $resolvedBaseUrl
if ($isLocalUrl) {
    Write-Section 'Local service status'
    $serviceState = Get-ServiceState -Name $ServiceName
    $report.Service = $serviceState
    Write-Host $serviceState.Raw

    $serviceDirectory = Get-ServiceExecutableDirectory -Name $ServiceName
    if ($serviceDirectory) {
        $logDirectory = Join-Path $serviceDirectory 'logs'
        $recentFailures = Get-RecentEtwFailures -LogDirectory $logDirectory
        $report.RecentEtwFailures = $recentFailures
        if ($recentFailures.Count -gt 0) {
            Write-Host ''
            Write-Host 'Recent ETW failure hints found in logs:' -ForegroundColor Yellow
            $recentFailures | ForEach-Object {
                Write-Host ("[{0}] {1}" -f $_.File, $_.Line) -ForegroundColor Yellow
            }
        }
    }
}

Write-Section 'Health check'
try {
    $health = Invoke-JsonGet -Url "$resolvedBaseUrl/health"
    $report.Health = $health
    Write-Host ("Health OK: status={0}, utcNow={1}" -f $health.status, $health.utcNow) -ForegroundColor Green
}
catch {
    Write-Host ("Health failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    throw
}

Write-Section 'Baseline realtime sampling'
$baselineSamples = Sample-Realtime -ResolvedBaseUrl $resolvedBaseUrl -Count $SampleCount -DelaySeconds $IntervalSeconds
$report.BaselineRealtimeSamples = $baselineSamples
$baselineSamples | ForEach-Object {
    Write-Host ("[{0}] up={1}/s, down={2}/s, wanUp={3}/s, wanDown={4}/s, lanUp={5}/s, lanDown={6}/s" -f $_.Index, (Format-Bytes $_.TotalUploadBytesPerSecond), (Format-Bytes $_.TotalDownloadBytesPerSecond), (Format-Bytes $_.WanUploadBytesPerSecond), (Format-Bytes $_.WanDownloadBytesPerSecond), (Format-Bytes $_.LanUploadBytesPerSecond), (Format-Bytes $_.LanDownloadBytesPerSecond))
}

if ($PauseForManualTraffic) {
    Write-Section 'Manual traffic step'
    Read-Host 'Generate traffic now, then press Enter to continue'
}
elseif (-not [string]::IsNullOrWhiteSpace($TrafficUrl)) {
    Write-Section 'Automatic traffic generation'
    $trafficResults = Invoke-TrafficGeneration -Url $TrafficUrl -RequestCount $TrafficRequests
    $report.TrafficGeneration = $trafficResults
    $trafficResults | ForEach-Object {
        if ($_.Success) {
            Write-Host ("[{0}] {1} -> {2}, {3} ms" -f $_.Index, $_.Url, (Format-Bytes $_.Bytes), $_.DurationMs)
        }
        else {
            Write-Host ("[{0}] {1} -> failed: {2}" -f $_.Index, $_.Url, $_.Error) -ForegroundColor Yellow
        }
    }
}

Write-Section 'Active realtime sampling'
$activeSamples = Sample-Realtime -ResolvedBaseUrl $resolvedBaseUrl -Count $SampleCount -DelaySeconds $IntervalSeconds
$report.ActiveRealtimeSamples = $activeSamples
$activeSamples | ForEach-Object {
    Write-Host ("[{0}] up={1}/s, down={2}/s, wanUp={3}/s, wanDown={4}/s, lanUp={5}/s, lanDown={6}/s" -f $_.Index, (Format-Bytes $_.TotalUploadBytesPerSecond), (Format-Bytes $_.TotalDownloadBytesPerSecond), (Format-Bytes $_.WanUploadBytesPerSecond), (Format-Bytes $_.WanDownloadBytesPerSecond), (Format-Bytes $_.LanUploadBytesPerSecond), (Format-Bytes $_.LanDownloadBytesPerSecond))
}

Write-Section 'Historical aggregation check'
$from = (Get-Date).ToUniversalTime().AddMinutes(-$HistoryWindowMinutes).ToString('o')
$to = (Get-Date).ToUniversalTime().ToString('o')
$summaryUrl = ("{0}/api/network/apps?from={1}`&to={2}`&topN={3}" -f $resolvedBaseUrl, [Uri]::EscapeDataString($from), [Uri]::EscapeDataString($to), $TopN)

$rawAppSummaries = Invoke-JsonGet -Url $summaryUrl
if ($null -eq $rawAppSummaries) {
    $appSummaries = @()
}
elseif ($rawAppSummaries -is [System.Array]) {
    $appSummaries = @($rawAppSummaries)
}
else {
    $appSummaries = @($rawAppSummaries)
}

$report.AppSummaries = $appSummaries

if ($appSummaries.Count -eq 0) {
    Write-Host 'No app summaries were returned in the selected history window.' -ForegroundColor Yellow
}
else {
    $appSummaries | Select-Object -First $TopN | ForEach-Object {
        $displayName = if ([string]::IsNullOrWhiteSpace($_.displayName)) { 'n/a' } else { $_.displayName }
        $total = [double]$_.totalUploadBytes + [double]$_.totalDownloadBytes
        Write-Host ("{0} ({1}) -> total={2}, upload={3}, download={4}" -f $_.processName, $displayName, (Format-Bytes $total), (Format-Bytes ([double]$_.totalUploadBytes)), (Format-Bytes ([double]$_.totalDownloadBytes)))
    }
}

$allRealtimeSamples = @($baselineSamples + $activeSamples)
$hasRealtimeTraffic = $allRealtimeSamples | Where-Object {
    $_.TotalUploadBytesPerSecond -gt 0 -or $_.TotalDownloadBytesPerSecond -gt 0
} | Select-Object -First 1

$hasHistoricalTraffic = $appSummaries.Count -gt 0
$hasEtwFailureHints = @($report.RecentEtwFailures).Count -gt 0

$verdict = [ordered]@{
    HealthOk = $true
    RealtimeTrafficObserved = [bool]$hasRealtimeTraffic
    HistoricalTrafficObserved = $hasHistoricalTraffic
    RecentEtwFailureHints = $hasEtwFailureHints
    Conclusion = ''
}

if ($hasRealtimeTraffic -and $hasHistoricalTraffic) {
    $verdict.Conclusion = 'PASS: ETW collection, realtime aggregation, and historical aggregation all look healthy.'
}
elseif (-not $hasRealtimeTraffic -and $hasHistoricalTraffic) {
    $verdict.Conclusion = 'PARTIAL: historical aggregation has data, but this realtime sampling window did not capture active traffic. Generate traffic during sampling and run again.'
}
elseif ($hasEtwFailureHints) {
    $verdict.Conclusion = 'FAIL: ETW startup/runtime failure hints were found in logs. Check privileges, ETW resource usage, or session conflicts.'
}
else {
    $verdict.Conclusion = 'WARN: no clear traffic was observed in realtime or history. The machine may be idle, or ETW collection may not be working.'
}

$report.Verdict = $verdict

Write-Section 'Verdict'
if ($verdict.Conclusion.StartsWith('PASS')) {
    Write-Host $verdict.Conclusion -ForegroundColor Green
}
elseif ($verdict.Conclusion.StartsWith('FAIL')) {
    Write-Host $verdict.Conclusion -ForegroundColor Red
}
else {
    Write-Host $verdict.Conclusion -ForegroundColor Yellow
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $resolvedReportPath -Encoding UTF8
    Write-Host ''
    Write-Host "Detailed report saved to: $resolvedReportPath"
}
