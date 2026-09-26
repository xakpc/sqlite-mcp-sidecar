<#
.SYNOPSIS
    Runs the sidecar over a seeded sample database, for a manual session.

.DESCRIPTION
    Seeds build/dev/app.db from the sample script of the test project, then starts the sidecar with
    a development token. The endpoint that it prints is the one that src/Xakpc.SQLiteMCPSidecar/mcp.http
    is already set up for.

    The same permission sets that the test suite uses are available here, thus a manual session and
    an automated run see the same deployment shapes.

.EXAMPLE
    ./scripts/dev-sidecar.ps1
    Starts a read-only deployment (schema,read) on port 8080.

.EXAMPLE
    ./scripts/dev-sidecar.ps1 -Permissions write -Fresh
    Shows the read-floor startup failure with a new database.

.EXAMPLE
    ./scripts/dev-sidecar.ps1
    Then, in a second shell, runs the whole e2e suite against this process:
        $env:SIDECAR_E2E_URL = 'http://localhost:8080'
        $env:SIDECAR_E2E_TOKEN = 'dev-token'
        $env:SIDECAR_E2E_PERMISSIONS = 'schema,read'
        dotnet test --solution Xakpc.SQLiteMCPSidecar.slnx
#>
[CmdletBinding()]
param(
    # The deployment permission set, exactly as SQLITE_SIDECAR_PERMISSIONS takes it.
    [string] $Permissions = 'schema,read',

    [int] $Port = 8080,

    [string] $Token = 'dev-token',

    # Rebuilds the sample database. Use it after a change to Fixtures/sample-db.sql.
    [switch] $Fresh
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$devDirectory = Join-Path $repositoryRoot 'build/dev'
$databasePath = Join-Path $devDirectory 'app.db'
$backupDirectory = Join-Path $devDirectory 'backups'

New-Item -ItemType Directory -Force -Path $devDirectory, $backupDirectory | Out-Null

if ($Fresh -or -not (Test-Path $databasePath)) {
    Write-Host "Seeding $databasePath from the sample script..." -ForegroundColor Cyan

    # The seeding lives in the test project, thus the dev script and the test suite share one
    # implementation: no second project, no checked-in binary, no sqlite3 prerequisite.
    $env:SIDECAR_DEV_DB = $databasePath
    dotnet test --solution (Join-Path $repositoryRoot 'Xakpc.SQLiteMCPSidecar.slnx') `
        --filter-method '*DevDatabaseTests.Create'
    Remove-Item Env:\SIDECAR_DEV_DB

    if (-not (Test-Path $databasePath)) {
        throw "The sample database was not created at $databasePath."
    }
}

$env:SQLITE_SIDECAR_DB = $databasePath
$env:SQLITE_SIDECAR_TOKEN = $Token
$env:SQLITE_SIDECAR_PERMISSIONS = $Permissions
$env:SQLITE_SIDECAR_BACKUP_DIR = $backupDirectory
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$env:ASPNETCORE_ENVIRONMENT = 'Development'

Write-Host ''
Write-Host "  MCP endpoint  http://localhost:$Port/mcp" -ForegroundColor Green
Write-Host "  health        http://localhost:$Port/health" -ForegroundColor Green
Write-Host "  token         $Token" -ForegroundColor Green
Write-Host "  permissions   $Permissions" -ForegroundColor Green
Write-Host "  database      $databasePath" -ForegroundColor Green
Write-Host ''
Write-Host '  Call the tools by hand from src/Xakpc.SQLiteMCPSidecar/mcp.http.' -ForegroundColor DarkGray
Write-Host ''

dotnet run --project (Join-Path $repositoryRoot 'src/Xakpc.SQLiteMCPSidecar') --no-launch-profile
