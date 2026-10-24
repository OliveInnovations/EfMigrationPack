# EfMigrationPack

**Runtime for packed EF Core migrations.** Reference it from the project that contains your migrations.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

Hundreds of migrations make a project slow to build, because every `.Designer.cs` file holds a full copy of your
model. The `dotnet ef-pack` command folds them into one **packed migration** with a single target model and moves the
originals into a zip. This package is what makes a pack safe to apply:

- each original migration in the pack runs only if the database hasn't recorded it, and is then recorded under its
  own id, so databases at any point in your history catch up correctly;
- every migration is reported as it finishes, and a failure names the migration that was running.

Unlike squashing or resetting migrations, it doesn't need every database to be at the latest migration, and nobody
has to rewrite `__EFMigrationsHistory` by hand.

## Choose the right version

The package's major version matches the EF Core major it is built for, like a database provider:

| EF Core | EfMigrationPack | Target framework |
|---|---|---|
| 6 | 6.x | .NET 6+ |
| 7 | 7.x | .NET 6+ |
| 8 | 8.x | .NET 8+ |
| 9 | 9.x | .NET 8+ |
| 10 | 10.x | .NET 10+ |

## Enable it

Call `UsePackedMigrations()` wherever the context is configured, including any `IDesignTimeDbContextFactory`, so
`dotnet ef` and the Package Manager Console use it too:

```csharp
using EfMigrationPack;

services.AddDbContext<AppDbContext>(options => options
    .UseSqlServer(connectionString)
    .UsePackedMigrations());
```

## Output when a pack is applied

```
Applying migration '00261024111000_Sprint10'.
Packed migration '00261024111000_Sprint10': 3 of 5 migration(s) to apply, 2 already applied.
Applied packed migration '20260310091500_AddInvoices' (1/3).
Applied packed migration '20260402140200_AddInvoiceLines' (2/3).
Applied packed migration '20260415083000_IndexInvoiceDate' (3/3).
```

Messages go through EF Core's migrations logger, so they appear in `dotnet ef`, `Update-Database`, migration bundles
and your application's logs. To also write them straight to the console:
`.UsePackedMigrations(packed => packed.WriteToConsole())`.

## Creating packs

Use the [`EfMigrationPack.Cli`](https://www.nuget.org/packages/EfMigrationPack.Cli) command-line tool or the
[`EfMigrationPack.Tools`](https://www.nuget.org/packages/EfMigrationPack.Tools) Package Manager Console commands.

Full documentation, the recommended workflow and limitations:
[github.com/OliveInnovations/EfMigrationPack](https://github.com/OliveInnovations/EfMigrationPack)
