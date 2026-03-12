param(
    [string]$Solution = "Incursa.Integrations.Public.CI.slnx",
    [string]$Configuration = "Release",
    [string]$Runsettings = "runsettings/smoke.runsettings",
    [string]$ResultsDirectory = "artifacts/codex/test-results/smoke",
    [switch]$NoRestore,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "QualityLane.Common.ps1")

Assert-DotNetAvailable

function Test-IsTestProject {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    [xml]$project = Get-Content -LiteralPath $ProjectPath

    foreach ($propertyGroup in @($project.Project.PropertyGroup)) {
        $isTestProjectProperty = $propertyGroup.PSObject.Properties["IsTestProject"]
        if ($null -ne $isTestProjectProperty -and [string]$isTestProjectProperty.Value -match '^(?i:true)$') {
            return $true
        }
    }

    $sdk = [string]$project.Project.Sdk
    if ($sdk -match '(^|;)MSTest\.Sdk($|;)') {
        return $true
    }

    $testPackageIds = @(
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.v3",
        "xunit.runner.visualstudio",
        "NUnit",
        "NUnit3TestAdapter",
        "MSTest.TestAdapter",
        "MSTest.TestFramework"
    )

    foreach ($itemGroup in @($project.Project.ItemGroup)) {
        $packageReferenceProperty = $itemGroup.PSObject.Properties["PackageReference"]
        if ($null -eq $packageReferenceProperty) {
            continue
        }

        foreach ($packageReference in @($packageReferenceProperty.Value)) {
            $packageId = [string]$packageReference.Include
            if ($testPackageIds -contains $packageId) {
                return $true
            }
        }
    }

    return $false
}

function Get-SmokeLaneProjects {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,

        [Parameter(Mandatory)]
        [string]$SolutionPath
    )

    [xml]$solution = Get-Content -LiteralPath $SolutionPath
    $projectPaths = New-Object System.Collections.Generic.List[string]

    foreach ($projectNode in @($solution.SelectNodes('//Project[@Path]'))) {
        $relativeProjectPath = [string]$projectNode.Path
        if (-not $relativeProjectPath.StartsWith('tests/', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $projectPath = Resolve-RepoPath -RepoRoot $RepoRoot -Path $relativeProjectPath
        if (Test-IsTestProject -ProjectPath $projectPath) {
            $projectPaths.Add($projectPath)
        }
    }

    return $projectPaths.ToArray()
}

$repoRoot = Get-QualityRepoRoot
$solutionPath = Resolve-RepoPath -RepoRoot $repoRoot -Path $Solution
$runsettingsPath = Resolve-RepoPath -RepoRoot $repoRoot -Path $Runsettings
$resultsPath = Resolve-RepoPath -RepoRoot $repoRoot -Path $ResultsDirectory
$summaryPath = Join-Path $resultsPath "summary.md"
$projects = Get-SmokeLaneProjects -RepoRoot $repoRoot -SolutionPath $solutionPath

Write-Host "Running smoke lane..." -ForegroundColor Cyan
Write-Host "Solution: $solutionPath" -ForegroundColor Yellow
Write-Host "Runsettings: $runsettingsPath" -ForegroundColor Yellow
Write-Host "Results: $resultsPath" -ForegroundColor Yellow

Initialize-ArtifactDirectory -Path $resultsPath -Clean | Out-Null
Invoke-TestPrerequisites -Solution $solutionPath -Configuration $Configuration -NoRestore:$NoRestore -NoBuild:$NoBuild

foreach ($projectPath in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    $testArgs = @(
        "test"
        $projectPath
        "--configuration"
        $Configuration
        "--settings"
        $runsettingsPath
        "--results-directory"
        $resultsPath
        "--logger"
        "trx;LogFileName=$projectName.trx"
        "--no-build"
        "--no-restore"
    )

    Write-Host "Testing $projectName..." -ForegroundColor Cyan
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke lane failed for $projectName with exit code $LASTEXITCODE."
    }
}

$summary = Write-TrxSummaryMarkdown -Title "Smoke Lane Summary" -ResultsDirectory $resultsPath -SummaryPath $summaryPath -RepoRoot $repoRoot -EmptyMessage "The smoke lane did not produce any TRX files."
Append-GitHubStepSummary -SummaryPath $summary.SummaryPath

if (-not $summary.HasResults) {
    throw "Smoke lane completed without producing TRX results."
}

Write-Host "Smoke lane completed successfully." -ForegroundColor Green
