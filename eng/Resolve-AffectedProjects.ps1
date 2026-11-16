[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$CatalogPath = (Join-Path $PSScriptRoot "package-catalog.json"),
    [string]$Base = "",
    [string]$Head = "",
    [string[]]$ChangedFiles = @(),
    [switch]$AsJson
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

function Normalize-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalized = $Path -replace "\\", "/"
    if ($normalized.StartsWith("./", [System.StringComparison]::Ordinal) -or $normalized.StartsWith(".\", [System.StringComparison]::Ordinal)) {
        return $normalized.Substring(2)
    }

    return $normalized
}

function Get-ChangedFilesFromGit {
    if ($ChangedFiles.Count -gt 0) {
        return $ChangedFiles | ForEach-Object { Normalize-RelativePath $_ } | Sort-Object -Unique
    }

    $gitArgs = @("diff", "--name-only", "--diff-filter=ACMRTUXB")
    if (-not [string]::IsNullOrWhiteSpace($Base)) {
        $gitArgs += $Base
    }
    if (-not [string]::IsNullOrWhiteSpace($Head)) {
        $gitArgs += $Head
    }

    $diffOutput = & git @gitArgs
    if ($LASTEXITCODE -ne 0) {
        throw "git diff failed with exit code $LASTEXITCODE."
    }

    return @($diffOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Normalize-RelativePath $_ } | Sort-Object -Unique)
}

function Get-ProjectReferences {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $RepoRoot $ProjectPath) -Raw
    $projectDirectory = Split-Path -Parent (Join-Path $RepoRoot $ProjectPath)

    $references = New-Object System.Collections.Generic.List[string]
    $projectReferenceNodes = $projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='ProjectReference']")
    foreach ($projectReference in $projectReferenceNodes) {
        $include = $projectReference.GetAttribute("Include")
        if ([string]::IsNullOrWhiteSpace($include)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $include))
        if (-not (Test-Path -LiteralPath $fullPath)) {
            continue
        }

        $relativePath = Normalize-RelativePath ([System.IO.Path]::GetRelativePath($RepoRoot, $fullPath))
        [void]$references.Add($relativePath)
    }

    return $references
}

function Get-ExternalInputDirectories {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $RepoRoot $ProjectPath) -Raw
    $projectDirectory = Split-Path -Parent (Join-Path $RepoRoot $ProjectPath)
    $projectRelativeDirectory = Normalize-RelativePath (Split-Path -Parent $ProjectPath)
    $directories = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    $inputNodes = $projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='Compile' or local-name()='Content' or local-name()='EmbeddedResource' or local-name()='None']")
    foreach ($inputNode in $inputNodes) {
        $include = $inputNode.GetAttribute("Include")
        if ([string]::IsNullOrWhiteSpace($include) -or -not $include.Contains("..", [System.StringComparison]::Ordinal)) {
            continue
        }

        $wildcardIndex = $include.IndexOfAny([char[]]@('*', '?'))
        $inputRoot = if ($wildcardIndex -ge 0) { $include.Substring(0, $wildcardIndex) } else { $include }
        $inputRoot = $inputRoot.TrimEnd([char[]]@('/', '\'))
        if ([string]::IsNullOrWhiteSpace($inputRoot)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $inputRoot))
        if ([System.IO.Path]::HasExtension($fullPath) -and $wildcardIndex -lt 0) {
            $fullPath = Split-Path -Parent $fullPath
        }

        $relativePath = Normalize-RelativePath ([System.IO.Path]::GetRelativePath($RepoRoot, $fullPath))
        if (-not $relativePath.StartsWith("..", [System.StringComparison]::Ordinal) -and $relativePath -ne $projectRelativeDirectory) {
            [void]$directories.Add($relativePath)
        }
    }

    return $directories
}

if (-not (Test-Path -LiteralPath $CatalogPath)) {
    throw "Package catalog not found at '$CatalogPath'. Run eng/Generate-PackageCatalog.ps1 first."
}

$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$projects = @($catalog.projects)
$projectPaths = $projects.projectPath

$referencesByProject = @{}
$dependentsByProject = @{}
$externalInputDirectoriesByProject = @{}
foreach ($projectPath in $projectPaths) {
    $references = @(Get-ProjectReferences -ProjectPath $projectPath | Where-Object { $projectPaths -contains $_ })
    $referencesByProject[$projectPath] = $references
    $externalInputDirectoriesByProject[$projectPath] = @(Get-ExternalInputDirectories -ProjectPath $projectPath)

    foreach ($reference in $references) {
        if (-not $dependentsByProject.ContainsKey($reference)) {
            $dependentsByProject[$reference] = New-Object System.Collections.Generic.List[string]
        }

        [void]$dependentsByProject[$reference].Add($projectPath)
    }
}

$changed = @(Get-ChangedFilesFromGit)

$globalImpactPatterns = @(
    "^Directory\.Build\.(props|targets)$",
    "^global\.json$",
    "^stylecop\.json$",
    "^\.editorconfig$",
    "^Incursa\.Platform(\.CI)?\.slnx$",
    "^assets/nuget_logo\.png$"
)

$affected = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

if ($changed | Where-Object {
        $path = $_
        $globalImpactPatterns | Where-Object { $path -match $_ }
    }) {
    foreach ($projectPath in $projectPaths) {
        [void]$affected.Add($projectPath)
    }
}
else {
    foreach ($changedPath in $changed) {
        foreach ($project in $projects) {
            $projectPath = [string]$project.projectPath
            $projectDirectory = Normalize-RelativePath (Split-Path -Parent $projectPath)
            $matchesExternalInput = @($externalInputDirectoriesByProject[$projectPath] | Where-Object {
                    $changedPath -eq $_ -or $changedPath -like "$_/*"
                }).Count -gt 0
            if ($changedPath -eq $projectPath -or $changedPath -like "$projectDirectory/*" -or $matchesExternalInput) {
                [void]$affected.Add($projectPath)
            }
        }
    }

    $pending = New-Object System.Collections.Generic.Queue[string]
    foreach ($projectPath in $affected) {
        $pending.Enqueue($projectPath)
    }

    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if (-not $dependentsByProject.ContainsKey($current)) {
            continue
        }

        foreach ($dependent in @($dependentsByProject[$current])) {
            if ([string]::IsNullOrWhiteSpace([string]$dependent)) {
                continue
            }

            if ($affected.Add($dependent)) {
                $pending.Enqueue($dependent)
            }
        }
    }
}

$result = [ordered]@{
    changedFiles = $changed
    projectPaths = @($affected | Sort-Object)
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 4
}
else {
    $result.projectPaths
}
