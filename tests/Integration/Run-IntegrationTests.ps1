<#
.SYNOPSIS
    End-to-end test of the packages against a real SQLite database, once per EF Core major.

.DESCRIPTION
    Uses the packages in artifacts/packages (run ./build.ps1 first). For each EF Core major it copies
    tests/Integration/TestApp to artifacts/integration/ef{N}/, targets the matching .NET version, and:

      1. adds an Initial migration (one table)
      2. changes the model and adds a second migration (a new column, a new table and an index)
      3. applies only Initial to a second database, to test a database part-way through the migrations
      4. generates the SQL script for all migrations
      5. checks "dotnet ef-pack pack --dry-run" changes nothing
      6. packs with Pack-Migrations (the Package Manager Console module, run outside Visual Studio)
      7. generates the SQL script again and compares it with step 4
      8. applies the migrations to a new database, and to the part-way database from step 3
      9. unpacks with "dotnet ef-pack unpack" and checks the files are restored byte for byte
     10. applies the migrations again and checks nothing new is applied
     11. adds a third migration and packs again under the same name, which must give a new pack id
     12. applies that pack to the database from step 8, which must run only the third migration

    Needs the .NET runtime each EF Core major targets: .NET 6 for EF Core 6 and 7, .NET 8 for 8 and 9,
    .NET 10 for 10.

.PARAMETER EfMajors
    EF Core majors to test.

.PARAMETER PackagesDir
    Folder containing the built packages. Defaults to artifacts/packages.

.EXAMPLE
    ./build.ps1 -EfMajors 8
    ./tests/Integration/Run-IntegrationTests.ps1 -EfMajors 8
#>
param(
    [int[]] $EfMajors = @(6, 7, 8, 9, 10),
    [string] $PackagesDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $PackagesDir) { $PackagesDir = Join-Path $repoRoot 'artifacts/packages' }
$PackagesDir = (Resolve-Path $PackagesDir).Path
$templateDir = Join-Path $PSScriptRoot 'TestApp'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$targetFrameworks = @{ 6 = 'net6.0'; 7 = 'net6.0'; 8 = 'net8.0'; 9 = 'net8.0'; 10 = 'net10.0' }
$packName = 'Sprint1'
$script:packId = $null # set once a pack exists; its id includes the time it was made

# ------------------------------------------------------------------ helpers

function Find-PackageVersion([string] $Id, [string] $MajorPrefix = '') {
    $pattern = '^' + [regex]::Escape($Id) + '\.(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\.nupkg$'
    $versions = Get-ChildItem $PackagesDir -File |
        ForEach-Object { if ($_.Name -match $pattern) { $Matches.version } } |
        Where-Object { -not $MajorPrefix -or $_.StartsWith("$MajorPrefix.") }
    $versions = @($versions)
    if ($versions.Count -ne 1) {
        throw "Expected exactly one $Id package$(if ($MajorPrefix) { " $MajorPrefix.x" }) in $PackagesDir, found $($versions.Count). Run ./build.ps1 first."
    }
    return $versions[0]
}

# Runs a command, logs its output, and fails the step if it exits non-zero. Returns the output as one string.
function Invoke-Logged([string] $Exe, [string[]] $Arguments) {
    $line = "> $Exe $($Arguments -join ' ')"
    Add-Content -Path $script:log -Value $line
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = (& $Exe @Arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    Add-Content -Path $script:log -Value $output
    $script:lastOutput = "$line`n$output"
    if ($code -ne 0) {
        throw "'$Exe $($Arguments -join ' ')' failed with exit code $code."
    }

    return $output
}

function Invoke-Ef([string] $Database, [string[]] $Arguments) {
    $env:EFPACK_TEST_DB = $Database
    Push-Location $script:appDir
    try { return Invoke-Logged 'dotnet' (@('ef') + $Arguments) }
    finally { Pop-Location }
}

function Invoke-EfPack([string[]] $Arguments) {
    Push-Location $script:appDir
    try { return Invoke-Logged 'dotnet' (@('ef-pack') + $Arguments) }
    finally { Pop-Location }
}

function Get-Dump([string] $Database) {
    Push-Location $script:appDir
    try { $output = Invoke-Logged 'dotnet' @('run', '--no-build', '--', 'dump', $Database) }
    finally { Pop-Location }
    $json = ($output -split "`n" | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1)
    if (-not $json) { throw "TestApp dump printed no JSON for $Database." }
    return $json | ConvertFrom-Json
}

# Relative path -> SHA-256 of every file under a folder, as one comparable string.
function Get-TreeFingerprint([string] $Folder) {
    $lines = Get-ChildItem $Folder -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Folder, $_.FullName).Replace('\', '/')
        "$relative $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
    }
    return ($lines -join "`n")
}

