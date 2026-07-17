param(
    [switch]$FailOnThreshold
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet is required but not found."
    exit 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$summaryPath = Join-Path $repoRoot "artifacts\codex\provider-mutation-summary.md"
New-Item -Path (Split-Path $summaryPath -Parent) -ItemType Directory -Force | Out-Null

try {
    dotnet tool restore | Out-Null
    $toolCheck = dotnet tool run dotnet-stryker -- --help 2>&1
    $toolCheckExitCode = $LASTEXITCODE
    $toolCheckText = $toolCheck -join [Environment]::NewLine
    if ($toolCheckExitCode -ne 0 -or $toolCheckText -match 'make the "dotnet-stryker" command available') {
        throw "dotnet-stryker is not restored."
    }
} catch {
    Write-Error "dotnet-stryker is not available. Install it with: dotnet tool install dotnet-stryker --local"
    exit 1
}

$configs = @(
    @{ Config = "scripts/quality/stryker/sqlserver.stryker-config.json"; WorkingDirectory = "tests/Incursa.Platform.SqlServer.Tests" },
    @{ Config = "scripts/quality/stryker/postgres.stryker-config.json"; WorkingDirectory = "tests/Incursa.Platform.Postgres.Tests" },
    @{ Config = "scripts/quality/stryker/inmemory.stryker-config.json"; WorkingDirectory = "tests/Incursa.Platform.InMemory.Tests" }
)

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("# Provider Mutation Summary")
$summary.Add("")
$summary.Add("Executed with dotnet-stryker using scoped provider configs.")
$summary.Add("")
$summary.Add("| Config | Result |")
$summary.Add("| --- | --- |")

$failures = 0
foreach ($entry in $configs) {
    $config = $entry.Config
    $configPath = Join-Path $repoRoot $config
    $workingDirectory = Join-Path $repoRoot $entry.WorkingDirectory
    Write-Host "Running mutation tests with $config from $($entry.WorkingDirectory)"
    try {
        Push-Location $workingDirectory
        try {
            $output = dotnet tool run dotnet-stryker -- --config-file $configPath 2>&1
            $runExitCode = $LASTEXITCODE
            $outputText = $output -join [Environment]::NewLine
            Write-Host $outputText
        } finally {
            Pop-Location
        }

        if ($runExitCode -ne 0 -or $outputText -match 'make the "dotnet-stryker" command available') {
            throw "dotnet-stryker failed for $config with exit code $runExitCode."
        }

        $summary.Add("| $config | Passed |")
    } catch {
        $failures++
        $summary.Add("| $config | Failed |")
    }
}

$summary | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host "Mutation summary written to $summaryPath"

if ($FailOnThreshold -and $failures -gt 0) {
    exit 1
}
