[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$CatalogPath = (Join-Path $PSScriptRoot "package-catalog.json"),
    [string]$Base = "",
    [string]$Head = "",
    [string[]]$ChangedFiles = @()
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $CatalogPath)) {
    throw "Package catalog not found at '$CatalogPath'. Run eng/Generate-PackageCatalog.ps1 first."
}

$resolveScript = Join-Path $PSScriptRoot "Resolve-AffectedProjects.ps1"
$resolveArguments = @{
    RepoRoot = $RepoRoot
    CatalogPath = $CatalogPath
    Base = $Base
    Head = $Head
    AsJson = $true
}
if ($ChangedFiles.Count -gt 0) {
    $resolveArguments.ChangedFiles = $ChangedFiles
}

$affected = & $resolveScript @resolveArguments | ConvertFrom-Json
$affectedPaths = @($affected.projectPaths)
$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json

@($catalog.projects |
    Where-Object { $_.publishable -and $affectedPaths -contains $_.projectPath } |
    ForEach-Object { [string]$_.packageId } |
    Sort-Object -Unique)
