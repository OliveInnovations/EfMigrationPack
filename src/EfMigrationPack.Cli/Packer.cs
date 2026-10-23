using System.Globalization;
using System.Text.RegularExpressions;

namespace EfMigrationPack;

/// <summary>Pack ids are a sequence padded like a timestamp, so every pack sorts before every real migration.</summary>
internal static class PackNames
{
    public const string Prefix = "PackedMigrations";

    public static string ClassName(int sequence) => Prefix + sequence.ToString("D4", CultureInfo.InvariantCulture);

    public static string Id(int sequence) => sequence.ToString("D14", CultureInfo.InvariantCulture) + "_" + ClassName(sequence);
}

/// <summary>pack: packs every migration not already packed into a new pack and archives the originals.</summary>
internal static class Packer
{
    public static int Run(Options o)
    {
        var warnings = new List<string>();
        var classes = Scanner.Index(Scanner.LoadProject(o.ProjectDir));
        var set = MigrationSet.Select(classes, o.Context, warnings);

        if (set.Unsupported.Count > 0)
        {
            // Leaving them behind would change the order migrations run in on an empty database.
            throw new PackException(
                "These migrations do not derive directly from Migration and cannot be packed safely: " +
                string.Join(", ", set.Unsupported.Select(c => $"{c.Id} ({c.Key})")));
        }

        if (set.Loose.Count == 0)
        {
            PrintWarnings(warnings);
            Console.WriteLine($"No unpacked migrations for {set.ContextName}; nothing to pack.");
            return 0;
        }

        var duplicate = set.Loose.GroupBy(m => m.Id!, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new PackException($"Migration id {duplicate.Key} is declared by more than one class: {string.Join(", ", duplicate.Select(c => c.Key))}.");
        }

        var packedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in set.Packs)
        {
            foreach (var id in pack.PackedSteps().Keys)
            {
                packedBy[id] = pack.ClassName;
            }
        }

        var repacked = set.Loose.Where(m => packedBy.ContainsKey(m.Id!)).Select(m => $"{m.Id} (in {packedBy[m.Id!]})").ToList();
        if (repacked.Count > 0)
        {
            throw new PackException("These migrations are already packed; did a merge restore their original files? " + string.Join(", ", repacked));
        }

        var sequence = set.Packs.Count == 0 ? 1 : set.Packs.Max(p => p.PackSequence) + 1;
        var name = PackNames.ClassName(sequence);
        var packId = PackNames.Id(sequence);
        if (string.CompareOrdinal(packId, set.Loose[0].Id) >= 0)
        {
            throw new PackException($"Migration {set.Loose[0].Id} does not sort after the new pack id {packId}.");
        }

        var packKey = Code.Qualify(set.Namespace, name);
        if (classes.ContainsKey(packKey))
        {
            throw new PackException($"A class named {packKey} already exists.");
        }

        var (snapshotPart, buildModel) = set.SnapshotModel();
        var contextAttribute = set.Snapshot.Attributes.First(a => Code.AttrName(a) == "DbContext");
        var dir = Path.GetDirectoryName(snapshotPart.File.Path)!;

        var steps = set.Loose.Select(m => StepBuilder.Build(m, warnings)).ToList();

        // Every file moved into the zip must contain only the migrations being packed.
        var looseKeys = new HashSet<string>(set.Loose.Select(m => m.Key), StringComparer.Ordinal);
        var originals = set.Loose.SelectMany(m => m.Files).Distinct().OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        foreach (var file in originals)
        {
            Guard.OnlyDeclares(file, looseKeys);
            PackArchive.Relative(o.ProjectDir, file.Path);
        }

        var originalPaths = originals.Select(f => f.Path).ToList();
        var zipPath = Path.Combine(dir, name + ".zip");

        var outputs = new List<(string Path, string Text)>
        {
            (Path.Combine(dir, packId + ".cs"), CodeWriter.MainFile(set.Namespace, name, steps, o.SingleFile)),
            (Path.Combine(dir, packId + ".Designer.cs"), CodeWriter.DesignerFile(set.Namespace, name, packId, contextAttribute, snapshotPart, buildModel)),
        };