# Splits a migrations script into statements, leaving out transaction statements and the pack's own history row,
# which are the only differences a pack is allowed to make.
function Get-ComparableStatements([string] $Path) {
    $text = (Get-Content $Path -Raw).Replace("`r`n", "`n")
    return @($text -split ';\s*\n' |
        ForEach-Object { $_.Trim().TrimEnd(';').Trim() } |
        Where-Object { $_ -and $_ -notmatch '^(BEGIN TRANSACTION|COMMIT)$' -and $_ -notlike "*'$($script:packId)'*" })
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Check failed: $Message" }
}

function Assert-Matches([string] $Text, [string] $Pattern, [string] $What) {
    if ($Text -notmatch $Pattern) {
        throw "Check failed: expected $What (pattern: $Pattern)."
    }
}

function Get-Columns($Dump, [string] $Table) {
    $property = $Dump.tables.PSObject.Properties[$Table]
    if (-not $property) { return '(no table)' }
    return (@($property.Value) -join ',')
}

function Assert-Database($Dump, [string[]] $ExpectedHistory, [string] $What, [string] $ExpectedWidgets = 'Id,Name,Description') {
    $history = @($Dump.history) -join ', '
    Assert-True ($history -eq ($ExpectedHistory -join ', ')) "$What history is [$history], expected [$($ExpectedHistory -join ', ')]."
    $widgets = Get-Columns $Dump 'Widgets'
    $categories = Get-Columns $Dump 'Categories'
    Assert-True ($widgets -eq $ExpectedWidgets) "$What Widgets columns are [$widgets], expected [$ExpectedWidgets]."
    Assert-True ($categories -eq 'Id,Title') "$What Categories columns are [$categories], expected [Id,Title]."
}

# Runs Pack-Migrations from the Package Manager Console module and returns the new pack's migration id.
function Invoke-PackMigrations([string] $Name) {
    Push-Location $script:appDir
    try { $output = (Pack-Migrations $Name *>&1 | ForEach-Object { "$_" }) -join "`n" }
    finally { Pop-Location }
    Add-Content -Path $script:log -Value "> Pack-Migrations $Name`n$output"
    $script:lastOutput = "> Pack-Migrations $Name`n$output"
    Assert-True ($output -notmatch '(?m)^warning:') 'Pack-Migrations printed no warnings.'

    $files = @(Get-ChildItem (Join-Path $script:appDir 'Migrations') -Filter "*_$Name.cs" | Where-Object { $_.Name -notlike '*.Designer.cs' })
    Assert-True ($files.Count -eq 1) "Pack-Migrations created one pack called $Name."
    $id = $files[0].BaseName
    Assert-True ($id -match "^00\d{12}_$Name`$") "the pack id $id is 00yyMMddHHmmss_$Name."
    return $id
}

function Test-RuntimeInstalled([string] $TargetFramework) {
    $major = $TargetFramework.Substring(3).Split('.')[0]
    return [bool]((& dotnet --list-runtimes) -match "^Microsoft\.NETCore\.App $major\.")
}

# ------------------------------------------------------------------ test

