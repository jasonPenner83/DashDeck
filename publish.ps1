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

# The catalog is copied fresh: a file that moved (LCARS to catalog\extras, ADR-0043) must not linger
# from an earlier deploy and load twice.
$oldCatalog = Join-Path $Output "catalog"
if (Test-Path $oldCatalog) { Remove-Item $oldCatalog -Recurse -Force }

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

# Components (ADR-0023) ship beside the executable for the same reason the catalog does:
# PluginPath.FindRoot walks up for a plugins\ folder, and a deploy folder has nothing above it.
# Each component is its own project outside the Host build, so it is built here first — which
# deploys its DLL and manifest into the repo's plugins\ via the component's own DeployToPlugin
# target — and then the whole plugins\ tree is copied into the deploy.
$componentProjects = @(Get-ChildItem -Path (Join-Path $PSScriptRoot 'components') -Filter *.csproj -Recurse -ErrorAction SilentlyContinue)

foreach ($proj in $componentProjects) {
    Write-Output "Building component $($proj.BaseName) ..."
    dotnet build $proj.FullName --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "component build failed: $($proj.Name)" }
}

$pluginsSource = Join-Path $PSScriptRoot 'plugins'
$pluginsDest = Join-Path $Output 'plugins'
$componentCount = 0

if (Test-Path $pluginsSource) {
    # Only real component folders travel — each must carry a manifest, or it is not a component
    # and the host would only reject it. The .gitkeep at the plugins\ root is left behind.
    foreach ($dir in Get-ChildItem $pluginsSource -Directory) {
        if (Test-Path (Join-Path $dir.FullName 'component.json')) {
            New-Item -ItemType Directory -Force -Path $pluginsDest | Out-Null
            Copy-Item $dir.FullName -Destination $pluginsDest -Recurse -Force
            $componentCount++
        }
    }
}

$bytes = (Get-ChildItem $Output -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Output ""
Write-Output ("  {0,-22} {1}" -f 'executable', (Resolve-Path $exe))
Write-Output ("  {0,-22} {1} MB in {2} files" -f 'size', [math]::Round($bytes / 1MB), (Get-ChildItem $Output -Recurse -File).Count)
Write-Output ("  {0,-22} {1}" -f 'catalog', 'included')
Write-Output ("  {0,-22} {1}" -f 'libvlc', $(if (Test-Path (Join-Path $Output 'libvlc')) { 'included' } else { 'MISSING' }))
Write-Output ("  {0,-22} {1}" -f 'components', $(if ($componentCount -gt 0) { "$componentCount included" } else { 'none' }))

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
