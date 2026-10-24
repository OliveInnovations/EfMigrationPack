using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfMigrationPack;

/// <summary>A parsed C# file. Paths are absolute for project files and project-relative for archived ones.</summary>
internal sealed class SourceFile
{
    public SourceFile(string path, CompilationUnitSyntax root)
    {
        Path = path;
        Root = root;
    }

    public string Path { get; }

    public CompilationUnitSyntax Root { get; }

    /// <summary>Types (and anything else) declared at file or namespace level.</summary>
    public IEnumerable<MemberDeclarationSyntax> TopLevelMembers =>
        Root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m is not BaseNamespaceDeclarationSyntax);
}

internal sealed record ClassPart(SourceFile File, ClassDeclarationSyntax Class);

/// <summary>One class, gathered from all of its partial declarations.</summary>
internal sealed class ScannedClass
{
    public ScannedClass(string key, string className, string ns)
    {
        Key = key;
        ClassName = className;
        Namespace = ns;
    }

    public string Key { get; }

    public string ClassName { get; }

    public string Namespace { get; }

    public List<ClassPart> Parts { get; } = new();

    public IReadOnlyList<SourceFile> Files => Parts.Select(p => p.File).Distinct().ToList();

    public IEnumerable<AttributeSyntax> Attributes =>
        Parts.SelectMany(p => p.Class.AttributeLists).SelectMany(l => l.Attributes);

    public IEnumerable<string> BaseTypeNames =>
        Parts.SelectMany(p => p.Class.BaseList == null
                ? Enumerable.Empty<BaseTypeSyntax>()
                : p.Class.BaseList.Types.AsEnumerable())
            .Select(t => Code.LastIdentifier(t.Type));

    public string? Id =>
        Attributes.Where(a => Code.AttrName(a) == "Migration").Select(Code.StringArg).FirstOrDefault(s => s != null);

    public string? ContextType =>
        Attributes.Where(a => Code.AttrName(a) == "DbContext")
            .Select(a => (a.ArgumentList?.Arguments.FirstOrDefault()?.Expression as TypeOfExpressionSyntax)?.Type.ToString())
            .FirstOrDefault(s => s != null);

    public bool IsSnapshot => BaseTypeNames.Contains("ModelSnapshot");

    public bool IsPacked =>
        Attributes.Any(a => Code.AttrName(a) == "PackedMigration") || BaseTypeNames.Contains("PackedMigration");

    public bool IsPlainMigration => !IsPacked && BaseTypeNames.Contains("Migration");

    /// <summary>
    /// The 14-digit prefix of a pack's id as a number: 00yyMMddHHmmss, or a plain sequence (1, 2, ...) for packs
    /// made by versions before named packs.
    /// </summary>
    public long PackNumber =>
        Id is { Length: > 15 } id
        && id[14] == '_'
        && long.TryParse(id.AsSpan(0, 14), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new PackException($"Packed migration {Key} has an unexpected id '{Id}'.");

    /// <summary>The nested [PackedStep("id")] classes of a pack, keyed by original migration id.</summary>
    public Dictionary<string, ClassDeclarationSyntax> PackedSteps()
    {
        var steps = new Dictionary<string, ClassDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var nested in Parts.SelectMany(p => p.Class.Members.OfType<ClassDeclarationSyntax>()))
        {
            var id = nested.AttributeLists
                .SelectMany(l => l.Attributes)
                .Where(a => Code.AttrName(a) == "PackedStep")
                .Select(Code.StringArg)
                .FirstOrDefault(s => s != null);

            if (id is null)
            {
                continue;
            }

            if (!steps.TryAdd(id, nested))
            {
                throw new PackException($"{Key} contains step {id} more than once.");
            }
        }

        return steps;
    }
}

internal static class Scanner
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    public static SourceFile Parse(string path, string text) =>
        new(path, CSharpSyntaxTree.ParseText(text, ParseOptions, path).GetCompilationUnitRoot());

    /// <summary>Parses the project's .cs files that could hold migrations, a snapshot, packs or the runtime.</summary>
    public static List<SourceFile> LoadProject(string projectDir)
    {
        var files = new List<SourceFile>();
        foreach (var path in EnumerateSourceFiles(projectDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path);
            if (text.Contains("Migration", StringComparison.Ordinal) || text.Contains("ModelSnapshot", StringComparison.Ordinal))
            {
                files.Add(Parse(path, text));
            }
        }

        return files;
    }

    public static Dictionary<string, ScannedClass> Index(IEnumerable<SourceFile> files)
    {
        var classes = new Dictionary<string, ScannedClass>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var cls in file.TopLevelMembers.OfType<ClassDeclarationSyntax>())
            {
                var ns = Code.NamespaceOf(cls);
                var key = Code.Qualify(ns, cls.Identifier.ValueText);
                if (!classes.TryGetValue(key, out var scanned))
                {
                    classes[key] = scanned = new ScannedClass(key, cls.Identifier.ValueText, ns);
                }

                scanned.Parts.Add(new ClassPart(file, cls));
            }
        }

        return classes;
    }

    // Skips bin/obj, hidden folders, and sub-folders that belong to another project.
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                yield return file;
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".", StringComparison.Ordinal)
                    || Directory.EnumerateFiles(sub, "*.csproj").Any())
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }
}

/// <summary>The migrations, packs and snapshot that belong to one DbContext.</summary>
internal sealed class MigrationSet
{
    private MigrationSet(string contextName, ScannedClass snapshot)
    {
        ContextName = contextName;
        Snapshot = snapshot;
    }

    public string ContextName { get; }

    public ScannedClass Snapshot { get; }

    /// <summary>Packed migrations, ordered by id.</summary>
    public List<ScannedClass> Packs { get; } = new();

