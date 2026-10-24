# Package Manager Console front-end for ef-pack.
# Uses the project selected in the console's "Default project" drop-down unless -Project is given,
# the same way Add-Migration and Update-Database do.

function Pack-Migrations {
    <#
    .SYNOPSIS
        Packs every migration that is not already packed into a new packed migration.

    .DESCRIPTION
        Creates PackedMigrationsNNNN (migration id 0000000000NNNN_PackedMigrationsNNNN), whose Up applies
        each original migration that the database has not already recorded, and moves the original
        migration files into PackedMigrationsNNNN.zip.

    .PARAMETER Context
        The DbContext to pack migrations for. Required when the project has several.

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

    $arguments = @('pack') + (Get-EfPackProjectArguments $Project)
    if ($Context) { $arguments += @('--context', $Context) }
    if ($SingleFile) { $arguments += '--single-file' }
    if ($DryRun -or $WhatIfPreference) { $arguments += '--dry-run' }

    Invoke-EfPack $arguments
}

function Unpack-Migrations {
    <#
    .SYNOPSIS
        Restores the newest packed migration's original migrations and removes the pack.

    .DESCRIPTION
        Unzips PackedMigrationsNNNN.zip back to the original locations and deletes the packed
        migration. Packs are unpacked newest first; -All unpacks every pack.

    .PARAMETER Context
        The DbContext to unpack migrations for. Required when the project has several.

    .PARAMETER Project
        The project to use. Defaults to the Package Manager Console's default project.

    .PARAMETER All
        Unpack every pack, newest first.

    .PARAMETER Force
        Unpack even if the pack was edited after packing or restored files would overwrite others.

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

    $arguments = @('unpack') + (Get-EfPackProjectArguments $Project)
    if ($Context) { $arguments += @('--context', $Context) }
    if ($All) { $arguments += '--all' }
    if ($Force) { $arguments += '--force' }
    if ($DryRun -or $WhatIfPreference) { $arguments += '--dry-run' }

    Invoke-EfPack $arguments
}

function Get-EfPackProjectArguments([string] $ProjectName) {
    # Files are read from disk, so unsaved edits in open editors aren't seen; like EF Core's own commands,
    # this doesn't save them for you. (Saving through $DTE.ExecuteCommand('File.SaveAll') was removed:
    # when that fails, Visual Studio reports it in a modal "Value does not fall within the expected range" dialog.)

    if (Get-Command Get-Project -ErrorAction SilentlyContinue) {
        try {
            $dteProject = if ($ProjectName) { Get-Project -Name $ProjectName -ErrorAction Stop } else { Get-Project -ErrorAction Stop }
        }
        catch {
            throw "Couldn't get project '$ProjectName' from Visual Studio: $($_.Exception.Message). Select the migrations project as the Default project, or pass -Project."
        }

        if (-not $dteProject) {
            throw "No project selected. Select the migrations project as the Package Manager Console's Default project, or pass -Project."
        }

        return @('--project', (Get-EfPackProjectDirectory $dteProject))
    }

    # Plain PowerShell (outside Visual Studio): a folder or .csproj path, or the current folder.
    if ($ProjectName) { return @('--project', $ProjectName) }
    return @('--project', (Get-Location).Path)
}

function Get-EfPackProjectDirectory($dteProject) {
    # The same property EF Core's own commands read; the project file path is the fallback.
    try {
        $fullPath = $dteProject.Properties.Item('FullPath').Value
        if ($fullPath) { return $fullPath.TrimEnd('\', '/') }
    }
    catch { }

    try {
        $fullName = $dteProject.FullName
        if ($fullName) { return $fullName }
    }
    catch { }

    throw "Couldn't find the folder of project '$($dteProject.Name)'. Pass -Project with the project's folder or .csproj path."
}

function Invoke-EfPack([string[]] $Arguments) {
    $dll = Join-Path (Join-Path $PSScriptRoot 'bin') 'EfMigrationPack.Cli.dll'
    if (-not (Test-Path $dll)) {
        throw "ef-pack binaries were not found at $dll."
    }

    & dotnet $dll @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ef-pack failed (exit code $LASTEXITCODE)."
    }
}

# The names used before version 1.1, kept so existing scripts keep working.
Set-Alias -Name Compress-Migrations -Value Pack-Migrations
Set-Alias -Name Expand-Migrations -Value Unpack-Migrations

Export-ModuleMember -Function Pack-Migrations, Unpack-Migrations -Alias Compress-Migrations, Expand-Migrations
