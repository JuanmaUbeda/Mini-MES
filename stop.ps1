<#
.SYNOPSIS
    Stops everything that start.ps1 (or a manual dotnet run) started: the API, the ERP simulator
    and the two windows that start.ps1 opened for them.
#>
$stopped = 0

# The services themselves, found by the ports they listen on (one process can listen on IPv4 and IPv6).
$processIds = foreach ($port in 5062, 5063) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess
}
foreach ($processId in $processIds | Select-Object -Unique) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -like 'MiniMes*') {
        Write-Host "Stopping $($process.ProcessName)"
        Stop-Process -Id $process.Id -Force
        $stopped++
    }
}

# The windows start.ps1 opened for them. Only these exact titles, so no other terminal can be closed by mistake.
$titles = 'Mini-MES API', 'Mini-MES ERP simulator'
Get-Process powershell -ErrorAction SilentlyContinue |
    Where-Object { $titles -contains $_.MainWindowTitle } |
    ForEach-Object { Stop-Process -Id $_.Id -Force }

if ($stopped -eq 0) { Write-Host 'Nothing was running.' } else { Write-Host 'Done.' }
