# dotnet-ef-pack

**Pack hundreds of EF Core migrations into one, from the command line.** Used like `dotnet ef`.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/YOUR_BMC_HANDLE)

Every `.Designer.cs` file holds a full copy of your model, so a project with hundreds of migrations builds slowly.
`dotnet ef-pack compress` folds the migrations into one **packed migration** with a single target model, and moves
the originals into a zip outside the build. `dotnet ef-pack expand` puts them back.

The commands only read and write your project's source files: they never connect to a database or build the
project. Works with EF Core 6 and later; runs on any .NET 6 or later runtime.

## Install

```
dotnet tool install --global dotnet-ef-pack
```

Your migrations project also needs the [`EfMigrationPack`](https://www.nuget.org/packages/EfMigrationPack) runtime
package (the major version matching your EF Core major), with `UsePackedMigrations()` on the DbContext options.

## Use

```
dotnet ef-pack compress --dry-run     # show what would change
dotnet ef-pack compress               # create PackedMigrationsNNNN and PackedMigrationsNNNN.zip
dotnet ef-pack expand                 # restore the newest pack's original migrations
```

| Option | |
|---|---|
| `--project <folder or .csproj>` | Project to work on (default: current folder). |
| `--context <DbContext>` | Needed when the project has migrations for more than one context. |
| `--dry-run` | Show what would change without changing anything. |
| `--single-file` | compress: put every original migration in the pack's main file. |
| `--all` | expand: expand every pack, newest first. |
| `--force` | expand: continue even if the pack was edited after compressing. |

Prefer Visual Studio? The same commands are in the Package Manager Console via
[`EfMigrationPack.Tools`](https://www.nuget.org/packages/EfMigrationPack.Tools).

Full documentation, the recommended workflow and limitations:
[github.com/YOUR_GITHUB_USER/ef-pack](https://github.com/YOUR_GITHUB_USER/ef-pack)
