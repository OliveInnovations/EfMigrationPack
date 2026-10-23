# Releasing

## Version numbers

Every release has a release number `minor.patch` (for example `1.0`). It becomes:

| Package | Version for release `1.0` |
|---|---|
| `EfMigrationPack` for EF Core 6 / 7 / 8 / 9 / 10 | `6.1.0` / `7.1.0` / `8.1.0` / `9.1.0` / `10.1.0` |
| `dotnet-ef-pack` | `1.1.0` |
| `EfMigrationPack.Tools` | `1.1.0` |

The runtime package's major always matches the EF Core major it is built against, and each build only accepts
that major (`Microsoft.EntityFrameworkCore.Relational [8.0.0, 9.0.0)` for 8.x), the same convention database
providers use. Local builds end in `-dev`.

## One-off nuget.org setup

There is nothing to create per package on nuget.org: a package ID is registered by the first push of it, and
belongs to whichever account or organization the push key was issued for.

1. **Organization.** Sign in to nuget.org with your personal (Microsoft) account, then create an organization for the
   company from your account menu (**Add new organization**). Your account becomes its administrator. Packages owned
   by the organization show it as the owner, and other people can be added or removed without handing over a
   personal login.
2. **Trusted publishing policy.** In the account menu, open **Trusted Publishing** and add a policy:
   - Owner: the organization
   - Repository owner and repository: the GitHub owner and repository name of this repo
   - Workflow file: `release.yml`
   - Environment: leave empty (the workflow doesn't use one)

   This lets the `Release` workflow obtain a one-hour push key for packages owned by the organization, so no
   long-lived API key is stored in GitHub. For a private repository the policy is provisional until the first
   successful publish (seven days), so publish soon after creating it.
3. **GitHub secret.** Add the Actions secret `NUGET_USER` containing your nuget.org **profile name** (not your
   email). It must be a member of the organization.
4. **Optional: reserve the ID prefix.** Ask nuget.org (account@nuget.org) to reserve `EfMigrationPack` for the
   organization. Reserved packages get a verified tick, and nobody else can publish `EfMigrationPack.*` IDs.
   `dotnet-ef-pack` doesn't share the prefix, so it is protected only by being registered first.

## Publishing a release

Tag the commit and push the tag:

```
git tag v1.0
git push origin v1.0
```

The `Release` workflow builds every package with that release number, pushes them (and their symbol packages) to
nuget.org, and creates a GitHub release with the packages attached. New versions take a few minutes to be
validated and indexed before they appear in search.

## Package pages on nuget.org

| What nuget.org shows | Where it comes from |
|---|---|
| Readme on the package page | `docs/nuget/<PackageId>.md` (wired up in `Directory.Build.targets`) |
| Summary in search results | `<Description>` in each `.csproj` |
| Authors, licence, project and source links, tags | `Directory.Build.props` |
| Owner | The organization that published it |
| Icon | Not set yet: add a 128×128 PNG and `<PackageIcon>` to use one |

nuget.org renders a subset of Markdown: relative links don't work (use full GitHub URLs), and images only load from
allowed hosts such as `img.shields.io`, which is why the badges use it.

## Adding a new EF Core major

1. In `src/EfMigrationPack/EfMigrationPack.csproj`, map the new major to its target framework.
2. Add it to `$EfMajors` in `build.ps1`.
3. Check `MigrationsAssembly`'s constructor and `DbCommandInterceptor`'s signatures haven't changed; if they
   have, use `#if EFCORE11` (the build defines `EFCORE{major}`).

## Building locally

```powershell
./build.ps1                    # all packages, -dev versions, into artifacts/packages
./build.ps1 -EfMajors 8        # just the EF Core 8 runtime (plus the tools)
```
