# ef-pack

**Pack hundreds of EF Core migrations into one, without losing any of them.**

[![EfMigrationPack](https://img.shields.io/nuget/v/EfMigrationPack?label=EfMigrationPack)](https://www.nuget.org/packages/EfMigrationPack)
[![dotnet-ef-pack](https://img.shields.io/nuget/v/dotnet-ef-pack?label=dotnet-ef-pack)](https://www.nuget.org/packages/dotnet-ef-pack)
[![EfMigrationPack.Tools](https://img.shields.io/nuget/v/EfMigrationPack.Tools?label=EfMigrationPack.Tools)](https://www.nuget.org/packages/EfMigrationPack.Tools)
[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

A project with hundreds of migrations builds slowly, mostly because every `.Designer.cs` file holds a complete copy
of your model as it was at that point. ef-pack folds the migrations into a single **packed migration** with one
target model, and moves the original files into a zip outside the build.

Nothing is lost. When a pack is applied, each original migration inside it runs only if the database hasn't
recorded it, and it's recorded under its own id. A developer whose database is part-way through those migrations
gets exactly the ones they're missing, and every migration is reported as it runs. You can expand a pack back into
its original files at any time.

Works with EF Core 6, 7, 8, 9 and 10, on any relational provider.

## Packages

| Package | What it is |
|---|---|
| [`EfMigrationPack`](https://www.nuget.org/packages/EfMigrationPack) | Runtime, referenced by the project containing your migrations. **Use the version whose major matches your EF Core major** (8.x for EF Core 8). |
| [`dotnet-ef-pack`](https://www.nuget.org/packages/dotnet-ef-pack) | Command-line tool: `dotnet ef-pack`, used like `dotnet ef`. |
| [`EfMigrationPack.Tools`](https://www.nuget.org/packages/EfMigrationPack.Tools) | Package Manager Console commands `Compress-Migrations` and `Expand-Migrations`, like `Add-Migration`. |

### Requirements

Nothing beyond what your EF Core version already needs:

| EF Core | `EfMigrationPack` version | Target framework |
|---|---|---|
| 6 | 6.x | .NET 6 or later |
| 7 | 7.x | .NET 6 or later |
| 8 | 8.x | .NET 8 or later |
| 9 | 9.x | .NET 8 or later |
| 10 | 10.x | .NET 10 or later |

`dotnet-ef-pack` and `EfMigrationPack.Tools` run on any installed .NET 6 or later runtime. Only building this
repository from source needs the .NET 10 SDK.

## Getting started

### 1. Add the runtime to your migrations project

```
dotnet add package EfMigrationPack --version 8.0.0
```

Pick the major that matches your `Microsoft.EntityFrameworkCore` version. A mismatch is refused at restore.

### 2. Enable packed migrations

Call `UsePackedMigrations()` everywhere the context is configured, **including any
`IDesignTimeDbContextFactory`**, so `dotnet ef` and the Package Manager Console use it too:

```csharp
using EfMigrationPack;

services.AddDbContext<AppDbContext>(options => options
    .UseSqlServer(connectionString)
    .UsePackedMigrations());
```

If a pack is applied without it, EF stops with an error saying so, rather than re-running migrations.

### 3. Install the commands

Command line, globally or as a local tool:

```
dotnet tool install --global dotnet-ef-pack
```

Package Manager Console in Visual Studio:

```
Install-Package EfMigrationPack.Tools
```

### 4. Pack

```
dotnet ef-pack compress --dry-run
dotnet ef-pack compress
```

or in the Package Manager Console:

```
Compress-Migrations -WhatIf
Compress-Migrations
```

Then build, [verify](#verify-a-pack-before-committing), and commit.

## Commands

| Command line | Package Manager Console | What it does |
|---|---|---|
| `dotnet ef-pack compress` | `Compress-Migrations` | Packs every migration not already packed into a new `PackedMigrationsNNNN` and moves the originals into `PackedMigrationsNNNN.zip`. |
| `dotnet ef-pack expand` | `Expand-Migrations` | Restores the newest pack's original migrations from its zip and removes the pack. |

| Option (CLI / PMC) | Applies to | |
|---|---|---|
| `--project` / `-Project` | both | Project folder or `.csproj`. The PMC defaults to the console's **Default project**; the CLI to the current folder. |
| `--context` / `-Context` | both | The DbContext, when the project has migrations for more than one. |
| `--dry-run` / `-DryRun` or `-WhatIf` | both | Show what would change without changing anything. |
| `--single-file` / `-SingleFile` | compress | Put every original migration in the pack's main file instead of a `PackedMigrationsNNNN.Steps` folder. |
| `--all` / `-All` | expand | Expand every pack, newest first. |
| `--force` / `-Force` | expand | Expand even if the pack was edited after compressing, or a restored file would overwrite a different one. |

The commands parse your source files; they don't build the project, so they're quick.

## What you see when a pack is applied

Every migration in a pack is reported as it finishes, for example from `dotnet ef database update`:

```
Applying migration '00000000000002_PackedMigrations0002'.
Packed migration '00000000000002_PackedMigrations0002': 3 of 5 migration(s) to apply, 2 already applied.
Applied packed migration '20260310091500_AddInvoices' (1/3).
Applied packed migration '20260402140200_AddInvoiceLines' (2/3).
Applied packed migration '20260415083000_IndexInvoiceDate' (3/3).
Done.
```

If a command fails, an error names the packed migration that was running. Skipped migrations are logged at Debug
level (`dotnet ef database update --verbose` shows them).

The messages go through EF Core's migrations logger (category `Microsoft.EntityFrameworkCore.Migrations`), so they
appear wherever EF's own "Applying migration" lines do: `dotnet ef`, `Update-Database`, migration bundles, and your
application's logs. If your application calls `Migrate()` without console logging, ask for console output as well:

```csharp
options.UseSqlServer(connectionString)
       .UsePackedMigrations(packed => packed.WriteToConsole());
```

## How it works

`compress` creates, next to your model snapshot:

- `0000000000NNNN_PackedMigrationsNNNN.cs`: `Up` lists every original migration in id order, `Down` lists them in
  reverse.
- `0000000000NNNN_PackedMigrationsNNNN.Designer.cs`: one target model, copied from the model snapshot.
- `PackedMigrationsNNNN.Steps/`: each original migration as a nested class, its code unchanged except that `Up` and
  `Down` become `public`.
- `PackedMigrationsNNNN.zip`: the original `.cs` and `.Designer.cs` files, at their project-relative paths, with a
  checksum manifest. The zip is written and verified before any original is removed.

Earlier packs are never changed; each run makes a new one.

**Why the ids look like `00000000000001_PackedMigrations0001`.** EF decides what to run purely by migration id,
sorted as text. Pack ids are a sequence padded to look like timestamps, so packs sort in the order they were made
and before every ordinary migration. A migration merged after a pack, even one with an older timestamp, sorts after
the packs and goes into the next pack. An empty database then runs it in the same order existing databases did.

**Applying a pack.** For each original migration, the pack checks the history table. If the id is missing, it runs
the migration and inserts that id; if it's present, it skips it. `Down` does the reverse. Every database therefore
keeps a history row for every original migration, which is also what lets `expand` work without touching databases.

## Expanding

`expand` restores the newest pack's original files from its zip, then deletes the pack and the zip. Packs are
expanded newest first, because an older pack's migrations would otherwise sort after a newer pack. Before changing
anything it checks the zip's checksums, that the pack and the zip describe the same migrations, that no packed
migration was edited after compressing, and that nothing would be overwritten.

Databases need nothing doing afterwards: the restored migrations are already recorded there by id.

## Recommended workflow

1. When you cut a release branch, run `dotnet ef-pack compress` on a clean working tree.
2. Verify (below), commit, and merge to main.
3. Developers who sync main get one new pack instead of each feature's migrations. Feature branches keep their own
   migration files until they merge.

Only compress on one branch at a time: two branches compressing concurrently both create the same
`PackedMigrationsNNNN`, and whichever merges second must expand and compress again.

## Verify a pack before committing

1. **Script diff.** Before compressing, run `dotnet ef migrations script 0 -o before.sql` with a connection string
   for a database that doesn't exist on a reachable server. Repeat after compressing into `after.sql` and diff them.
   Only the extra history `INSERT`s and transaction grouping should differ.
2. **Part-way database.** Apply to a copy of a database that is part-way through the packed migrations and check its
   schema and history.
3. **Round trip.** `dotnet ef database update 0`, then `dotnet ef database update`, on a scratch database.
4. **Expand round trip.** `dotnet ef-pack expand`, then `git status`: the restored files should be identical.

## Limitations

- **Scripts reflect the database they're generated against.** Which migrations a pack includes is decided when EF
  generates the SQL, from the connected database's history table. An idempotent script generated against an empty
  database includes every migration in the pack. That's unsafe on a database that already has some of them, which
  includes **production at your first pack**. Deploy packs to such databases with `Migrate()` or a migration bundle,
  which decide at run time, or generate the script against a copy of the target.
- **One target model per pack.** Every packed migration's SQL is generated against the model at pack time. This
  rarely matters on SQL Server; on SQLite, operations that rebuild tables can differ. The script diff catches it.
- **One transaction per pack** on providers that use transactions, instead of one per migration.
- **No intermediate targets inside a pack.** `database update` to a migration inside a pack isn't possible; expand
  first.
- **Packs are generated code.** Don't edit them or `migrations remove` them; expand instead.
- The tools warn about migrations that use `Migration.TargetModel`, `UpOperations` or `DownOperations`, refer to
  their own class name, have no `Down`, or have `#if`/`#region` blocks spanning members.

## Support

If ef-pack saves you build time, you can [buy me a coffee](https://www.buymeacoffee.com/thepjo). Thank you!

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

Bugs and ideas: [GitHub issues](https://github.com/OliveInnovations/EfMigrationPack/issues).

## Building from source

```powershell
./build.ps1        # every package, into artifacts/packages
```

Needs the .NET 10 SDK. Release numbering and publishing are described in
[RELEASING.md](https://github.com/OliveInnovations/EfMigrationPack/blob/main/RELEASING.md).

## License

[MIT](https://github.com/OliveInnovations/EfMigrationPack/blob/main/LICENSE)
