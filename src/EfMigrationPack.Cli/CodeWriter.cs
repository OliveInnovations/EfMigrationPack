using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfMigrationPack;

/// <summary>Generates the packed migration's files. Output is C# 10 compatible (EF Core 6 / net6.0).</summary>
internal static class CodeWriter
{
    private const string RuntimeUsing = "using EfMigrationPack;";
    private const string MigrationsUsing = "using Microsoft.EntityFrameworkCore.Migrations;";
    private const string InfrastructureUsing = "using Microsoft.EntityFrameworkCore.Infrastructure;";

    public static string MainFile(string ns, string name, IReadOnlyList<Step> steps, bool includeSteps)
    {
        var body = new StringBuilder();
        body.AppendLine("    /// <summary>");
        body.AppendLine($"    /// Packs {steps.Count} migration(s) ({steps[0].Id} .. {steps[^1].Id}).");
        body.AppendLine("    /// Each step runs only if its original migration id is absent from the history table.");
        body.AppendLine("    /// </summary>");
        body.AppendLine($"    public partial class {name} : PackedMigration");
        body.AppendLine("    {");
        // Up lists the steps in migration-id order; Down lists them in exact reverse.
        body.AppendLine("        protected override void Up(MigrationBuilder migrationBuilder)");
        body.AppendLine("        {");
        AppendStepList(body, "ApplyUp", steps);
        body.AppendLine("        }");
        body.AppendLine();
        body.AppendLine("        protected override void Down(MigrationBuilder migrationBuilder)");
        body.AppendLine("        {");
        AppendStepList(body, "ApplyDown", steps.Reverse().ToList());
        body.AppendLine("        }");

        var outer = new List<string> { RuntimeUsing, MigrationsUsing };
        var inner = new List<string>();
        if (includeSteps)
        {
            foreach (var step in steps)
            {
                body.AppendLine();
                body.Append(step.ClassText);
                Code.EnsureNewline(body);
                outer.AddRange(step.OuterUsings);
                inner.AddRange(step.InnerUsings);
                AddNamespaceUsing(outer, step.Namespace, ns);
            }
        }

        body.AppendLine("    }");

        return FileText($"Packed migration {name}. Unpack-Migrations (dotnet ef-pack unpack) restores the originals from {name}.zip.", outer, ns, inner, body.ToString());
    }

    public static string DesignerFile(string ns, string name, string id, AttributeSyntax contextAttribute, ClassPart snapshotPart, MethodDeclarationSyntax buildModel)
    {
        var outer = new List<string> { RuntimeUsing, InfrastructureUsing, MigrationsUsing };
        outer.AddRange(snapshotPart.File.Root.Usings.Where(Code.IsNotGlobal).Select(u => u.ToString()));
        var inner = snapshotPart.Class.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .SelectMany(n => n.Usings)
            .Where(Code.IsNotGlobal)
            .Select(u => u.ToString())
            .ToList();

        // ModelSnapshot.BuildModel and Migration.BuildTargetModel have the same signature; only the name differs.
        var buildTargetModel = buildModel.WithIdentifier(
            SyntaxFactory.Identifier(buildModel.Identifier.LeadingTrivia, "BuildTargetModel", buildModel.Identifier.TrailingTrivia));

        var body = new StringBuilder();
        body.AppendLine($"    [{contextAttribute}]");
        body.AppendLine($"    [Migration({Code.Literal(id)})]");
        body.AppendLine("    [PackedMigration]");
        body.AppendLine($"    partial class {name}");
        body.AppendLine("    {");
        body.Append(buildTargetModel.ToFullString());
        Code.EnsureNewline(body);
        body.AppendLine("    }");

        return FileText($"Packed migration {name}. Target model copied from {snapshotPart.Class.Identifier.ValueText}.", outer, ns, inner, body.ToString());
    }

    public static string StepFile(string ns, string name, Step step)
    {
        var outer = new List<string> { RuntimeUsing, MigrationsUsing };
        outer.AddRange(step.OuterUsings);
        AddNamespaceUsing(outer, step.Namespace, ns);

        var body = new StringBuilder();
        body.AppendLine($"    partial class {name}");
        body.AppendLine("    {");
        body.Append(step.ClassText);
        Code.EnsureNewline(body);
        body.AppendLine("    }");

        return FileText($"Original migration {step.Id}, packed into {name}.", outer, ns, step.InnerUsings, body.ToString());
    }

    private static void AppendStepList(StringBuilder body, string method, IReadOnlyList<Step> steps)
    {
        body.AppendLine($"            {method}(");
        body.AppendLine("                migrationBuilder,");
        for (var i = 0; i < steps.Count; i++)
        {
            var separator = i == steps.Count - 1 ? ");" : ",";
            body.AppendLine($"                Step({Code.Literal(steps[i].Id)}, () => new {steps[i].NestedName}()){separator}");
        }
    }

    private static void AddNamespaceUsing(List<string> usings, string stepNamespace, string packNamespace)
    {
        if (stepNamespace.Length > 0 && stepNamespace != packNamespace)
        {
            usings.Add($"using {stepNamespace};");
        }
    }

    private static string FileText(string description, IEnumerable<string> outerUsings, string ns, IEnumerable<string> innerUsings, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"//     {description}");
        sb.AppendLine("//     Generated by ef-pack. Do not edit: changes are lost when the pack is unpacked.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable disable"); // EF scaffolds this at the top of every migration file.

        // Without a namespace there is nowhere to put the inner usings, so they join the outer ones.
        var outer = ns.Length == 0 ? outerUsings.Concat(innerUsings) : outerUsings;
        AppendUsings(sb, outer, indent: string.Empty);
        sb.AppendLine();

        if (ns.Length == 0)
        {
            sb.Append(body);
            return sb.ToString();
        }

        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        var inner = innerUsings.Distinct(StringComparer.Ordinal).ToList();
        if (inner.Count > 0)
        {
            AppendUsings(sb, inner, indent: "    ");
            sb.AppendLine();
        }

        sb.Append(body);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendUsings(StringBuilder sb, IEnumerable<string> usings, string indent)
    {
        foreach (var u in usings.Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal))
        {
            sb.Append(indent).AppendLine(u);
        }
    }
}
