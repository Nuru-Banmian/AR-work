param(
    [ValidateRange(1, 65535)][int]$ApiPort = 18088,
    [ValidateRange(1, 65535)][int]$WebPort = 5176,
    [string]$DataFile = 'backend/data/issues.json',
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$backend = Join-Path $root 'backend'
$web = Join-Path $root 'web'
$runtime = Join-Path $root ('.local/start/' + [guid]::NewGuid().ToString('N'))
$apiProcess = $null
$webProcess = $null
$previousEnvironment = @{}
foreach ($key in 'ARWORK_ADDR', 'ARWORK_DATA', 'ARWORK_ORIGINS', 'ARWORK_READY', 'ARWORK_API_URL') {
    $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

function Wait-Service([string]$Url, [System.Diagnostics.Process]$Process) {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { throw "Service exited. See logs in $runtime" }
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Milliseconds 300 }
    }
    throw "Service did not become ready: $Url. See logs in $runtime"
}

try {
    if ($ApiPort -eq $WebPort) { throw 'API and web ports must be different.' }
    $goCommand = Get-Command go.exe -ErrorAction SilentlyContinue
    $go = if ($goCommand) { $goCommand.Source } else { Join-Path $root '.local/go/bin/go.exe' }
    if (-not (Test-Path -LiteralPath $go)) { throw 'Go not found. Install Go and add it to PATH.' }
    $node = (Get-Command node.exe -ErrorAction Stop).Source
    $npm = (Get-Command npm.cmd -ErrorAction Stop).Source
    if (-not [System.IO.Path]::IsPathRooted($DataFile)) { $DataFile = Join-Path $root $DataFile }
    $DataFile = [System.IO.Path]::GetFullPath($DataFile)
    if (Test-Path -LiteralPath $DataFile -PathType Container) { throw 'DataFile must be a file path.' }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null

    # Only install on first use; never remove dependencies used by another Vite process.
    $vite = Join-Path $web 'node_modules/vite/bin/vite.js'
    if (-not (Test-Path -LiteralPath $vite)) {
        Push-Location $web
        try {
            & $npm ci
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        }
        finally { Pop-Location }
    }

    $exe = Join-Path $runtime 'backend.exe'
    Push-Location $backend
    try {
        & $go build -o $exe .
        if ($LASTEXITCODE -ne 0) { throw 'Go build failed.' }
    }
    finally { Pop-Location }

    $env:ARWORK_ADDR = ":$ApiPort"
    $env:ARWORK_DATA = $DataFile
    $env:ARWORK_ORIGINS = ((0..9 | ForEach-Object { "http://127.0.0.1:$($WebPort+$_),http://localhost:$($WebPort+$_)" }) -join ',')
    $env:ARWORK_READY = Join-Path $runtime ("ready-" + [guid]::NewGuid().ToString('N') + '.json')
    $apiProcess = Start-Process -FilePath $exe -WorkingDirectory $backend -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runtime 'api.stdout.log') -RedirectStandardError (Join-Path $runtime 'api.stderr.log')
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath $env:ARWORK_READY)) {
        if ($apiProcess.HasExited -or (Get-Date) -gt $deadline) { throw "API could not bind or start. See $runtime/api.stderr.log" }
        Start-Sleep -Milliseconds 200
    }
    $receiver = Get-Content -Raw -LiteralPath $env:ARWORK_READY | ConvertFrom-Json
    $ApiPort = [int]$receiver.port
    $env:ARWORK_API_URL = "http://127.0.0.1:$ApiPort"
    Wait-Service "$env:ARWORK_API_URL/health" $apiProcess
    # Vite owns the successful socket. strictPort rejects occupied candidates; never reuse another service.
    $webStarted = $false
    foreach ($candidate in $WebPort..([Math]::Min(65535, $WebPort + 9))) {
        if ($candidate -eq $ApiPort) { continue }
        $webProcess = Start-Process -FilePath $node -ArgumentList @('node_modules/vite/bin/vite.js', '--host', '127.0.0.1', '--port', "$candidate", '--strictPort') `
            -WorkingDirectory $web -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $runtime 'web.stdout.log') -RedirectStandardError (Join-Path $runtime 'web.stderr.log')
        # A unique launch is ready only when its own Vite output announces the listening URL.
        $deadline = (Get-Date).AddSeconds(30)
        while (-not $webProcess.HasExited -and (Get-Date) -lt $deadline) {
            $output = Get-Content -Raw (Join-Path $runtime 'web.stdout.log') -ErrorAction SilentlyContinue
            if ($output -match 'Local:') { $webStarted = $true; break }
            Start-Sleep -Milliseconds 200
        }
        if ($webStarted) { $WebPort = $candidate; break }
        if (-not $webProcess.HasExited) { Stop-Process -Id $webProcess.Id; throw 'Web startup timed out.' }
        $reason = Get-Content -Raw (Join-Path $runtime 'web.stderr.log')
        if ($reason -notmatch 'already in use') { throw "Web startup failed: $reason" }
    }
    if (-not $webStarted) { throw 'No available web port in candidate range.' }
    $webUrl = "http://127.0.0.1:$WebPort"
    Wait-Service $webUrl $webProcess

    Write-Host "Web: $webUrl"
    Write-Host "Receiver: $($receiver.name) [$($receiver.id)] API: $env:ARWORK_API_URL (web connects automatically)"
    Write-Host 'Phone API address candidates (same LAN; choose the reachable PC adapter):'
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
        ForEach-Object { Write-Host "  http://$($_.IPAddress):$ApiPort [$($_.InterfaceAlias)]" }
    Write-Host "Data: $DataFile"
    Write-Host "Logs: $runtime"
    Write-Host 'Phone: Settings > enter one of the LAN API addresses above > Connect and save. Allow the actual API TCP port in the PC firewall.'
    Write-Host 'Keep this terminal open. Press Ctrl+C to stop both services.'
    if (-not $NoBrowser) { Start-Process $webUrl }
    while ($true) {
        if ($apiProcess.HasExited -or $webProcess.HasExited) { throw "A service stopped. See logs in $runtime" }
        Start-Sleep -Seconds 1
    }
}
finally {
    foreach ($process in @($webProcess, $apiProcess)) {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
    }
    if ($env:ARWORK_READY -and (Test-Path -LiteralPath $env:ARWORK_READY)) { Remove-Item -LiteralPath $env:ARWORK_READY }
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
}
