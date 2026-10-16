<#
.SYNOPSIS
    Starts the ERP simulator and the Mini-MES API, each in its own window.

.DESCRIPTION
    1. checks that dotnet exists and that ports 5062 / 5063 are free
    2. builds the solution once
    3. opens one window per service (so each log stays readable)
    4. waits until both answer, then opens the browser
    5. waits for Enter and stops both services

.PARAMETER NoBrowser
    Do not open the browser.

.PARAMETER NoWait
    Return as soon as both services are running and leave their windows open.
    Stop them later with .\stop.ps1.

.EXAMPLE
    .\start.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [switch]$NoWait
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$apiUrl = 'http://localhost:5062'
$erpUrl = 'http://localhost:5063'

function Test-PortInUse([int]$port) {
    [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

function Wait-ForUrl([string]$url, [System.Diagnostics.Process]$process, [int]$seconds = 60) {
    for ($i = 0; $i -lt $seconds; $i++) {
        if ($process.HasExited) { return $false }
        try { Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true }
        catch { Start-Sleep -Seconds 1 }
    }
    return $false
}

function Start-ServiceWindow([string]$title, [string]$projectFolder, [string]$url) {
    # The command is passed encoded, which avoids every quoting problem with paths that contain spaces.
    $command = @"
`$Host.UI.RawUI.WindowTitle = '$title'
`$env:ASPNETCORE_ENVIRONMENT = 'Development'
Set-Location '$projectFolder'
dotnet run --no-build --no-launch-profile --urls $url
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process powershell -ArgumentList '-NoExit', '-NoProfile', '-EncodedCommand', $encoded -PassThru
}

function Stop-Tree([System.Diagnostics.Process]$process) {
    if ($process -and -not $process.HasExited) { & taskkill /PID $process.Id /T /F | Out-Null }
}

# --- 1. checks -------------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found. Install the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8) and open a new terminal."
}
foreach ($port in 5062, 5063) {
    if (Test-PortInUse $port) {
        throw "Port $port is already in use. Run .\stop.ps1 first, or close the window that is running Mini-MES."
    }
}

# --- 2. build ---------------------------------------------------------------------------------
Write-Host 'Building...' -ForegroundColor Cyan
& dotnet build "$root\MiniMes.sln" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Fix the errors above and try again.' }

# --- 3. start ---------------------------------------------------------------------------------
Write-Host 'Starting the ERP simulator and the Mini-MES API...' -ForegroundColor Cyan
$erp = Start-ServiceWindow 'Mini-MES ERP simulator' "$root\src\MiniMes.ErpSimulator" $erpUrl
$api = Start-ServiceWindow 'Mini-MES API'           "$root\src\MiniMes.Api"          $apiUrl

# --- 4. wait until both answer ----------------------------------------------------------------
$erpOk = Wait-ForUrl "$erpUrl/admin/mode" $erp
$apiOk = Wait-ForUrl "$apiUrl/api/config" $api
if (-not ($erpOk -and $apiOk)) {
    Stop-Tree $erp; Stop-Tree $api
    throw 'A service did not start. Run it by hand to see the error: cd src\MiniMes.Api ; dotnet run'
}

Write-Host ''
Write-Host "  Mini-MES         $apiUrl" -ForegroundColor Green
Write-Host "  ERP simulator    $erpUrl" -ForegroundColor Green
Write-Host ''
if (-not $NoBrowser) { Start-Process $apiUrl }

if ($NoWait) {
    Write-Host 'Both services are running in their own windows. Stop them with .\stop.ps1'
    return
}

# --- 5. wait, then stop ----------------------------------------------------------------------
try {
    Read-Host 'Press Enter to stop both services' | Out-Null
}
finally {
    Write-Host 'Stopping...' -ForegroundColor Cyan
    Stop-Tree $api
    Stop-Tree $erp
}
