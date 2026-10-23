param($installPath, $toolsPath, $package, $project)

# Run by Visual Studio when the solution loads; makes Compress-Migrations and Expand-Migrations
# available in the Package Manager Console.
Import-Module (Join-Path $toolsPath 'EfMigrationPack.psm1') -Force -DisableNameChecking
