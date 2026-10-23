using Microsoft.CodeAnalysis.CSharp;

namespace EfMigrationPack;

/// <summary>
/// unpack: restores a pack's original migration files from its zip and removes the pack.
/// Packs are unpacked newest first. Unpacking an older pack while a newer one remains would give its
/// migrations real timestamps that sort after the newer pack, so an empty database would run them in
/// the wrong order.
/// </summary>
internal static class Unpacker
{
    private sealed class Plan
    {
        public Plan(ScannedClass pack, string zipPath, ArchiveContents archive)
        {
            Pack = pack;
            ZipPath = zipPath;
            Archive = archive;
        }

        public ScannedClass Pack { get; }

        public string ZipPath { get; }

        public ArchiveContents Archive { get; }

        /// <summary>Project-relative paths to write back.</summary>
        public List<string> Restore { get; } = new();

        /// <summary>The pack's own files, to delete.</summary>
        public List<string> PackFiles { get; } = new();
    }

    public static int Run(Options o)
    {
        var warnings = new List<string>();
        var classes = Scanner.Index(Scanner.LoadProject(o.ProjectDir));
        var set = MigrationSet.Select(classes, o.Context, warnings);

        if (set.Packs.Count == 0)
        {
            throw new PackException($"No packed migrations found for {set.ContextName}.");
        }

        var targets = o.All
            ? set.Packs.AsEnumerable().Reverse().ToList()
            : new List<ScannedClass> { set.Packs[set.Packs.Count - 1] };

        // Ids that must not reappear: loose migrations, and steps of packs that stay packed.
        var remaining = new HashSet<string>(set.Loose.Select(m => m.Id!), StringComparer.Ordinal);
        foreach (var pack in set.Packs.Except(targets))
        {
            remaining.UnionWith(pack.PackedSteps().Keys);
        }

        var plans = targets.Select(pack => BuildPlan(pack, o, remaining, warnings)).ToList();
        var keep = Path.GetDirectoryName(set.SnapshotModel().Part.File.Path)!;

        foreach (var plan in plans)
        {
            Console.WriteLine($"Unpacking {plan.Pack.ClassName} ({plan.Pack.Id}): {plan.Archive.Manifest.Steps.Count} migration(s), " +
                              $"restoring {plan.Restore.Count} file(s) from {Packer.Display(o, plan.ZipPath)}");
        }

        Packer.PrintWarnings(warnings);

        if (o.DryRun)
        {
            foreach (var plan in plans)
            {
                foreach (var path in plan.PackFiles)
                {
                    Console.WriteLine($"  - {Packer.Display(o, path)}");
                }

                Console.WriteLine($"  - {Packer.Display(o, plan.ZipPath)}");
                foreach (var relative in plan.Restore)
                {
                    Console.WriteLine($"  + {relative}");
                }
            }

            Console.WriteLine("Dry run: nothing was changed.");
            return 0;
        }

        foreach (var plan in plans)
        {
            Apply(plan, o.ProjectDir, keep);
            Console.WriteLine($"Unpacked {plan.Pack.ClassName}.");
        }

        Console.WriteLine("Databases that applied a pack keep its history row; the restored migrations are already recorded there by id, so EF treats them as applied.");
        if (set.Packs.Count == targets.Count)
        {
            Console.WriteLine("No packs remain. The EfMigrationPack package and UsePackedMigrations() can stay or be removed.");
        }

        return 0;
    }

    private static Plan BuildPlan(ScannedClass pack, Options o, HashSet<string> remaining, List<string> warnings)
    {
        var problems = new List<string>();

        var zipPath = pack.Files
            .Select(f => Path.Combine(Path.GetDirectoryName(f.Path)!, pack.ClassName + ".zip"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists)
            ?? throw new PackException($"{pack.ClassName}.zip was not found next to {pack.ClassName}; its original migrations cannot be restored.");

        var archive = PackArchive.Read(zipPath);
        if (archive.Manifest.PackId != pack.Id)
        {
            throw new PackException($"{zipPath} belongs to {archive.Manifest.PackId}, not {pack.Id}.");
        }

        var plan = new Plan(pack, zipPath, archive);

        // The pack's steps and the archive must describe the same migrations.
        var current = pack.PackedSteps();
        var archivedFiles = archive.Files
            .Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => Scanner.Parse(f.Key, Code.Decode(f.Value.Bytes)))
            .ToList();
        var archivedMigrations = Scanner.Index(archivedFiles).Values
            .Where(c => c.Id != null)
            .ToDictionary(c => c.Id!, StringComparer.Ordinal);

        foreach (var id in current.Keys.Where(id => !archivedMigrations.ContainsKey(id)))
        {
            problems.Add($"step {id} has no original in the zip; unpacking would lose it.");
        }

        foreach (var id in archivedMigrations.Keys.Where(id => !current.ContainsKey(id)))
        {
            problems.Add($"the zip contains {id}, which is not a step of the pack.");
        }

        // Rebuild each step from its original; a difference means the pack was edited after packing.
        foreach (var migration in archivedMigrations.Values)
        {
            if (!current.TryGetValue(migration.Id!, out var existing))
            {
                continue;
            }

            var rebuilt = SyntaxFactory.ParseMemberDeclaration(StepBuilder.Build(migration, new List<string>()).ClassText);
            if (rebuilt == null || !SyntaxFactory.AreEquivalent(rebuilt, existing, topLevel: false))
            {
                problems.Add($"step {migration.Id} was edited after packing; unpacking restores the original and discards that edit.");
            }
        }

        foreach (var id in archivedMigrations.Keys.Where(remaining.Contains))
        {
            problems.Add($"{id} already exists as a migration or in another pack.");
        }

        foreach (var file in archive.Manifest.Files)
        {
            var target = PackArchive.Resolve(o.ProjectDir, file.Path);
            if (File.Exists(target))
            {
                if (PackArchive.Hash(File.ReadAllBytes(target)) == file.Sha256)
                {
                    warnings.Add($"{file.Path} already exists with identical content; leaving it.");
                    continue;
                }

                problems.Add($"{file.Path} already exists with different content.");
            }

            plan.Restore.Add(file.Path);
        }

        var packKey = new HashSet<string>(StringComparer.Ordinal) { pack.Key };
        foreach (var file in pack.Files)
        {
            Guard.OnlyDeclares(file, packKey);
            plan.PackFiles.Add(file.Path);
        }

        if (problems.Count > 0)
        {
            if (!o.Force)
            {
                throw new PackException(
                    $"Cannot unpack {pack.ClassName}:{Environment.NewLine}  " +
                    string.Join(Environment.NewLine + "  ", problems) +
                    $"{Environment.NewLine}Fix these, or use --force (-Force) to unpack anyway.");
            }

            warnings.AddRange(problems.Select(p => $"{pack.ClassName}: {p} (forced)"));
        }

        return plan;
    }

    private static void Apply(Plan plan, string projectDir, string keep)
    {
        foreach (var relative in plan.Restore)
        {
            var archived = plan.Archive.Files[relative];
            var target = PackArchive.Resolve(projectDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, archived.Bytes);
            File.SetLastWriteTime(target, archived.LastWriteTime);
        }

        foreach (var path in plan.PackFiles)
        {
            File.Delete(path);
        }

        File.Delete(plan.ZipPath);
        FileOps.RemoveEmptyDirectories(plan.PackFiles, projectDir, keep);
    }
}
