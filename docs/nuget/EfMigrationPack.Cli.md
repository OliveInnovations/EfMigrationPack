# EfMigrationPack.Cli

**Pack hundreds of EF Core migrations into one, from the command line.** Used like `dotnet ef`.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

Every `.Designer.cs` file holds a full copy of your model, so a project with hundreds of migrations builds slowly.
`dotnet ef-pack pack` folds the migrations into one **packed migration** with a single target model, and moves
the originals into a zip outside the build. `dotnet ef-pack unpack` puts them back.

The commands only read and write your project's source files: they never connect to a database or build the
project. Works with EF Core 6 and later; runs on any .NET 6 or later runtime.

Unlike squashing or resetting migrations, it doesn't need every database to be at the latest migration, and nobody
has to rewrite `__EFMigrationsHistory` by hand.

## Install

```
dotnet tool install --global EfMigrationPack.Cli
```

The package is EfMigrationPack.Cli; the command it installs is `dotnet ef-pack`.

Your migrations project also needs the [`EfMigrationPack`](https://www.nuget.org/packages/EfMigrationPack) runtime
package (the major version matching your EF Core major), with `UsePackedMigrations()` on the DbContext options.

## Use

```
dotnet ef-pack pack Sprint10 --dry-run   # show what would change
dotnet ef-pack pack Sprint10             # create the pack Sprint10 and Sprint10.zip
dotnet ef-pack unpack                    # restore the newest pack's original migrations
```

| Option | |
|---|---|
| `<name>` | pack: the pack's name, such as the sprint or release it's for. Its migration id is `00yyMMddHHmmss_<name>`, so it never repeats. |
| `--project <folder or .csproj>` | Project to work on (default: current folder). |
| `--context <DbContext>` | Needed when the project has migrations for more than one context. |
| `--dry-run` | Show what would change without changing anything. |
| `--single-file` | pack: put every original migration in the pack's main file. |
| `--all` | unpack: unpack every pack, newest first. |
| `--force` | unpack: continue even if the pack was edited after packing. |

The 1.0 command names `compress` and `expand` still work.

Prefer Visual Studio? The same commands are in the Package Manager Console via
[`EfMigrationPack.Tools`](https://www.nuget.org/packages/EfMigrationPack.Tools).

Full documentation, the recommended workflow and limitations:
[github.com/OliveInnovations/EfMigrationPack](https://github.com/OliveInnovations/EfMigrationPack)
