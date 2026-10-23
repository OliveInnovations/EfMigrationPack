param($installPath, $toolsPath, $package, $project)

# Run by Visual Studio when the solution loads; makes Pack-Migrations and Unpack-Migrations
# available in the Package Manager Console. -DisableNameChecking because Pack and Unpack aren't on
# PowerShell's approved-verb list, which would otherwise print a warning on every solution load.
Import-Module (Join-Path $toolsPath 'EfMigrationPack.psm1') -Force -DisableNameChecking