    /// <summary>Ordinary migrations not yet packed, ordered by id (ordinal, as EF orders them).</summary>
    public List<ScannedClass> Loose { get; } = new();

    /// <summary>[Migration] classes that derive from something other than Migration.</summary>
    public List<ScannedClass> Unsupported { get; } = new();

    public string Namespace => Snapshot.Namespace;

    public static MigrationSet Select(IReadOnlyDictionary<string, ScannedClass> classes, string? context, List<string> warnings)
    {
        var candidates = classes.Values.Where(c => c.Id != null || c.IsSnapshot).ToList();

        foreach (var orphan in candidates.Where(c => c.Id != null && c.ContextType == null))
        {
            warnings.Add($"{orphan.Key} has [Migration] but no [DbContext]; EF ignores it, and so does ef-pack.");
        }

        var withContext = candidates.Where(c => c.ContextType != null).ToList();
        string chosen;
        if (context != null)
        {
            chosen = context;
        }
        else
        {
            var names = withContext.Select(c => SimpleName(c.ContextType!)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count == 0)
            {
                throw new PackException("No migrations or model snapshot found in this project.");
            }

            if (names.Count > 1)
            {
                throw new PackException($"Migrations for several DbContexts found ({string.Join(", ", names)}). Specify the context.");
            }

            chosen = names[0];
        }

        var mine = withContext.Where(c => ContextMatches(c.ContextType!, chosen)).ToList();
        var snapshots = mine.Where(c => c.IsSnapshot).ToList();
        if (snapshots.Count == 0)
        {
            throw new PackException($"No ModelSnapshot found for {chosen}.");
        }

        if (snapshots.Count > 1)
        {
            throw new PackException($"Several ModelSnapshots found for {chosen}: {string.Join(", ", snapshots.Select(s => s.Key))}.");
        }

        var set = new MigrationSet(SimpleName(chosen), snapshots[0]);
        foreach (var migration in mine.Where(c => c.Id != null).OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (migration.IsPacked)
            {
                set.Packs.Add(migration);
            }
            else if (migration.IsPlainMigration)
            {
                set.Loose.Add(migration);
            }
            else
            {
                set.Unsupported.Add(migration);
            }
        }

        return set;
    }

    /// <summary>The snapshot part holding BuildModel; its folder is where packs are written.</summary>
    public (ClassPart Part, MethodDeclarationSyntax BuildModel) SnapshotModel()
    {
        foreach (var part in Snapshot.Parts)
        {
            var method = part.Class.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == "BuildModel");
            if (method != null)
            {
                return (part, method);
            }
        }

        throw new PackException($"{Snapshot.Key} has no BuildModel method.");
    }

    private static string Strip(string type) =>
        type.StartsWith("global::", StringComparison.Ordinal) ? type.Substring("global::".Length) : type;

    private static string SimpleName(string type) => Strip(type).Split('.').Last();

    private static bool ContextMatches(string type, string wanted)
    {
        type = Strip(type);
        wanted = Strip(wanted);
        if (type == wanted)
        {
            return true;
        }

        // Compare simple names when either side is unqualified.
        return (!type.Contains('.') || !wanted.Contains('.')) && SimpleName(type) == SimpleName(wanted);
    }
}

/// <summary>Small syntax and text helpers.</summary>
internal static class Code
{
    public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string LastIdentifier(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => type.ToString(),
    };

    public static string AttrName(AttributeSyntax a)
    {
        var name = LastIdentifier(a.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;
    }

    public static string? StringArg(AttributeSyntax a) =>
        a.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    public static string NamespaceOf(SyntaxNode node) =>
        string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));

    public static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    public static bool IsNotGlobal(UsingDirectiveSyntax u) => u.GlobalKeyword.IsKind(SyntaxKind.None);

    public static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>Decodes file bytes the same way File.ReadAllText does (BOM-aware, UTF-8 default).</summary>
    public static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static void EnsureNewline(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
        {
            sb.AppendLine();
        }
    }

    public static string Describe(MemberDeclarationSyntax m) => m switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
        DelegateDeclarationSyntax d => d.Identifier.ValueText,
        _ => m.Kind().ToString(),
    };
}

internal static class Guard
{
    /// <summary>Refuses to move or delete a file that declares anything other than the given classes.</summary>
    public static void OnlyDeclares(SourceFile file, ISet<string> allowedKeys)
    {
        var foreign = file.TopLevelMembers
            .Where(m => m is not ClassDeclarationSyntax c
                        || !allowedKeys.Contains(Code.Qualify(Code.NamespaceOf(c), c.Identifier.ValueText)))
            .Select(Code.Describe)
            .ToList();

        if (foreign.Count > 0)
        {
            throw new PackException($"{file.Path} also declares {string.Join(", ", foreign)}; move it to another file first.");
        }

        if (file.Root.AttributeLists.Count > 0 || file.Root.Usings.Any(u => !Code.IsNotGlobal(u)))
        {
            throw new PackException($"{file.Path} contains assembly attributes or global usings; move them to another file first.");
        }
    }
}

internal static class FileOps
{
    /// <summary>Removes folders left empty by deleted files, never the project folder or <paramref name="keep"/>.</summary>
    public static void RemoveEmptyDirectories(IEnumerable<string> deletedFiles, string projectDir, string keep)
    {
        var dirs = deletedFiles
            .Select(Path.GetDirectoryName)
            .Where(d => d != null)
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Length);

        foreach (var start in dirs)
        {
            var dir = start;
            while (dir != null
                   && !SamePath(dir, projectDir)
                   && !SamePath(dir, keep)
                   && Directory.Exists(dir)
                   && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class PackException : Exception
{
    public PackException(string message)
        : base(message)
    {
    }
}
