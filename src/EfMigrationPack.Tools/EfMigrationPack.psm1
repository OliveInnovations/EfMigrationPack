# Package Manager Console front-end for ef-pack.
# Uses the project selected in the console's "Default project" drop-down unless -Project is given,
# the same way Add-Migration and Update-Database do.

function Compress-Migrations {
    <#
    .SYNOPSIS
        Packs every migration that is not already packed into a new packed migration.

    .DESCRIPTION
        Creates PackedMigrationsNNNN (migration id 0000000000NNNN_PackedMigrationsNNNN), whose Up applies
        each original migration that the database has not already recorded, and moves the original
        migration files into PackedMigrationsNNNN.zip.

    .PARAMETER Context
        The DbContext to compress migrations for. Required when the project has several.

    .PARAMETER Project
        The project to use. Defaults to the Package Manager Console's default project.

    .PARAMETER SingleFile
        Put every step in the pack's main file instead of a PackedMigrationsNNNN.Steps folder.

    .PARAMETER DryRun
        Show what would change without changing anything. -WhatIf does the same.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [string] $Context,
        [string] $Project,
        [switch] $SingleFile,
        [switch] $DryRun
    )

    $arguments = @('compress') + (Get-EfPackProjectArguments $Project)
    if ($Context) { $arguments += @('--context', $Context) }
    if ($SingleFile) { $arguments += '--single-file' }
    if ($DryRun -or $WhatIfPreference) { $arguments += '--dry-run' }

    Invoke-EfPack $arguments
}

function Expand-Migrations {
    <#
    .SYNOPSIS
        Restores the newest packed migration's original migrations and removes the pack.

    .DESCRIPTION
        Unzips PackedMigrationsNNNN.zip back to the original locations and deletes the packed
        migration. Packs are expanded newest first; -All expands every pack.

    .PARAMETER Context
        The DbContext to expand migrations for. Required when the project has several.

    .PARAMETER Project
        The project to use. Defaults to the Package Manager Console's default project.

    .PARAMETER All
        Expand every pack, newest first.

    .PARAMETER Force
        Expand even if the pack was edited after compressing or restored files would overwrite others.

    .PARAMETER DryRun
        Show what would change without changing anything. -WhatIf does the same.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [string] $Context,
        [string] $Project,
        [switch] $All,
        [switch] $Force,
        [switch] $DryRun
    )

    $arguments = @('expand') + (Get-EfPackProjectArguments $Project)
    if ($Context) { $arguments += @('--context', $Context) }
    if ($All) { $arguments += '--all' }
    if ($Force) { $arguments += '--force' }
    if ($DryRun -or $WhatIfPreference) { $arguments += '--dry-run' }

    Invoke-EfPack $arguments
}

function Get-EfPackProjectArguments([string] $ProjectName) {
    # The tool reads files from disk, so save open editors first (Package Manager Console only).
    if (Get-Variable -Name DTE -Scope Global -ErrorAction SilentlyContinue) {
        try { $global:DTE.ExecuteCommand('File.SaveAll') } catch { }
    }

    if (Get-Command Get-Project -ErrorAction SilentlyContinue) {
        $dteProject = if ($ProjectName) { Get-Project -Name $ProjectName } else { Get-Project }
        if (-not $dteProject) {
            throw "Project '$ProjectName' was not found. Select a default project or pass -Project."
        }

        return @('--project', $dteProject.FullName)
    }

    # Plain PowerShell (outside Visual Studio): a path, or the current folder.
    if ($ProjectName) { return @('--project', $ProjectName) }
    return @('--project', (Get-Location).Path)
}

function Invoke-EfPack([string[]] $Arguments) {
    $dll = Join-Path $PSScriptRoot 'bin\EfMigrationPack.Cli.dll'
    if (-not (Test-Path $dll)) {
        throw "ef-pack binaries were not found at $dll."
    }

    & dotnet $dll @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ef-pack failed (exit code $LASTEXITCODE)."
    }
}

Export-ModuleMember -Function Compress-Migrations, Expand-Migrations
