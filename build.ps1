<#
.SYNOPSIS
    Builds every package into artifacts/packages.

.DESCRIPTION
    EfMigrationPack           runtime, one package per EF Core major (6, 7, 8, 9, 10)
    EfMigrationPack.Cli       command-line tool ("dotnet ef-pack")
    EfMigrationPack.Tools     Package Manager Console commands

.PARAMETER PackRelease
    Minor.patch for this release, e.g. 1.0 gives EfMigrationPack 8.1.0 and EfMigrationPack.Cli 1.1.0.
    Defaults to the value in Directory.Build.props.

.PARAMETER Release
    Build release versions. Without it, versions end in -dev.<timestamp>.

.PARAMETER EfMajors
    EF Core majors to build the runtime for.
#>
param(
    [string] $PackRelease = '',
    [switch] $Release,
    [int[]] $EfMajors = @(6, 7, 8, 9, 10)
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE." }
}

$common = @('-c', 'Release')
if ($PackRelease) { $common += "-p:PackRelease=$PackRelease" }
if ($Release) {
    $common += '-p:IsRelease=true'
}
else {
    # A unique -dev suffix per build, shared by every package, so NuGet's package cache never serves a stale
    # copy of an earlier build with the same version.
    $common += "-p:VersionSuffix=dev.$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
}

if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }

foreach ($ef in $EfMajors) {
    Write-Host "== EfMigrationPack for EF Core $ef" -ForegroundColor Cyan
    Invoke-Dotnet pack src/EfMigrationPack/EfMigrationPack.csproj @common "-p:EfMajor=$ef" -o artifacts/packages
}

Write-Host '== EfMigrationPack.Cli' -ForegroundColor Cyan
Invoke-Dotnet pack src/EfMigrationPack.Cli/EfMigrationPack.Cli.csproj @common -o artifacts/packages
Invoke-Dotnet publish src/EfMigrationPack.Cli/EfMigrationPack.Cli.csproj @common -o artifacts/cli-bin

Write-Host '== EfMigrationPack.Tools' -ForegroundColor Cyan
Invoke-Dotnet pack src/EfMigrationPack.Tools/EfMigrationPack.Tools.csproj @common -o artifacts/packages

Get-ChildItem artifacts/packages | Select-Object Name
