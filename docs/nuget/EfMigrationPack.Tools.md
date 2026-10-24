# EfMigrationPack.Tools

**Pack hundreds of EF Core migrations into one, from Visual Studio's Package Manager Console.** Used like
`Add-Migration`.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

Every `.Designer.cs` file holds a full copy of your model, so a project with hundreds of migrations builds slowly.
`Pack-Migrations` folds the migrations into one **packed migration** with a single target model, and moves the
originals into a zip outside the build. `Unpack-Migrations` puts them back.

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
Pack-Migrations Sprint10 -WhatIf    # show what would change
Pack-Migrations Sprint10            # create the pack Sprint10 and Sprint10.zip
Unpack-Migrations                   # restore the newest pack's original migrations
```

| Parameter | |
|---|---|
| `-Name <name>` (first position) | Pack-Migrations: the pack's name, such as the sprint or release it's for. Its migration id is `00yyMMddHHmmss_<name>`, so it never repeats. |
| `-Project <name>` | Use another project instead of the Default project. |
| `-Context <DbContext>` | Needed when the project has migrations for more than one context. |
| `-DryRun` / `-WhatIf` | Show what would change without changing anything. |
| `-SingleFile` | Pack-Migrations: put every original migration in the pack's main file. |
| `-All` | Unpack-Migrations: unpack every pack, newest first. |
| `-Force` | Unpack-Migrations: continue even if the pack was edited after packing. |

The 1.0 names `Compress-Migrations` and `Expand-Migrations` still work.

Prefer the command line? Use [`EfMigrationPack.Cli`](https://www.nuget.org/packages/EfMigrationPack.Cli).

Full documentation, the recommended workflow and limitations:
[github.com/OliveInnovations/EfMigrationPack](https://github.com/OliveInnovations/EfMigrationPack)
