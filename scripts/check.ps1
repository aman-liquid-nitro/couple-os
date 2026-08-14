# Everything CI runs, locally, in the order CI runs it. The PowerShell half of
# scripts/check.sh — see that file for why both exist and what --fast means.
#
#   .\scripts\check.ps1
#   .\scripts\check.ps1 -Fast
#   $env:EVAL_SAMPLES = '1'; .\scripts\check.ps1
#
# Requires the stack:  docker compose up -d

[CmdletBinding()]
param(
    [switch] $Fast
)

$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')

# The model credential and host live in .env, like everything else the compose
# stack reads. Loaded rather than duplicated here.
if (Test-Path .env) {
    foreach ($line in Get-Content .env) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2].Trim('"'))
        }
    }
}

function Step([string] $Name) {
    Write-Host ''
    Write-Host "== $Name" -ForegroundColor Cyan
}

function Invoke-Checked([scriptblock] $Command, [string] $What) {
    & $Command

    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

Step 'Clearing the run record'
if (Test-Path artifacts/eval) { Remove-Item artifacts/eval -Recurse -Force }

Step 'Build'
Invoke-Checked { dotnet build CoupleOS.slnx } 'Build'

Step 'Unit tests, and the pipeline evals'
Invoke-Checked { dotnet test tests/CoupleOS.UnitTests } 'Unit tests'

Step 'Integration tests, and the database evals'
Invoke-Checked { dotnet test tests/CoupleOS.IntegrationTests } 'Integration tests'

Step 'Row-level security assertions, in SQL'
$dbUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { 'postgres' }
$dbName = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { 'coupleos' }
Invoke-Checked {
    Get-Content data/rls-tests.sql -Raw |
        docker compose exec -T db psql -U $dbUser -d $dbName -v ON_ERROR_STOP=1
} 'RLS assertions'

if ($Fast) {
    Step 'Skipping the extraction evals (-Fast)'
    Write-Host 'The gate below will report the 51 cases they cover as never run, and fail.'
}
else {
    Step 'Extraction evals'
    Invoke-Checked { dotnet test tests/CoupleOS.AITests } 'Extraction evals'
}

Step 'Eval gate'
dotnet run --project tools/CoupleOS.EvalGate

exit $LASTEXITCODE
