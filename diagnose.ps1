<#
    What DashDeck was doing when it would not start.

    Collects the four things that answer it — the startup trail, the crash log, whether a stuck
    DashDeck is holding the single-instance lock, and which COM ports exist — and prints them in
    one block to paste back. Reads only; changes nothing, starts nothing, kills nothing.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools-diagnose.ps1
#>

$ErrorActionPreference = 'Continue'
$local = Join-Path $env:LOCALAPPDATA 'DashDeck'

Write-Output "=== DashDeck processes (a row here with no window on screen is the usual culprit) ==="
$running = Get-Process DashDeck.Host -ErrorAction SilentlyContinue
if ($running) {
    $running | Select-Object Id, StartTime, CPU, Responding, MainWindowHandle | Format-Table -AutoSize | Out-String | Write-Output
} else {
    Write-Output "none running"
}

Write-Output "=== startup trail (the phase it never finished is the last line) ==="
$startup = Join-Path $local 'startup.log'
if (Test-Path $startup) { Get-Content $startup | Write-Output } else { Write-Output "no startup.log — this build predates it" }

Write-Output ""
Write-Output "=== crash log, last 20 lines ==="
$crash = Join-Path $local 'crash.log'
if (Test-Path $crash) { Get-Content $crash -Tail 20 | Write-Output } else { Write-Output "no crash.log" }

Write-Output ""
Write-Output "=== settings that decide what startup touches ==="
$settings = Join-Path $local 'settings.json'
if (Test-Path $settings) {
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    [pscustomobject]@{
        adapterSerialPort = $json.adapterSerialPort
        adapterBaudRate   = $json.adapterBaudRate
        gpsEnabled        = $json.gpsEnabled
        gpsTransport      = $json.gpsTransport
        gpsSerialPort     = $json.gpsSerialPort
    } | Format-List | Out-String | Write-Output
} else {
    Write-Output "no settings.json"
}

Write-Output "=== COM ports Windows has ==="
[System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object | Write-Output

Write-Output ""
Write-Output "=== build ==="
Write-Output ("commit: " + (git log --oneline -1 2>$null))
Write-Output ("deploy: " + (Test-Path (Join-Path $PSScriptRoot 'dist\DashDeck\DashDeck.Host.exe')))
