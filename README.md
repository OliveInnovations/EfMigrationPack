# <img src="docs/nuget/icon.png" width="40" height="40" alt=""> EfMigrationPack

**Pack hundreds of EF Core migrations into one, without losing any of them.**

[![EfMigrationPack](https://img.shields.io/nuget/v/EfMigrationPack?label=EfMigrationPack)](https://www.nuget.org/packages/EfMigrationPack)
[![EfMigrationPack.Cli](https://img.shields.io/nuget/v/EfMigrationPack.Cli?label=EfMigrationPack.Cli)](https://www.nuget.org/packages/EfMigrationPack.Cli)
[![EfMigrationPack.Tools](https://img.shields.io/nuget/v/EfMigrationPack.Tools?label=EfMigrationPack.Tools)](https://www.nuget.org/packages/EfMigrationPack.Tools)
[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

A project with hundreds of migrations builds slowly, mostly because every `.Designer.cs` file holds a complete copy
of your model as it was at that point. EfMigrationPack folds the migrations into a single **packed migration** with one
target model, and moves the original files into a zip outside the build.

Nothing is lost. When a pack is applied, each original migration inside it runs only if the database hasn't
recorded it, and it's recorded under its own id. A developer whose database is part-way through those migrations
gets exactly the ones they're missing, and every migration is reported as it runs. You can unpack a pack back into
its original files at any time.

Works with EF Core 6, 7, 8, 9 and 10, on any relational provider.

## Why not just squash the migrations?

When an EF Core project accumulates hundreds of migrations, the usual advice is to squash or reset them: delete
them all and scaffold one fresh `InitialCreate`. That makes the build fast, but it only works if every database is
already at the latest migration, and each one still needs its `__EFMigrationsHistory` table rewritten by hand.

| | Builds fast | Databases part-way through still update | Existing databases need manual fixes | Reversible |
|---|---|---|---|---|
| Keep every migration | No | Yes | No | n/a |
| Squash / reset to a new `InitialCreate` | Yes | **No**: the new migration tries to create tables that exist | **Yes**: rewrite each history table | Only from source control |
| Hand-delete old `BuildTargetModel` bodies | Mostly | Yes | No | Only from source control |
| **EfMigrationPack** | Yes | **Yes**: each runs exactly the migrations it's missing | **No** | **Yes**: `unpack` |

Hand-deleting the target models from old `.Designer.cs` files removes most of the build time too, but it means
editing generated code, and EF Core still uses those models for some SQL generation, such as SQLite table rebuilds.

**When a plain squash is the better choice:** if you have only a few dozen migrations, or every database you care
about is already at the latest migration and you're happy to reset their history, a squash is simpler and needs no
extra package. EfMigrationPack is for teams with many developer, test and customer databases at different points in
the history, where a squash would break them.

## Packages

| Package | What it is |
|---|---|
| [`EfMigrationPack`](https://www.nuget.org/packages/EfMigrationPack) | Runtime, referenced by the project containing your migrations. **Use the version whose major matches your EF Core major** (8.x for EF Core 8). |
| [`EfMigrationPack.Cli`](https://www.nuget.org/packages/EfMigrationPack.Cli) | Command-line tool: `dotnet ef-pack`, used like `dotnet ef`. |
| [`EfMigrationPack.Tools`](https://www.nuget.org/packages/EfMigrationPack.Tools) | Package Manager Console commands `Pack-Migrations` and `Unpack-Migrations`, like `Add-Migration`. |

### Requirements

Nothing beyond what your EF Core version already needs:

| EF Core | `EfMigrationPack` version | Target framework |
|---|---|---|
| 6 | 6.x | .NET 6 or later |
| 7 | 7.x | .NET 6 or later |
| 8 | 8.x | .NET 8 or later |
| 9 | 9.x | .NET 8 or later |
| 10 | 10.x | .NET 10 or later |

`EfMigrationPack.Cli` and `EfMigrationPack.Tools` run on any installed .NET 6 or later runtime. Only building this
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
dotnet tool install --global EfMigrationPack.Cli
```

Package Manager Console in Visual Studio:

```
Install-Package EfMigrationPack.Tools
```

### 4. Pack

```
dotnet ef-pack pack Sprint10 --dry-run
dotnet ef-pack pack Sprint10
```

or in the Package Manager Console:

```
Pack-Migrations Sprint10 -WhatIf
Pack-Migrations Sprint10
```

Give each pack a name that says where it came from, such as the sprint or release it was made for, just as you
name a migration. Then build, [verify](#verify-a-pack-before-committing), and commit.

## Commands

| Command line | Package Manager Console | What it does |
|---|---|---|
| `dotnet ef-pack pack <name>` | `Pack-Migrations <Name>` | Packs every migration not already packed into a new packed migration called `<name>`, and moves the originals into `<name>.zip`. |
| `dotnet ef-pack unpack` | `Unpack-Migrations` | Restores the newest pack's original migrations from its zip and removes the pack. |

| Option (CLI / PMC) | Applies to | |
|---|---|---|
| `--project` / `-Project` | both | Project folder or `.csproj`. The PMC defaults to the console's **Default project**; the CLI to the current folder. |
| `--context` / `-Context` | both | The DbContext, when the project has migrations for more than one. |
| `--dry-run` / `-DryRun` or `-WhatIf` | both | Show what would change without changing anything. |
| `--single-file` / `-SingleFile` | pack | Put every original migration in the pack's main file instead of a `<name>.Steps` folder. |
| `--all` / `-All` | unpack | Unpack every pack, newest first. |
| `--force` / `-Force` | unpack | Unpack even if the pack was edited after packing, or a restored file would overwrite a different one. |

The commands parse your source files; they don't build the project, so they're quick.

The names from version 1.0 still work: `dotnet ef-pack compress` / `expand` and `Compress-Migrations` / `Expand-Migrations`.

## What you see when a pack is applied

Every migration in a pack is reported as it finishes, for example from `dotnet ef database update`:

```
Applying migration '00261024111000_Sprint10'.
Packed migration '00261024111000_Sprint10': 3 of 5 migration(s) to apply, 2 already applied.
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

`pack Sprint10` creates, next to your model snapshot:

- `00261024111000_Sprint10.cs`: `Up` lists every original migration in id order, `Down` lists them in reverse.
- `00261024111000_Sprint10.Designer.cs`: one target model, copied from the model snapshot.
- `Sprint10.Steps/`: each original migration as a nested class, its code unchanged except that `Up` and `Down`
  become `public`.
- `Sprint10.zip`: the original `.cs` and `.Designer.cs` files, at their project-relative paths, with a
  checksum manifest. The zip is written and verified before any original is removed.

Earlier packs are never changed; each run makes a new one.

**Why the ids look like `00261024111000_Sprint10`.** EF decides what to run purely by migration id, sorted as text,
and never runs an id a database has already recorded. A pack's id is `00` followed by the UTC time it was made as
`yyMMddHHmmss`, then its name. So:

- packs sort in the order they were made, and **before every ordinary migration**, whose ids start with the full
  year. A migration merged after a pack, even one with an older timestamp, sorts after the packs and goes into the
  next pack, so an empty database runs it in the same order existing databases did;
- an id **never repeats**, even if you unpack, change the migrations and pack again under the same name. A repeated
  id would be skipped by every database that had applied the first pack, so new migrations in it would never run.

Packs made by earlier versions, with ids like `00000000000001_PackedMigrations0001`, keep working and sort first.

**Applying a pack.** For each original migration, the pack checks the history table. If the id is missing, it runs
the migration and inserts that id; if it's present, it skips it. `Down` does the reverse. Every database therefore
keeps a history row for every original migration, which is also what lets `unpack` work without touching databases.

## Unpacking

`unpack` restores the newest pack's original files from its zip, then deletes the pack and the zip. Packs are
unpacked newest first, because an older pack's migrations would otherwise sort after a newer pack. Before changing
anything it checks the zip's checksums, that the pack and the zip describe the same migrations, that no packed
migration was edited after packing, and that nothing would be overwritten.

Databases need nothing doing afterwards: the restored migrations are already recorded there by id.

## Recommended workflow

1. When you cut a release branch, run `dotnet ef-pack pack <release or sprint name>` on a clean working tree.
2. Verify (below), commit, and merge to main.
3. Developers who sync main get one new pack instead of each feature's migrations. Feature branches keep their own
   migration files until they merge.

Only pack on one branch at a time. Two branches that pack concurrently both pack the migrations they share, and
once both are merged those migrations are in two packs; databases cope (the second pack skips what the first
applied), but unpacking reports the duplicates. Whichever merges second should unpack and pack again.

## Verify a pack before committing

1. **Script diff.** Before packing, run `dotnet ef migrations script 0 -o before.sql` with a connection string
   for a database that doesn't exist on a reachable server. Repeat after packing into `after.sql` and diff them.
   Only the extra history `INSERT`s and transaction grouping should differ.
2. **Part-way database.** Apply to a copy of a database that is part-way through the packed migrations and check its
   schema and history.
3. **Round trip.** `dotnet ef database update 0`, then `dotnet ef database update`, on a scratch database.
4. **Unpack round trip.** `dotnet ef-pack unpack`, then `git status`: the restored files should be identical.

## Limitations

- **Scripts reflect the database they're generated against.** Which migrations a pack includes is decided when EF
  generates the SQL, from the connected database's history table. An idempotent script generated against an empty
  database includes every migration in the pack. That's unsafe on a database that already has some of them, which
  includes **production at your first pack**. Deploy packs to such databases with `Migrate()` or a migration bundle,
  which decide at run time, or generate the script against a copy of the target.
- **One target model per pack.** Every packed migration's SQL is generated against the model at pack time. This
  rarely matters on SQL Server; on SQLite, operations that rebuild tables can differ. The script diff catches it.
- **One transaction per pack** on providers that use transactions, instead of one per migration.
- **No intermediate targets inside a pack.** `database update` to a migration inside a pack isn't possible; unpack
  first.
- **Packs are generated code.** Don't edit them or `migrations remove` them; unpack instead.
- The tools warn about migrations that use `Migration.TargetModel`, `UpOperations` or `DownOperations`, refer to
  their own class name, have no `Down`, or have `#if`/`#region` blocks spanning members.

## FAQ

**Why is my EF Core project slow to build with lots of migrations?**
Each migration's `.Designer.cs` file contains `BuildTargetModel`, a complete copy of your model as it was when the
migration was created. With hundreds of migrations you compile hundreds of copies of an ever-growing model. A pack
keeps one.

**How do I squash EF Core migrations without breaking existing databases?**
Pack them: `dotnet ef-pack pack <name>`. The pack replaces the migrations in the build, but each database still
applies exactly the original migrations it hasn't recorded, so nothing needs fixing by hand. See
[Why not just squash?](#why-not-just-squash-the-migrations)

**What happens to a database that's part-way through the packed migrations?**
It applies only the ones it's missing, and records each under its original id.

**Does packing change the SQL my migrations run?**
No. Each migration's `Up` and `Down` code is kept as it was. Compare `dotnet ef migrations script` before and after to
confirm: only the history inserts and transaction grouping differ. See [Limitations](#limitations) for the one
target model a pack uses.

**Can I undo a pack?**
Yes: `dotnet ef-pack unpack` (or `Unpack-Migrations`) restores the original files byte for byte. Databases need no
changes.

**Can I keep adding migrations after packing?**
Yes. New migrations are ordinary migrations; the next pack picks them up.

**Which databases does it work with?**
Any relational EF Core provider: SQL Server, PostgreSQL, MySQL, SQLite, Oracle and others. It uses only EF Core's
own migration and history-table APIs. Every release is tested end to end against SQLite on EF Core 6 to 10.

**Does it work with `Migrate()`, `dotnet ef database update` and migration bundles?**
Yes, all three. For SQL scripts, read the first item under [Limitations](#limitations).

**Which EF Core and .NET versions are supported?**
EF Core 6 to 10, on the .NET version each requires. Use the `EfMigrationPack` version whose major matches your
EF Core major.

## Support

If EfMigrationPack saves you build time, you can [buy me a coffee](https://www.buymeacoffee.com/thepjo). Thank you!

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/thepjo)

Bugs and ideas: [GitHub issues](https://github.com/OliveInnovations/EfMigrationPack/issues).

## Building from source

```powershell
./build.ps1                                   # every package, into artifacts/packages
./tests/Integration/Run-IntegrationTests.ps1  # end-to-end test against SQLite, for each EF Core major
```

Needs the .NET 10 SDK. The integration test also needs the .NET 6 and 8 runtimes, for EF Core 6–9; pass
`-EfMajors 8` (for example) to test one version. It adds migrations to a test project, packs them, checks the
generated SQL is unchanged, applies the pack to an empty and a part-way database, unpacks, packs again after adding
another migration, and checks nothing is applied twice and nothing is missed. CI runs it for every EF Core major on Linux and Windows, and every release runs it before publishing.

Release numbering and publishing are described in
[RELEASING.md](https://github.com/OliveInnovations/EfMigrationPack/blob/main/RELEASING.md).

## License

[MIT](https://github.com/OliveInnovations/EfMigrationPack/blob/main/LICENSE)
