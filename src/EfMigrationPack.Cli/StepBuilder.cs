using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfMigrationPack;

/// <summary>An original migration converted into a nested PackedStep class.</summary>
internal sealed class Step
{
    public Step(string id, string nestedName, string ns, IReadOnlyList<string> outerUsings, IReadOnlyList<string> innerUsings, string classText)
    {
        Id = id;
        NestedName = nestedName;
        Namespace = ns;
        OuterUsings = outerUsings;
        InnerUsings = innerUsings;
        ClassText = classText;
    }

    public string Id { get; }

    public string NestedName { get; }

    /// <summary>The original migration's namespace.</summary>
    public string Namespace { get; }

    /// <summary>File-level usings of the original files.</summary>
    public IReadOnlyList<string> OuterUsings { get; }

    /// <summary>Usings declared inside the original namespace (they resolve relative to it).</summary>
    public IReadOnlyList<string> InnerUsings { get; }

    public string ClassText { get; }
}

internal static class StepBuilder
{
    // Members of Migration that a PackedStep does not provide.
    private static readonly string[] BaseMemberNames = { "TargetModel", "UpOperations", "DownOperations" };

    /// <summary>
    /// Builds the nested step class. Deterministic: unpack rebuilds it from the archived originals and
    /// compares it with the pack to detect edits made after packing.
    /// </summary>
    public static Step Build(ScannedClass migration, List<string> warnings)
    {
        var id = migration.Id!;
        var nested = NestedName(id);

        // Order parts by path so pack (absolute paths) and unpack (relative paths) agree.
        var parts = migration.Parts.OrderBy(p => Code.NormalizePath(p.File.Path), StringComparer.Ordinal).ToList();
        var members = parts
            .SelectMany(p => p.Class.Members)
            .Where(m => !(m is MethodDeclarationSyntax method && method.Identifier.ValueText == "BuildTargetModel"))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("        [PackedStep(").Append(Code.Literal(id)).AppendLine(")]");
        sb.Append("        private class ").Append(nested).AppendLine(" : PackedStep");
        sb.AppendLine("        {");
        foreach (var member in members)
        {
            sb.Append(Rewrite(member, nested).ToFullString());
        }

        Code.EnsureNewline(sb);
        sb.AppendLine("        }");

        Warn(migration, id, nested, members, warnings);

        var outer = parts
            .SelectMany(p => p.File.Root.Usings)
            .Where(Code.IsNotGlobal)
            .Select(u => u.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var inner = parts
            .SelectMany(p => p.Class.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings))
            .Where(Code.IsNotGlobal)
            .Select(u => u.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new Step(id, nested, migration.Namespace, outer, inner, sb.ToString());
    }

    public static string NestedName(string id) => "M" + Regex.Replace(id, "[^A-Za-z0-9_]", "_");

    private static void Warn(ScannedClass migration, string id, string nested, List<MemberDeclarationSyntax> members, List<string> warnings)
    {
        var identifiers = new HashSet<string>(
            members.SelectMany(m => m.DescendantNodes().OfType<IdentifierNameSyntax>()).Select(i => i.Identifier.ValueText),
            StringComparer.Ordinal);
        var declared = new HashSet<string>(
            members.Select(m => m switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                _ => string.Empty,
            }),
            StringComparer.Ordinal);

        foreach (var name in BaseMemberNames.Where(n => identifiers.Contains(n) || declared.Contains(n)))
        {
            warnings.Add($"{id}: uses Migration.{name}, which a PackedStep does not have; the build will fail until it is rewritten.");
        }

        if (identifiers.Contains(migration.ClassName))
        {
            warnings.Add($"{id}: refers to its own class name '{migration.ClassName}', which is now '{nested}'.");
        }

        if (!members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "Down"))
        {
            warnings.Add($"{id}: has no Down; reverting it will throw, as it did before.");
        }

        foreach (var member in members)
        {
            var directives = member.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsDirective).ToList();
            var ifs = directives.Count(t => t.IsKind(SyntaxKind.IfDirectiveTrivia));
            var endIfs = directives.Count(t => t.IsKind(SyntaxKind.EndIfDirectiveTrivia));
            var regions = directives.Count(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia));
            var endRegions = directives.Count(t => t.IsKind(SyntaxKind.EndRegionDirectiveTrivia));
            if (ifs != endIfs || regions != endRegions)
            {
                warnings.Add($"{id}: has #if/#region directives that span members; check the generated step compiles.");
                break;
            }
        }
    }

    private static MemberDeclarationSyntax Rewrite(MemberDeclarationSyntax member, string nestedName) => member switch
    {
        MethodDeclarationSyntax { Identifier.ValueText: "Up" or "Down" } method when method.Modifiers.Any(SyntaxKind.OverrideKeyword)
            => method.WithModifiers(MakePublic(method.Modifiers)),
        ConstructorDeclarationSyntax ctor
            => ctor.WithIdentifier(SyntaxFactory.Identifier(ctor.Identifier.LeadingTrivia, nestedName, ctor.Identifier.TrailingTrivia)),
        _ => member,
    };

    // "protected override" -> "public override", keeping the leading trivia (indentation, comments).
    private static SyntaxTokenList MakePublic(SyntaxTokenList modifiers)
    {
        var tokens = new List<SyntaxToken>
        {
            SyntaxFactory.Token(modifiers[0].LeadingTrivia, SyntaxKind.PublicKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space)),
        };

        tokens.AddRange(modifiers
            .Where(t => !t.IsKind(SyntaxKind.PublicKeyword)
                        && !t.IsKind(SyntaxKind.ProtectedKeyword)
                        && !t.IsKind(SyntaxKind.InternalKeyword)
                        && !t.IsKind(SyntaxKind.PrivateKeyword))
            .Select(t => t.WithLeadingTrivia()));

        return SyntaxFactory.TokenList(tokens);
    }
}
