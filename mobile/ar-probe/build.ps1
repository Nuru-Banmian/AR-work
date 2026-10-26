param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditor
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $UnityEditor)) { throw 'Unity Editor executable not found.' }
$probeRoot = $PSScriptRoot
$logDirectory = Join-Path $probeRoot 'Logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$buildLog = Join-Path $logDirectory 'build.log'
$arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $probeRoot + '"'),
    '-buildTarget', 'Android', '-executeMethod', 'ProbeBuild.Android', '-logFile', ('"' + $buildLog + '"'))
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
# Wait for the editor itself; Gradle/UPM child daemons can outlive a completed build.
$process.WaitForExit()
$process.Refresh()
if ($process.ExitCode -ne 0) { throw "Unity build failed; see $buildLog" }
$apk = Join-Path $probeRoot 'Builds\AR-Device-Probe.apk'
if (-not (Test-Path $apk) -or -not (Select-String -LiteralPath $buildLog -SimpleMatch 'AR_PROBE_BUILD_RESULT=Succeeded' -Quiet)) {
    throw "No successful build result; see $buildLog"
}
$artifact = Get-Item -LiteralPath $apk
[pscustomobject]@{
    Apk = $artifact.FullName
    Bytes = $artifact.Length
    SHA256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash
} | ConvertTo-Json
