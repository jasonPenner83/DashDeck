<#
    Builds the tablet deploy.

    Produces a self-contained folder under dist/ that runs on the Surface with nothing
    installed — no .NET runtime, no VLC, no registry, no services. That is constraint C1
    taken literally: uninstalling DashDeck is deleting the folder.

    Usage:
        pwsh -File publish.ps1              # build it
        pwsh -File publish.ps1 -Shortcut    # and drop a shortcut in the folder
#>

[CmdletBinding()]
param(
    [string] $Output = "dist\DashDeck",
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $Shortcut
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Output "Publishing $Runtime self-contained to $Output ..."

dotnet publish src\DashDeck.Host\DashDeck.Host.csproj `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $Output `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $Output "DashDeck.Host.exe"
if (-not (Test-Path $exe)) { throw "no executable at $exe" }

# The catalog is data (ADR-0004) and has to travel with the binary; a published folder has
# nothing above it for FindCatalog to walk up to.
$catalog = Join-Path $Output "catalog\signals.obd2-standard.json"
if (-not (Test-Path $catalog)) { throw "the signal catalog did not make it into the deploy" }

$bytes = (Get-ChildItem $Output -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Output ""
Write-Output ("  {0,-22} {1}" -f 'executable', (Resolve-Path $exe))
Write-Output ("  {0,-22} {1} MB in {2} files" -f 'size', [math]::Round($bytes / 1MB), (Get-ChildItem $Output -Recurse -File).Count)
Write-Output ("  {0,-22} {1}" -f 'catalog', 'included')
Write-Output ("  {0,-22} {1}" -f 'libvlc', $(if (Test-Path (Join-Path $Output 'libvlc')) { 'included' } else { 'MISSING' }))

if ($Shortcut) {
    $link = Join-Path (Resolve-Path $Output) "DashDeck.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($link)
    $s.TargetPath = (Resolve-Path $exe).Path
    $s.WorkingDirectory = (Resolve-Path $Output).Path
    $s.Description = "DashDeck"
    $s.Save()
    Write-Output ("  {0,-22} {1}" -f 'shortcut', $link)
}

Write-Output ""
Write-Output "Run it by launching the executable. Escape closes."
