# EfMigrationPack.Tools

**Pack hundreds of EF Core migrations into one, from Visual Studio's Package Manager Console.** Used like
`Add-Migration`.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

Every `.Designer.cs` file holds a full copy of your model, so a project with hundreds of migrations builds slowly.
`Compress-Migrations` folds the migrations into one **packed migration** with a single target model, and moves the
originals into a zip outside the build. `Expand-Migrations` puts them back.

The commands only read and write your project's source files: they never connect to a database or build the
project. Works with EF Core 6 and later.

## Install

```
Install-Package EfMigrationPack.Tools
```

Reopen the solution if the commands don't appear straight away. Your migrations project also needs the
[`EfMigrationPack`](https://www.nuget.org/packages/EfMigrationPack) runtime package (the major version matching your
EF Core major), with `UsePackedMigrations()` on the DbContext options.

## Use

The commands work on the console's **Default project**, like the EF Core commands.

```
Compress-Migrations -WhatIf      # show what would change
Compress-Migrations              # create PackedMigrationsNNNN and PackedMigrationsNNNN.zip
Expand-Migrations                # restore the newest pack's original migrations
```

| Parameter | |
|---|---|
| `-Project <name>` | Use another project instead of the Default project. |
| `-Context <DbContext>` | Needed when the project has migrations for more than one context. |
| `-DryRun` / `-WhatIf` | Show what would change without changing anything. |
| `-SingleFile` | Compress-Migrations: put every original migration in the pack's main file. |
| `-All` | Expand-Migrations: expand every pack, newest first. |
| `-Force` | Expand-Migrations: continue even if the pack was edited after compressing. |

Prefer the command line? Use [`dotnet-ef-pack`](https://www.nuget.org/packages/dotnet-ef-pack).

Full documentation, the recommended workflow and limitations:
[github.com/OliveInnovations/EfMigrationPack](https://github.com/OliveInnovations/EfMigrationPack)