        if (o.SingleFile)
        {
            warnings.Add("--single-file merges every step's usings into one file; if two of them clash, the build will say so.");
        }
        else
        {
            outputs.AddRange(steps.Select(s => (Path.Combine(dir, name + ".Steps", s.Id + ".cs"), CodeWriter.StepFile(set.Namespace, name, s))));
        }

        RuntimeCheck.Warn(o.ProjectDir, classes, warnings);

        var existing = outputs.Select(x => x.Path).Append(zipPath).Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            throw new PackException("These files already exist: " + string.Join(", ", existing));
        }

        Console.WriteLine($"Packing {steps.Count} migration(s) for {set.ContextName} ({steps[0].Id} .. {steps[^1].Id})");
        Console.WriteLine($"  into {packKey} with migration id {packId}");
        Console.WriteLine($"  leaving {set.Packs.Count} earlier pack(s) untouched");
        Console.WriteLine($"  moving {originals.Count} file(s) into {Display(o, zipPath)}");
        PrintWarnings(warnings);

        if (o.DryRun)
        {
            foreach (var path in originalPaths)
            {
                Console.WriteLine($"  - {Display(o, path)}");
            }

            foreach (var (path, _) in outputs)
            {
                Console.WriteLine($"  + {Display(o, path)}");
            }

            Console.WriteLine($"  + {Display(o, zipPath)}");
            Console.WriteLine("Dry run: nothing was changed.");
            return 0;
        }

        var manifest = new PackManifest
        {
            PackId = packId,
            Context = set.ContextName,
            ToolVersion = typeof(PackNames).Assembly.GetName().Version?.ToString() ?? string.Empty,
            CreatedUtc = DateTime.UtcNow,
            Steps = steps.Select(s => s.Id).ToList(),
        };

        // Archive (verified) first, then write the pack, then remove the originals.
        PackArchive.Create(zipPath, o.ProjectDir, originalPaths, manifest);

        foreach (var (path, text) in outputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, Code.Utf8WithBom);
        }

        foreach (var path in originalPaths)
        {
            File.Delete(path);
        }

        FileOps.RemoveEmptyDirectories(originalPaths, o.ProjectDir, keep: dir);

        Console.WriteLine($"Done: {name} created.");
        Console.WriteLine("Build, then compare the migration script before and after (see README) before committing.");
        return 0;
    }

    internal static void PrintWarnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            Console.WriteLine($"warning: {warning}");
        }
    }

    internal static string Display(Options o, string path) => Path.GetRelativePath(o.ProjectDir, path);
}

/// <summary>Packed migrations need the EfMigrationPack package at build and run time; warn if it looks missing.</summary>
internal static class RuntimeCheck
{
    private static readonly Regex PackageReference = new(
        @"<PackageReference\b[^>]*\bInclude\s*=\s*""EfMigrationPack""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] SharedBuildFiles =
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
    };

    public static void Warn(string projectDir, IReadOnlyDictionary<string, ScannedClass> classes, List<string> warnings)
    {
        // Runtime types compiled into the project, as an earlier ef-pack version did, clash with the package.
        if (classes.ContainsKey("EfMigrationPack.PackedMigrationsAssembly"))
        {
            warnings.Add("this project declares the EfMigrationPack runtime types itself (PackedMigrations.Runtime.cs); delete that file and use the EfMigrationPack package instead.");
        }

        if (!ReferencesPackage(projectDir))
        {
            warnings.Add("this project doesn't appear to reference the EfMigrationPack package. Add the version whose major matches " +
                         "your EF Core major (e.g. 8.x for EF Core 8) and call UsePackedMigrations() on the DbContext options.");
        }
    }

    private static bool ReferencesPackage(string projectDir)
    {
        var candidates = Directory.EnumerateFiles(projectDir, "*.csproj").ToList();
        for (var dir = new DirectoryInfo(projectDir); dir != null; dir = dir.Parent)
        {
            candidates.AddRange(SharedBuildFiles.Select(f => Path.Combine(dir.FullName, f)).Where(File.Exists));
        }

        return candidates.Any(file => PackageReference.IsMatch(File.ReadAllText(file)));
    }
}
