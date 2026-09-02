<#
    Builds the DashDeck mockup pages.

    Each *.src.html is a page shell containing placeholders of the form
    <!--SCREEN:Name-->. For every placeholder, the artboard Name.dc.html is read,
    the fragment between </helmet> and </x-dc> is extracted, the {{accent}} hole is
    resolved to a literal, and the result is inlined. Output is <name>.html.

    Usage:  pwsh -File build.ps1
#>

[CmdletBinding()]
param(
    [string] $Accent = '#FF7A1A'
)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

$sources = Get-ChildItem -Path $dir -Filter '*.src.html'
if (-not $sources) { throw 'No *.src.html shells found.' }

foreach ($src in $sources) {
    $page = Get-Content $src.FullName -Raw -Encoding UTF8
    $names = [regex]::Matches($page, '<!--SCREEN:(\w+)-->') |
             ForEach-Object { $_.Groups[1].Value } |
             Select-Object -Unique

    foreach ($name in $names) {
        $artboard = Join-Path $dir "$name.dc.html"
        if (-not (Test-Path $artboard)) { throw "$($src.Name): missing artboard $name.dc.html" }

        $raw = Get-Content $artboard -Raw -Encoding UTF8
        $match = [regex]::Match($raw, '(?s)</helmet>(.*?)</x-dc>')
        if (-not $match.Success) { throw "$name.dc.html: no fragment between </helmet> and </x-dc>" }

        $fragment = $match.Groups[1].Value.Replace('{{accent}}', $Accent)
        if ($fragment -match '\{\{') { throw "$name.dc.html: unresolved template hole" }

        $page = $page.Replace("<!--SCREEN:$name-->", $fragment)
    }

    if ($page -match '<!--SCREEN:') { throw "$($src.Name): a placeholder was left unfilled" }

    $out = Join-Path $dir ($src.Name -replace '\.src\.html$', '.html')
    Set-Content $out -Value $page -Encoding utf8 -NoNewline

    $kb = [math]::Round((Get-Item $out).Length / 1KB)
    Write-Output ("{0} -> {1} ({2} screens, {3} KB)" -f $src.Name, (Split-Path $out -Leaf), $names.Count, $kb)
}
