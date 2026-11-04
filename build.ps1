<#
.SYNOPSIS
  Publica o BackupControlePro portátil em dist\ (EXE único + cliente PostgreSQL).
.NOTES
  - Não apaga backups nem arquivos .controlepro-* já existentes em dist\.
  - Requer .NET 10 SDK e uma cópia dos binários do PostgreSQL 17+ (pgenv ou -PgBin).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1
  powershell -ExecutionPolicy Bypass -File build.ps1 -PgBin "C:\tools\pgsql\bin"
  powershell -ExecutionPolicy Bypass -File build.ps1 -NoPg
#>
param(
  [string]$PgBin = (Join-Path $env:USERPROFILE 'tools\pgenv\Library\bin'),
  [switch]$NoPg
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$proj = Join-Path $repo 'BackupControlePro.csproj'
$dist = Join-Path $repo 'dist'
$staging = Join-Path $repo 'bin\portable-staging'

New-Item -ItemType Directory -Force -Path $dist, $staging | Out-Null

Write-Host "==> dotnet publish (win-x64, self-contained, single-file)"
dotnet publish $proj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true `
  -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (exit $LASTEXITCODE)" }

$exeName = 'BackupControlePro.exe'
$published = Join-Path $staging $exeName
if (-not (Test-Path $published)) { throw "EXE publicado não encontrado: $published" }

Write-Host "==> copiando $exeName para dist\ (preserva backups/senhas existentes)"
Copy-Item $published (Join-Path $dist $exeName) -Force

if (-not $NoPg) {
  Write-Host "==> copiando cliente PostgreSQL de: $PgBin"
  foreach ($tool in 'pg_dump.exe', 'pg_restore.exe', 'psql.exe') {
    if (-not (Test-Path (Join-Path $PgBin $tool))) {
      throw "ferramenta ausente em ${PgBin}: $tool"
    }
  }
  $pgDest = Join-Path $dist 'pg'
  New-Item -ItemType Directory -Force -Path $pgDest | Out-Null
  Copy-Item (Join-Path $PgBin '*') $pgDest -Recurse -Force
  foreach ($tool in 'pg_dump.exe', 'pg_restore.exe', 'psql.exe') {
    if (-not (Test-Path (Join-Path $pgDest $tool))) {
      throw "ferramenta não copiada para dist\pg: $tool"
    }
  }
}

Write-Host ""
Write-Host "OK: $dist"
Write-Host "  $exeName"
if (-not $NoPg) { Write-Host "  pg\ (pg_dump/pg_restore/psql)" }
Write-Host ""
Write-Host "Dica: rode .\$exeName para o primeiro uso (senha + token ficam em DPAPI)."
