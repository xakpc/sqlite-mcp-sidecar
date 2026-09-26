<#
.SYNOPSIS
    Builds the sample database that the launch profiles and scripts/dev-sidecar.ps1 serve.

.DESCRIPTION
    Writes build/dev/app.db from test/Xakpc.SQLiteMCPSidecar.Tests/Fixtures/sample-db.sql and creates
    build/dev/backups. Run it one time before the first F5, and again after a change to that script.

    The seeding runs as one test, thus the manual path and the test suite share one implementation:
    no second project, no database file in the repository, and no sqlite3 prerequisite.

.EXAMPLE
    ./scripts/seed-dev-db.ps1
#>
[CmdletBinding()]
param(
    # Rebuilds the database when it exists already.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$devDirectory = Join-Path $repositoryRoot 'build/dev'
$databasePath = Join-Path $devDirectory 'app.db'

New-Item -ItemType Directory -Force -Path $devDirectory, (Join-Path $devDirectory 'backups') | Out-Null

if ((Test-Path $databasePath) -and -not $Force) {
    Write-Host "The sample database is present: $databasePath" -ForegroundColor DarkGray
    Write-Host 'Use -Force to rebuild it.' -ForegroundColor DarkGray
    return $databasePath
}

Write-Host "Seeding $databasePath ..." -ForegroundColor Cyan

$env:SIDECAR_DEV_DB = $databasePath
try {
    dotnet test --solution (Join-Path $repositoryRoot 'Xakpc.SQLiteMCPSidecar.slnx') `
        --filter-method '*DevDatabaseTests.Create'
    if ($LASTEXITCODE -ne 0) {
        throw "The seeding test failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:\SIDECAR_DEV_DB -ErrorAction SilentlyContinue
}

if (-not (Test-Path $databasePath)) {
    throw "The sample database was not created at $databasePath."
}

Write-Host "Ready: $databasePath" -ForegroundColor Green
return $databasePath