function Test-EfMajor([int] $EfMajor) {
    $tfm = $targetFrameworks[$EfMajor]
    if (-not $tfm) { throw "EF Core $EfMajor is not supported by this test." }
    if (-not (Test-RuntimeInstalled $tfm)) { throw "The .NET $($tfm.Substring(3)) runtime is needed for EF Core $EfMajor and isn't installed." }

    $packVersion = Find-PackageVersion 'EfMigrationPack' "$EfMajor"
    $cliVersion = Find-PackageVersion 'EfMigrationPack.Cli'
    $toolsVersion = Find-PackageVersion 'EfMigrationPack.Tools'

    $work = Join-Path $repoRoot "artifacts/integration/ef$EfMajor"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work | Out-Null
    $script:log = Join-Path $work 'test.log'
    $script:appDir = Join-Path $work 'TestApp'
    $databases = Join-Path $work 'databases'
    New-Item -ItemType Directory -Path $databases | Out-Null
    $freshDb = Join-Path $databases 'fresh.db'
    $partialDb = Join-Path $databases 'partial.db'
    $absentDb = Join-Path $databases 'never-created.db'

    Copy-Item $templateDir $script:appDir -Recurse

    # Stop the repository's own Directory.Build.* files applying, and supply the versions under test.
    Set-Content (Join-Path $work 'Directory.Build.props') @"
<Project>
  <PropertyGroup>
    <TestTargetFramework>$tfm</TestTargetFramework>
    <EfCoreVersion>$EfMajor.*</EfCoreVersion>
    <EfPackVersion>$packVersion</EfPackVersion>
  </PropertyGroup>
</Project>
"@
    Set-Content (Join-Path $work 'Directory.Build.targets') '<Project />'

    # EfMigrationPack packages come only from the local build; everything else from nuget.org.
    $localSource = [Security.SecurityElement]::Escape($PackagesDir)
    Set-Content (Join-Path $work 'NuGet.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$localSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="EfMigrationPack*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@

    $step = 'Restore and install tools'
    try {
        Write-Host "  $step"
        Push-Location $script:appDir
        try { Invoke-Logged 'dotnet' @('restore') | Out-Null } finally { Pop-Location }

        $assets = Get-Content (Join-Path $script:appDir 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $designKey = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.EntityFrameworkCore.Design/*' })[0]
        $efVersion = $designKey.Split('/')[1]

        Push-Location $work
        try {
            Invoke-Logged 'dotnet' @('new', 'tool-manifest') | Out-Null
            Invoke-Logged 'dotnet' @('tool', 'install', 'dotnet-ef', '--version', $efVersion) | Out-Null
            Invoke-Logged 'dotnet' @('tool', 'install', 'EfMigrationPack.Cli', '--version', $cliVersion) | Out-Null
        }
        finally { Pop-Location }

        $toolsDir = Join-Path $work 'EfMigrationPack.Tools'
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $PackagesDir "EfMigrationPack.Tools.$toolsVersion.nupkg"), $toolsDir)
        Import-Module (Join-Path $toolsDir 'tools/EfMigrationPack.psm1') -Force -DisableNameChecking
        Write-Host "    EF Core $efVersion ($tfm), EfMigrationPack $packVersion, EfMigrationPack.Cli $cliVersion, EfMigrationPack.Tools $toolsVersion"

        $migrations = Join-Path $script:appDir 'Migrations'

        $step = '1. Add the Initial migration'
        Write-Host "  $step"
        Invoke-Ef $absentDb @('migrations', 'add', 'Initial') | Out-Null
        $files = @(Get-ChildItem $migrations -Filter '*_Initial.cs' | Where-Object { $_.Name -notlike '*.Designer.cs' })
        Assert-True ($files.Count -eq 1) 'the Initial migration file was created.'
        $initialId = $files[0].BaseName

        $step = '2. Change the model and add a second migration'
        Write-Host "  $step"
        Start-Sleep -Seconds 1 # migration ids are timestamps to the second
        Copy-Item (Join-Path $script:appDir 'stages/Model.v2.cs.txt') (Join-Path $script:appDir 'Model.cs') -Force
        Invoke-Ef $absentDb @('migrations', 'add', 'AddCategories') | Out-Null
        $files = @(Get-ChildItem $migrations -Filter '*_AddCategories.cs' | Where-Object { $_.Name -notlike '*.Designer.cs' })
        Assert-True ($files.Count -eq 1) 'the AddCategories migration file was created.'
        $secondId = $files[0].BaseName
        Assert-True ([string]::CompareOrdinal($initialId, $secondId) -lt 0) "$initialId sorts before $secondId."

        $step = '3. Apply only Initial to the part-way database'
        Write-Host "  $step"
        Invoke-Ef $partialDb @('database', 'update', 'Initial') | Out-Null
        $dump = Get-Dump $partialDb
        Assert-True ((@($dump.history) -join ',') -eq $initialId) "the part-way database has only $initialId."

        $before = Get-TreeFingerprint $migrations
        $snapshotFile = (Get-ChildItem $migrations -Filter '*ModelSnapshot.cs').FullName
        $snapshotHash = (Get-FileHash $snapshotFile -Algorithm SHA256).Hash

        $step = '4. Generate the SQL script'
        Write-Host "  $step"
        $beforeSql = Join-Path $work 'before.sql'
        Invoke-Ef $absentDb @('migrations', 'script', '--output', $beforeSql) | Out-Null

        $step = '5. dotnet ef-pack pack --dry-run changes nothing'
        Write-Host "  $step"
        Invoke-EfPack @('pack', $packName, '--dry-run') | Out-Null
        Assert-True ((Get-TreeFingerprint $migrations) -eq $before) 'the dry run left the migrations folder unchanged.'

        $step = '6. Pack-Migrations'
        Write-Host "  $step"
        $packId = Invoke-PackMigrations $packName
        $script:packId = $packId

        foreach ($expected in @("$packId.cs", "$packId.Designer.cs", "$packName.zip",
                "$packName.Steps/$initialId.cs", "$packName.Steps/$secondId.cs")) {
            Assert-True (Test-Path (Join-Path $migrations $expected)) "Pack-Migrations created Migrations/$expected."
        }

        foreach ($original in @("$initialId.cs", "$initialId.Designer.cs", "$secondId.cs", "$secondId.Designer.cs")) {
            Assert-True (-not (Test-Path (Join-Path $migrations $original))) "Pack-Migrations moved Migrations/$original into the zip."
        }

        Assert-True ((Get-FileHash $snapshotFile -Algorithm SHA256).Hash -eq $snapshotHash) 'the model snapshot is unchanged.'

        $step = '7. Generate the SQL script again and compare'
        Write-Host "  $step"
        $afterSql = Join-Path $work 'after.sql'
        Invoke-Ef $absentDb @('migrations', 'script', '--output', $afterSql) | Out-Null
        Assert-True (-not (Test-Path $absentDb)) 'generating scripts did not create a database.'

        $beforeStatements = Get-ComparableStatements $beforeSql
        $afterStatements = Get-ComparableStatements $afterSql
        $difference = Compare-Object $beforeStatements $afterStatements -SyncWindow 0
        if ($difference) {
            $details = ($difference | ForEach-Object { "$(if ($_.SideIndicator -eq '<=') { 'before only' } else { 'after only' }): $($_.InputObject)" }) -join "`n"
            throw "Check failed: the scripts differ beyond the pack's history row and transactions:`n$details"
        }
        Assert-Matches (Get-Content $afterSql -Raw) ([regex]::Escape("'$packId'")) "the pack's own history row in after.sql"

        $step = '8a. Apply the migrations to a new database'
        Write-Host "  $step"
        $output = Invoke-Ef $freshDb @('database', 'update')
        Assert-Matches $output "2 of 2 migration\(s\) to apply" 'the pack to report 2 of 2 migrations to apply'
        Assert-Matches $output ([regex]::Escape("Applied packed migration '$initialId' (1/2)")) "$initialId to be reported as applied"
        Assert-Matches $output ([regex]::Escape("Applied packed migration '$secondId' (2/2)")) "$secondId to be reported as applied"
        $freshDump = Get-Dump $freshDb
        Assert-Database $freshDump @($packId, $initialId, $secondId) 'The new database'

        $step = '8b. Apply the migrations to the part-way database'
        Write-Host "  $step"
        $output = Invoke-Ef $partialDb @('database', 'update')
        Assert-Matches $output "1 of 2 migration\(s\) to apply, 1 already applied" 'the pack to report 1 of 2 migrations to apply'
        Assert-Matches $output ([regex]::Escape("Applied packed migration '$secondId' (1/1)")) "$secondId to be reported as applied"
        Assert-True ($output -notmatch [regex]::Escape("Applied packed migration '$initialId'")) "$initialId was not applied again."
        Assert-Database (Get-Dump $partialDb) @($packId, $initialId, $secondId) 'The part-way database'

        $step = '9. dotnet ef-pack unpack'
        Write-Host "  $step"
        Invoke-EfPack @('unpack') | Out-Null
        $after = Get-TreeFingerprint $migrations
        if ($after -ne $before) {
            throw "Check failed: unpack didn't restore the migrations folder exactly.`nBefore pack:`n$before`nAfter unpack:`n$after"
        }

        $step = '10. Apply the migrations again'
        Write-Host "  $step"
        $output = Invoke-Ef $freshDb @('database', 'update')
        Assert-Matches $output 'No migrations were applied' 'EF to report that no migrations were applied'
        $finalDump = Get-Dump $freshDb
        Assert-True ((@($finalDump.history) -join ',') -eq (@($freshDump.history) -join ',')) 'no history rows were added.'
        Assert-Database $finalDump @($packId, $initialId, $secondId) 'The database'

        $step = '11. Add a migration and pack again under the same name'
        Write-Host "  $step"
        Start-Sleep -Seconds 1
        Copy-Item (Join-Path $script:appDir 'stages/Model.v3.cs.txt') (Join-Path $script:appDir 'Model.cs') -Force
        Invoke-Ef $absentDb @('migrations', 'add', 'AddQuantity') | Out-Null
        $files = @(Get-ChildItem $migrations -Filter '*_AddQuantity.cs' | Where-Object { $_.Name -notlike '*.Designer.cs' })
        Assert-True ($files.Count -eq 1) 'the AddQuantity migration file was created.'
        $thirdId = $files[0].BaseName

        $secondPackId = Invoke-PackMigrations $packName
        $script:packId = $secondPackId
        Assert-True ($secondPackId -ne $packId) "packing again under the same name gave a new id ($packId, then $secondPackId)."
        Assert-True ([string]::CompareOrdinal($packId, $secondPackId) -lt 0) "$secondPackId sorts after $packId."
        Assert-True ([string]::CompareOrdinal($secondPackId, $initialId) -lt 0) "$secondPackId sorts before every ordinary migration."
        Assert-True (Test-Path (Join-Path $migrations "$packName.Steps/$thirdId.cs")) "the new pack contains $thirdId."

        $step = '12. Apply the new pack to the database that applied the first'
        Write-Host "  $step"
        $output = Invoke-Ef $freshDb @('database', 'update')
        Assert-Matches $output "1 of 3 migration\(s\) to apply, 2 already applied" 'the new pack to report 1 of 3 migrations to apply'
        Assert-Matches $output ([regex]::Escape("Applied packed migration '$thirdId' (1/1)")) "$thirdId to be reported as applied"
        Assert-Database (Get-Dump $freshDb) @($packId, $secondPackId, $initialId, $secondId, $thirdId) 'The database' 'Id,Name,Description,Quantity'
    }
    catch {
        $message = "${step}: $($_.Exception.Message)"
        if ($script:lastOutput) { $message += "`nLast command:`n$($script:lastOutput)" }
        throw $message
    }
    finally {
        Remove-Module EfMigrationPack -ErrorAction SilentlyContinue
        Remove-Item Env:EFPACK_TEST_DB -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ run

$results = [ordered]@{}
foreach ($ef in $EfMajors) {
    Write-Host "== EF Core $ef" -ForegroundColor Cyan
    $script:lastOutput = $null
    try {
        Test-EfMajor $ef
        $results["EF Core $ef"] = 'passed'
        Write-Host "  passed" -ForegroundColor Green
    }
    catch {
        $results["EF Core $ef"] = 'FAILED'
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
$results.GetEnumerator() | ForEach-Object { Write-Host ("{0,-12} {1}" -f $_.Key, $_.Value) }
Write-Host "Logs, scripts and databases: $(Join-Path $repoRoot 'artifacts/integration')"
if ($results.Values -contains 'FAILED') { exit 1 }
